using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Vigilancia;

/// <summary>
/// RF-11 / RF-12 / CU11: alertas y pendientes por alcance (RN-22). El epidemiólogo y el administrador ven toda la
/// región; el vacunador y el jefe, el distrito de su establecimiento y, en las inasistencias, las citas de su
/// establecimiento (el paciente puede vivir en otro distrito). Una alerta fuera de alcance y una inexistente reciben
/// el mismo 403: no se revela qué ids existen. Las alertas de stock se cierran solas al reponer, no se atienden a mano.
/// </summary>
static class AlertasEndpoints
{
    const int FilasMaximas = 1000;
    static readonly string[] TiposPaciente = ["ZONA_BROTE", "DOSIS_ATRASADA", "INASISTENCIA"];
    static readonly string[] EstadosCierre = ["ATENDIDA", "DESCARTADA"];

    // Alertas de pacientes en alcance de un establecimiento: por distrito de residencia o por cita no asistida allí.
    const string EnAlcance = """
        (@Regional = 1
         OR v.Ubigeo = @UbigeoEst
         OR (v.TipoAlerta = 'INASISTENCIA' AND EXISTS (
                SELECT 1 FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
                WHERE c.IdPaciente = al.IdPaciente AND c.IdEsquema = al.IdEsquema
                  AND c.Estado = 'NO_ASISTIO' AND h.IdEstablecimiento = @Est)))
        """;

