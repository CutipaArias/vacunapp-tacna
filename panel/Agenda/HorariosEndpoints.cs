using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Agenda;

/// <summary>
/// RF-08 / CU08: el jefe de establecimiento define las franjas de atención con su cupo máximo por vacuna.
/// Las reglas viven en la base (usp_CrearHorario, usp_ActualizarHorario, índice único de franja); aquí se
/// valida la forma de la entrada y se aplica el alcance por establecimiento (RN-22).
/// </summary>
static class HorariosEndpoints
{
    const int DiasPorDefecto = 30;
    const int DiasMaximos = 366;
    static readonly string[] FormatosFechaHora = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

    public static void MapHorarios(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/horarios").RequireAuthorization(Politicas.Jefe);

        g.MapGet("", async (string? desde, string? hasta, ClaimsPrincipal user, Db db) =>
        {
            if (Alcance.Establecimiento(user) is not { } est) return Results.Forbid();

            var d = Fecha(desde) ?? (string.IsNullOrEmpty(desde) ? DateOnly.FromDateTime(DateTime.Today) : null);
            if (d is not { } inicio) return Error("La fecha 'desde' debe tener el formato AAAA-MM-DD.");
            var h = Fecha(hasta) ?? (string.IsNullOrEmpty(hasta) ? inicio.AddDays(DiasPorDefecto) : null);
            if (h is not { } fin) return Error("La fecha 'hasta' debe tener el formato AAAA-MM-DD.");
            if (fin < inicio) return Error("La fecha 'hasta' no puede ser anterior a 'desde'.");
            if (fin.DayNumber - inicio.DayNumber > DiasMaximos) return Error($"El rango admite hasta {DiasMaximos} días.");

            var filas = (await db.QueryAsync(
                """
                SELECT h.IdHorario, v.Codigo AS Vacuna, h.FechaHora, h.CupoMaximo, h.Activo,
                       (SELECT COUNT(*) FROM vac.Cita c WHERE c.IdHorario = h.IdHorario AND c.Estado <> 'CANCELADA') AS Ocupados
                FROM vac.HorarioAtencion h
                JOIN vac.Vacuna v ON v.IdVacuna = h.IdVacuna
                WHERE h.IdEstablecimiento = @Est
                  AND h.FechaHora >= @Desde AND h.FechaHora < @Hasta
                ORDER BY h.FechaHora, v.Codigo
                """,
                ("@Est", est), ("@Desde", inicio.ToDateTime(TimeOnly.MinValue)), ("@Hasta", fin.AddDays(1).ToDateTime(TimeOnly.MinValue))))[0];

            return Results.Ok(filas.Select(f => new HorarioFila(
                (int)f["IdHorario"]!, (string)f["Vacuna"]!, ((DateTime)f["FechaHora"]!).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                (short)f["CupoMaximo"]!, (bool)f["Activo"]!, (int)f["Ocupados"]!)));
        });

        g.MapPost("", async (NuevoHorario d, ClaimsPrincipal user, Db db) =>
        {
            var est = d.IdEstablecimiento ?? Alcance.Establecimiento(user);
            if (est is not { } e || !Alcance.PuedeEstablecimiento(user, e)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(d.Vacuna) || d.Vacuna.Trim().Length > 10) return Error("Indique la vacuna.");
            if (!DateTime.TryParseExact(d.FechaHora, FormatosFechaHora, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fechaHora))
                return Error("La fecha y hora deben tener el formato AAAA-MM-DDTHH:MM.");
            if (d.CupoMaximo is < 1 or > CupoLimite) return Error($"El cupo debe estar entre 1 y {CupoLimite}.");

            var id = new SqlParameter("@IdHorario", SqlDbType.Int) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_CrearHorario",
                ("@IdEstablecimiento", e), ("@CodigoVacuna", d.Vacuna.Trim().ToUpperInvariant()),
                ("@FechaHora", fechaHora), ("@CupoMaximo", d.CupoMaximo), ("@IdHorario", id));
            return Results.Ok(new { idHorario = (int)id.Value });
        });

        g.MapPut("/{idHorario:int}", async (int idHorario, ActualizarHorario d, ClaimsPrincipal user, Db db) =>
        {
            // Una franja ajena y una inexistente reciben la misma respuesta: no se revela qué ids existen.
            var dueno = (await db.QueryAsync("SELECT IdEstablecimiento FROM vac.HorarioAtencion WHERE IdHorario = @Id", ("@Id", idHorario)))[0];
            if (dueno.Count == 0 || !Alcance.PuedeEstablecimiento(user, (short)dueno[0]["IdEstablecimiento"]!)) return Results.Forbid();
            if (d.CupoMaximo is < 1 or > CupoLimite) return Error($"El cupo debe estar entre 1 y {CupoLimite}.");

            await db.ExecAsync("vac.usp_ActualizarHorario", ("@IdHorario", idHorario), ("@CupoMaximo", d.CupoMaximo), ("@Activo", d.Activo));
            return Results.Ok(new { idHorario, cupoMaximo = d.CupoMaximo, activo = d.Activo });
        });
    }

    const int CupoLimite = 500;

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record NuevoHorario(short? IdEstablecimiento, string? Vacuna, string? FechaHora, int CupoMaximo);
record ActualizarHorario(int CupoMaximo, bool Activo);
record HorarioFila(int IdHorario, string Vacuna, string FechaHora, int CupoMaximo, bool Activo, int Ocupados);
