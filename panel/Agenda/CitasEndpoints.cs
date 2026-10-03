using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;
using VacunApp.Panel.Pacientes;

namespace VacunApp.Panel.Agenda;

/// <summary>
/// RF-05 / CU05: reservar una cita para la dosis que corresponde. El ciudadano reserva solo para los pacientes
/// vinculados a su usuario y en cualquier establecimiento (RN-17); vacunador y jefe, para cualquier paciente pero solo
/// en franjas de su establecimiento (RN-22). El cupo, la elegibilidad y la concurrencia los resuelve la base
/// (usp_ReservarCita, trg_Cita_Validar); aquí se valida la forma de la entrada y el alcance.
/// </summary>
static class CitasEndpoints
{
    const int DiasPorDefecto = 30;
    const int DiasMaximos = 366;
    const int MaxFranjas = 200;

    public static void MapCitas(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agenda/franjas", async (string? vacuna, short? idEstablecimiento, string? desde, string? hasta, ClaimsPrincipal user, Db db) =>
        {
            short? est = idEstablecimiento;
            if (!user.IsInRole(Roles.Ciudadano))
            {
                // El personal solo consulta su establecimiento.
                est ??= Alcance.Establecimiento(user);
                if (est is not { } e || !Alcance.PuedeEstablecimiento(user, e)) return Results.Forbid();
            }

            var d = Fecha(desde) ?? (string.IsNullOrEmpty(desde) ? DateOnly.FromDateTime(DateTime.Today) : null);
            if (d is not { } inicio) return Error("La fecha 'desde' debe tener el formato AAAA-MM-DD.");
            var h = Fecha(hasta) ?? (string.IsNullOrEmpty(hasta) ? inicio.AddDays(DiasPorDefecto) : null);
            if (h is not { } fin) return Error("La fecha 'hasta' debe tener el formato AAAA-MM-DD.");
            if (fin < inicio) return Error("La fecha 'hasta' no puede ser anterior a 'desde'.");
            if (fin.DayNumber - inicio.DayNumber > DiasMaximos) return Error($"El rango admite hasta {DiasMaximos} días.");

            var filas = (await db.QueryAsync(
                $"""
                SELECT TOP ({MaxFranjas}) h.IdHorario, h.IdEstablecimiento, es.Nombre AS Establecimiento, d.Nombre AS Distrito,
                       v.Codigo AS Vacuna, h.FechaHora, h.CupoMaximo - x.Ocupados AS Libres
                FROM vac.HorarioAtencion h
                JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = h.IdEstablecimiento
                JOIN vac.Distrito d ON d.IdDistrito = es.IdDistrito
                JOIN vac.Vacuna v ON v.IdVacuna = h.IdVacuna
                CROSS APPLY (SELECT COUNT(*) AS Ocupados FROM vac.Cita c WHERE c.IdHorario = h.IdHorario AND c.Estado <> 'CANCELADA') x
                WHERE h.Activo = 1 AND h.FechaHora > SYSDATETIME()
                  AND h.FechaHora >= @Desde AND h.FechaHora < @Hasta
                  AND h.CupoMaximo > x.Ocupados
                  AND (@Est IS NULL OR h.IdEstablecimiento = @Est)
                  AND (@Vacuna IS NULL OR v.Codigo = @Vacuna)
                ORDER BY h.FechaHora, es.Nombre
                """,
                ("@Est", est), ("@Vacuna", string.IsNullOrWhiteSpace(vacuna) ? null : vacuna.Trim().ToUpperInvariant()),
                ("@Desde", inicio.ToDateTime(TimeOnly.MinValue)), ("@Hasta", fin.AddDays(1).ToDateTime(TimeOnly.MinValue))))[0];

            return Results.Ok(filas.Select(f => new FranjaLibre(
                (int)f["IdHorario"]!, (short)f["IdEstablecimiento"]!, (string)f["Establecimiento"]!, (string)f["Distrito"]!, (string)f["Vacuna"]!,
                ((DateTime)f["FechaHora"]!).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), (int)f["Libres"]!)));
        }).RequireAuthorization(Politicas.Reserva);

        app.MapPost("/api/citas", async (NuevaCita d, ClaimsPrincipal user, Db db) =>
        {
            var documento = d.Documento?.Trim();
            if (string.IsNullOrEmpty(documento) || documento.Length > 12) return Error("Indique el documento del paciente.");
            if (string.IsNullOrWhiteSpace(d.Vacuna) || d.Vacuna.Trim().Length > 10) return Error("Indique la vacuna.");
            if (d.Dosis is < 1 or > 5) return Error("El número de dosis debe estar entre 1 y 5.");

            // El ciudadano recibe el mismo 403 para un paciente ajeno y para uno inexistente (RN-17).
            var esCiudadano = user.IsInRole(Roles.Ciudadano);
            if (esCiudadano && !await CarneEndpoints.EsHijoAsync(db, user, documento)) return Results.Forbid();

            var ids = (await db.QueryAsync(
                """
                SELECT p.IdPaciente,
                       (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
                        WHERE v.Codigo = @Vacuna AND e.NumeroDosis = @Dosis) AS IdEsquema
                FROM vac.Paciente p WHERE p.NumeroDocumento = @Doc
                """,
                ("@Doc", documento), ("@Vacuna", d.Vacuna.Trim().ToUpperInvariant()), ("@Dosis", (byte)d.Dosis)))[0];
            if (ids.Count == 0) return Error("No existe un paciente con ese documento.");
            if (ids[0]["IdEsquema"] is not short idEsquema) return Error("La dosis indicada no existe en el esquema.");

            var franja = (await db.QueryAsync("SELECT IdEstablecimiento FROM vac.HorarioAtencion WHERE IdHorario = @H", ("@H", d.IdHorario)))[0];
            if (franja.Count == 0) return Error("La franja no existe.");
            if (!esCiudadano && !Alcance.PuedeEstablecimiento(user, (short)franja[0]["IdEstablecimiento"]!)) return Results.Forbid();

            var idCita = new SqlParameter("@IdCita", SqlDbType.Int) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_ReservarCita",
                ("@IdPaciente", (int)ids[0]["IdPaciente"]!), ("@IdHorario", d.IdHorario), ("@IdEsquema", idEsquema),
                ("@IdUsuario", CarneEndpoints.IdUsuario(user)), ("@IdCita", idCita));
            return Results.Ok(new { idCita = (int)idCita.Value });
        }).RequireAuthorization(Politicas.Reserva);

        app.MapGet("/api/citas", async (string? documento, ClaimsPrincipal user, Db db) =>
        {
            var doc = documento?.Trim();
            if (string.IsNullOrEmpty(doc) || doc.Length > 12) return Error("Indique el documento del paciente.");
            if (user.IsInRole(Roles.Ciudadano) && !await CarneEndpoints.EsHijoAsync(db, user, doc)) return Results.Forbid();

            var filas = (await db.QueryAsync(
                """
                SELECT TOP (50) c.IdCita, c.Estado, v.Codigo AS Vacuna, e.NumeroDosis, es.Nombre AS Establecimiento, h.FechaHora
                FROM vac.Cita c
                JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente
                JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
                JOIN vac.EsquemaDosis e ON e.IdEsquema = c.IdEsquema
                JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
                JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = h.IdEstablecimiento
                WHERE p.NumeroDocumento = @Doc
                ORDER BY h.FechaHora DESC
                """, ("@Doc", doc)))[0];

            return Results.Ok(filas.Select(f => new CitaFila(
                (int)f["IdCita"]!, (string)f["Estado"]!, (string)f["Vacuna"]!, (byte)f["NumeroDosis"]!, (string)f["Establecimiento"]!,
                ((DateTime)f["FechaHora"]!).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture))));
        }).RequireAuthorization(Politicas.Reserva);

        app.MapPost("/api/citas/{idCita:int}/cancelar", async (int idCita, ClaimsPrincipal user, Db db) =>
        {
            if (!await PuedeGestionarAsync(db, user, idCita)) return Results.Forbid();

            await db.ExecAsync("vac.usp_CancelarCita", ("@IdCita", idCita), ("@IdUsuario", CarneEndpoints.IdUsuario(user)));
            return Results.Ok(new { idCita, estado = "CANCELADA" });
        }).RequireAuthorization(Politicas.Reserva);

        app.MapPost("/api/citas/{idCita:int}/reprogramar", async (int idCita, ReprogramarCita d, ClaimsPrincipal user, Db db) =>
        {
            if (!await PuedeGestionarAsync(db, user, idCita)) return Results.Forbid();

            var nueva = (await db.QueryAsync("SELECT IdEstablecimiento FROM vac.HorarioAtencion WHERE IdHorario = @H", ("@H", d.IdHorario)))[0];
            if (nueva.Count == 0) return Error("La franja no existe.");
            // El personal solo mueve citas dentro de su establecimiento; el ciudadano puede elegir cualquiera.
            if (!user.IsInRole(Roles.Ciudadano) && !Alcance.PuedeEstablecimiento(user, (short)nueva[0]["IdEstablecimiento"]!)) return Results.Forbid();

            await db.ExecAsync("vac.usp_ReprogramarCita", ("@IdCita", idCita), ("@IdHorarioNuevo", d.IdHorario), ("@IdUsuario", CarneEndpoints.IdUsuario(user)));
            return Results.Ok(new { idCita, idHorario = d.IdHorario, estado = "PROGRAMADA" });
        }).RequireAuthorization(Politicas.Reserva);
    }

    // Una cita ajena y una inexistente reciben la misma respuesta (403): no se revela qué citas existen.
    // Ciudadano: solo citas de sus pacientes vinculados (RN-17). Personal: solo citas de franjas de su establecimiento (RN-22).
    static async Task<bool> PuedeGestionarAsync(Db db, ClaimsPrincipal user, int idCita)
    {
        var c = (await db.QueryAsync(
            """
            SELECT p.NumeroDocumento, h.IdEstablecimiento
            FROM vac.Cita c JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
            WHERE c.IdCita = @Id
            """, ("@Id", idCita)))[0];
        if (c.Count == 0) return false;
        return user.IsInRole(Roles.Ciudadano)
            ? await CarneEndpoints.EsHijoAsync(db, user, (string)c[0]["NumeroDocumento"]!)
            : Alcance.PuedeEstablecimiento(user, (short)c[0]["IdEstablecimiento"]!);
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record NuevaCita(string? Documento, string? Vacuna, int Dosis, int IdHorario);
record FranjaLibre(int IdHorario, short IdEstablecimiento, string Establecimiento, string Distrito, string Vacuna, string FechaHora, int Libres);
record CitaFila(int IdCita, string Estado, string Vacuna, byte NumeroDosis, string Establecimiento, string FechaHora);
record ReprogramarCita(int IdHorario);