    public static void MapAlertas(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/alertas").RequireAuthorization(Politicas.ConsultaClinica);

        g.MapGet("", async (string? tipo, string? ubigeo, string? documento, int? top, ClaimsPrincipal user, Db db) =>
        {
            if (await Resolver(user, db) is not { } amb) return Results.Forbid();
            if (!amb.Regional && ubigeo is not null && ubigeo != amb.Ubigeo) return Results.Forbid();
            if (tipo is not null && !TiposPaciente.Contains(tipo)) return Error($"El tipo debe ser {string.Join(", ", TiposPaciente)}.");

            var filas = (await db.QueryAsync(
                $"""
                SELECT TOP (@Top) v.*
                FROM vac.vw_AlertasPendientes v
                JOIN vac.Alerta al ON al.IdAlerta = v.IdAlerta
                WHERE (@Tipo IS NULL OR v.TipoAlerta = @Tipo)
                  AND (@Ubigeo IS NULL OR v.Ubigeo = @Ubigeo)
                  AND (@Documento IS NULL OR v.NumeroDocumento = @Documento)
                  AND {EnAlcance}
                ORDER BY CASE v.TipoAlerta WHEN 'ZONA_BROTE' THEN 0 WHEN 'INASISTENCIA' THEN 1 ELSE 2 END, v.DiasAbierta DESC, v.Paciente
                """,
                ("@Top", Math.Min(top ?? 100, FilasMaximas)), ("@Tipo", tipo), ("@Ubigeo", ubigeo), ("@Documento", documento),
                ("@Regional", amb.Regional), ("@UbigeoEst", amb.Ubigeo), ("@Est", amb.Establecimiento)))[0];

            return Results.Ok(filas);
        });

        g.MapGet("/stock", async (ClaimsPrincipal user, Db db) =>
        {
            if (await Resolver(user, db) is not { } amb) return Results.Forbid();

            // Sin SQL Server Agent en el hosting, las alertas de vencimiento se calculan al consultar (idempotente).
            await db.ExecAsync("vac.usp_GenerarAlertasStock", ("@Dias", 30),
                ("@AlertasCreadas", new SqlParameter("@AlertasCreadas", SqlDbType.Int) { Direction = ParameterDirection.Output }));

            var filas = (await db.QueryAsync(
                """
                SELECT a.IdAlerta, a.TipoAlerta, a.FechaGeneracion, es.Nombre AS Establecimiento, v.Codigo AS Vacuna,
                       l.NumeroLote, l.FechaVencimiento, s.Cantidad, s.UmbralMinimo
                FROM vac.Alerta a
                JOIN vac.StockLote s           ON s.IdStock = a.IdStock
                JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = s.IdEstablecimiento
                JOIN vac.LoteVacuna l          ON l.IdLote = s.IdLote
                JOIN vac.Vacuna v              ON v.IdVacuna = l.IdVacuna
                WHERE a.Estado = 'PENDIENTE' AND (@Regional = 1 OR s.IdEstablecimiento = @Est)
                ORDER BY es.Nombre, a.TipoAlerta, l.FechaVencimiento, l.NumeroLote
                """, ("@Regional", amb.Regional), ("@Est", amb.Establecimiento)))[0];

            return Results.Ok(filas.Select(f => new AlertaStockRegional(
                (long)f["IdAlerta"]!, (string)f["TipoAlerta"]!, (DateTime)f["FechaGeneracion"]!, (string)f["Establecimiento"]!,
                (string)f["Vacuna"]!, (string)f["NumeroLote"]!, DateOnly.FromDateTime((DateTime)f["FechaVencimiento"]!),
                (int)f["Cantidad"]!, (int)f["UmbralMinimo"]!)));
        });

        g.MapPost("/{idAlerta:long}/atender", async (long idAlerta, AtenderAlerta d, ClaimsPrincipal user, Db db) =>
        {
            if (await Resolver(user, db) is not { } amb) return Results.Forbid();

            // Solo alertas de pacientes (las de stock no tienen paciente) y dentro del alcance del usuario, en cualquier estado:
            // si ya estaba cerrada, el procedimiento responde 50041 en lugar de un 403 que parecería una pérdida de permisos.
            var visible = (await db.QueryAsync(
                $"""
                SELECT 1 AS Ok
                FROM vac.Alerta al
                JOIN vac.Paciente p ON p.IdPaciente = al.IdPaciente
                JOIN vac.Distrito d ON d.IdDistrito = p.IdDistrito
                CROSS APPLY (SELECT d.Ubigeo AS Ubigeo, al.TipoAlerta AS TipoAlerta) v
                WHERE al.IdAlerta = @Id AND {EnAlcance}
                """,
                ("@Id", idAlerta), ("@Regional", amb.Regional), ("@UbigeoEst", amb.Ubigeo), ("@Est", amb.Establecimiento)))[0];
            if (visible.Count == 0) return Results.Forbid();

            var estado = d.Estado?.Trim().ToUpperInvariant();
            if (estado is null || !EstadosCierre.Contains(estado)) return Error("Indique ATENDIDA o DESCARTADA.");

            await db.ExecAsync("vac.usp_AtenderAlerta", ("@IdAlerta", idAlerta), ("@Estado", estado));
            return Results.Ok(new { idAlerta, estado });
        });

        var p = app.MapGroup("/api/pendientes").RequireAuthorization(Politicas.ConsultaClinica);

        p.MapGet("", async (string? ubigeo, string? vacuna, bool? soloBrote, int? top, ClaimsPrincipal user, Db db) =>
        {
            if (await Resolver(user, db) is not { } amb) return Results.Forbid();
            if (!amb.Regional && ubigeo is not null && ubigeo != amb.Ubigeo) return Results.Forbid();

            return Results.Ok((await db.ExecAsync("vac.usp_ListarPendientes",
                ("@Ubigeo", amb.Regional ? ubigeo : amb.Ubigeo), ("@CodigoVacuna", vacuna),
                ("@SoloZonaBrote", soloBrote ?? false), ("@Top", Math.Min(top ?? 100, FilasMaximas))))[0]);
        });
    }

    /// <summary>Ámbito del usuario: toda la región o el establecimiento y distrito donde trabaja. Null si no tiene ninguno.</summary>
    static async Task<Ambito?> Resolver(ClaimsPrincipal user, Db db)
    {
        if (Alcance.EsRegional(user)) return new Ambito(true, null, null);
        if (Alcance.Establecimiento(user) is not { } est) return null;

        var fila = (await db.QueryAsync(
            "SELECT d.Ubigeo FROM vac.EstablecimientoSalud es JOIN vac.Distrito d ON d.IdDistrito = es.IdDistrito WHERE es.IdEstablecimiento = @E",
            ("@E", est)))[0].FirstOrDefault();
        return fila is null ? null : new Ambito(false, est, (string)fila["Ubigeo"]!);
    }

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);

    sealed record Ambito(bool Regional, short? Establecimiento, string? Ubigeo);
}

record AtenderAlerta(string? Estado);
record AlertaStockRegional(long IdAlerta, string TipoAlerta, DateTime FechaGeneracion, string Establecimiento, string Vacuna,
                           string NumeroLote, DateOnly FechaVencimiento, int Cantidad, int UmbralMinimo);
