using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Agenda;

/// <summary>
/// RF-07 / CU07: el vacunador atiende las citas de su establecimiento el día de la franja. Atender registra la dosis
/// con vac.usp_RegistrarDosis (mismas validaciones y mismo descuento de stock que el registro directo) y marca la cita
/// en una sola transacción; la inasistencia (RN-18) marca NO_ASISTIO, deja una alerta de seguimiento y no toca el stock.
/// Quien aplica la dosis es siempre el vacunador de la sesión. Una cita ajena y una inexistente reciben el mismo 403.
/// </summary>
static class AtencionEndpoints
{
    const int MaxCitasDia = 500;

    public static void MapAtencion(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/citas/dia", async (string? fecha, ClaimsPrincipal user, Db db) =>
        {
            if (Alcance.Establecimiento(user) is not { } est) return Results.Forbid();
            var dia = Fecha(fecha) ?? (string.IsNullOrEmpty(fecha) ? DateOnly.FromDateTime(DateTime.Today) : null);
            if (dia is not { } d) return Error("La fecha debe tener el formato AAAA-MM-DD.");

            var citas = (await db.QueryAsync(
                $"""
                SELECT TOP ({MaxCitasDia}) c.IdCita, c.Estado, h.FechaHora, p.NumeroDocumento,
                       CONCAT(p.Nombres, ' ', p.ApellidoPaterno) AS Paciente, v.Codigo AS Vacuna, e.NumeroDosis
                FROM vac.Cita c
                JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
                JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente
                JOIN vac.EsquemaDosis e ON e.IdEsquema = c.IdEsquema
                JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
                WHERE h.IdEstablecimiento = @Est AND h.FechaHora >= @Desde AND h.FechaHora < @Hasta AND c.Estado <> 'CANCELADA'
                ORDER BY h.FechaHora, p.ApellidoPaterno
                """,
                ("@Est", est), ("@Desde", d.ToDateTime(TimeOnly.MinValue)), ("@Hasta", d.AddDays(1).ToDateTime(TimeOnly.MinValue))))[0];

            var lotes = (await db.QueryAsync(
                """
                SELECT v.Codigo AS Vacuna, l.NumeroLote, s.Cantidad
                FROM vac.StockLote s
                JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
                JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
                WHERE s.IdEstablecimiento = @Est AND s.Cantidad > 0 AND l.FechaVencimiento >= CAST(GETDATE() AS DATE)
                ORDER BY v.Codigo, l.FechaVencimiento, l.NumeroLote
                """, ("@Est", est)))[0];

            return Results.Ok(new
            {
                citas = citas.Select(c => new CitaDia(
                    (int)c["IdCita"]!, (string)c["Estado"]!, ((DateTime)c["FechaHora"]!).ToString("HH:mm", CultureInfo.InvariantCulture),
                    (string)c["NumeroDocumento"]!, (string)c["Paciente"]!, (string)c["Vacuna"]!, (byte)c["NumeroDosis"]!)),
                lotes = lotes.Select(l => new LoteDisponible((string)l["Vacuna"]!, (string)l["NumeroLote"]!, (int)l["Cantidad"]!)),
            });
        }).RequireAuthorization(Politicas.Vacunador);

        app.MapPost("/api/citas/{idCita:int}/atender", async (int idCita, AtenderCita d, ClaimsPrincipal user, Db db, UsuarioRepo usuarios) =>
        {
            var lote = d.NumeroLote?.Trim();
            if (string.IsNullOrEmpty(lote) || lote.Length > 20) return Error("Indique el número de lote (hasta 20 caracteres).");
            if (!await EsDeMiEstablecimientoAsync(db, user, idCita)) return Results.Forbid();

            var dni = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var idUsuario)
                ? await usuarios.DniVacunadorAsync(idUsuario) : null;
            if (dni is null) return Results.Forbid();

            var idDosis = new SqlParameter("@IdDosis", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_AtenderCita",
                ("@IdCita", idCita), ("@NumeroLote", lote), ("@DniVacunador", dni), ("@IdDosis", idDosis));
            return Results.Ok(new { idCita, estado = "ATENDIDA", idDosis = (long)idDosis.Value });
        }).RequireAuthorization(Politicas.Vacunador);

        app.MapPost("/api/citas/{idCita:int}/inasistencia", async (int idCita, ClaimsPrincipal user, Db db) =>
        {
            if (!await EsDeMiEstablecimientoAsync(db, user, idCita)) return Results.Forbid();

            var alerta = new SqlParameter("@AlertaCreada", SqlDbType.Bit) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_RegistrarInasistencia", ("@IdCita", idCita), ("@AlertaCreada", alerta));
            return Results.Ok(new { idCita, estado = "NO_ASISTIO", alertaCreada = (bool)alerta.Value });
        }).RequireAuthorization(Politicas.Vacunador);
    }

    // RN-22: solo citas de franjas de su establecimiento; una inexistente da el mismo resultado que una ajena.
    static async Task<bool> EsDeMiEstablecimientoAsync(Db db, ClaimsPrincipal user, int idCita)
    {
        var c = (await db.QueryAsync(
            "SELECT h.IdEstablecimiento FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario WHERE c.IdCita = @Id",
            ("@Id", idCita)))[0];
        return c.Count > 0 && Alcance.PuedeEstablecimiento(user, (short)c[0]["IdEstablecimiento"]!);
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record AtenderCita(string? NumeroLote);
record CitaDia(int IdCita, string Estado, string Hora, string Documento, string Paciente, string Vacuna, byte NumeroDosis);
record LoteDisponible(string Vacuna, string NumeroLote, int Cantidad);
