using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Vigilancia;

/// <summary>
/// RF-10 / CU10: el epidemiólogo y el administrador declaran y cierran brotes (RN-22). Las reglas viven en la base
/// (usp_DeclararBrote, usp_CerrarBrote, índice único UX_Brote_Activo y el trigger que genera las alertas, RN-19/20);
/// aquí solo se valida la forma de la entrada. Los errores de negocio llegan como 400 con su código.
/// </summary>
static class BrotesEndpoints
{
    public static void MapBrotes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/brotes").RequireAuthorization(Politicas.Regional);

        g.MapGet("", async (Db db) =>
        {
            var r = await db.QueryAsync(
                """
                SELECT Nombre FROM vac.Enfermedad ORDER BY Nombre;
                SELECT b.IdBrote, e.Nombre AS Enfermedad, d.Ubigeo, d.Nombre AS Distrito, b.FechaInicio, b.FechaFin, b.CasosConfirmados,
                       (SELECT COUNT(*) FROM vac.Alerta a WHERE a.IdBrote = b.IdBrote AND a.Estado = 'PENDIENTE') AS AlertasPendientes
                FROM vac.Brote b
                JOIN vac.Enfermedad e ON e.IdEnfermedad = b.IdEnfermedad
                JOIN vac.Distrito d   ON d.IdDistrito   = b.IdDistrito
                ORDER BY CASE WHEN b.FechaFin IS NULL THEN 0 ELSE 1 END, b.FechaInicio DESC, b.IdBrote DESC
                """);

            return Results.Ok(new
            {
                enfermedades = r[0].Select(f => (string)f["Nombre"]!),
                brotes = r[1].Select(f => new BroteFila(
                    (int)f["IdBrote"]!, (string)f["Enfermedad"]!, (string)f["Ubigeo"]!, (string)f["Distrito"]!,
                    ((DateTime)f["FechaInicio"]!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    f["FechaFin"] is DateTime fin ? fin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    f["FechaFin"] is null ? "ACTIVO" : "CERRADO",
                    (short)f["CasosConfirmados"]!, (int)f["AlertasPendientes"]!)),
            });
        });

        g.MapPost("", async (DeclararBrote d, Db db) =>
        {
            if (d.Ubigeo is not { Length: 6 } ubigeo || !ubigeo.All(char.IsAsciiDigit)) return Error("El ubigeo debe tener 6 dígitos.");
            if (string.IsNullOrWhiteSpace(d.Enfermedad) || d.Enfermedad.Trim().Length > 50) return Error("Indique la enfermedad.");
            if (Fecha(d.FechaInicio) is not { } inicio) return Error("La fecha de inicio debe tener el formato AAAA-MM-DD.");
            var casos = d.CasosConfirmados ?? 0;   // un valor negativo lo rechaza la base (50024) con su propio mensaje
            if (casos is < short.MinValue or > short.MaxValue) return Error($"Los casos confirmados deben estar entre 0 y {short.MaxValue}.");

            var idBrote = new SqlParameter("@IdBrote", SqlDbType.Int) { Direction = ParameterDirection.Output };
            var alertas = new SqlParameter("@AlertasGeneradas", SqlDbType.Int) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_DeclararBrote",
                ("@Ubigeo", ubigeo), ("@Enfermedad", d.Enfermedad.Trim()), ("@FechaInicio", inicio.ToDateTime(TimeOnly.MinValue)),
                ("@CasosConfirmados", (short)casos), ("@IdBrote", idBrote), ("@AlertasGeneradas", alertas));
            return Results.Ok(new { idBrote = (int)idBrote.Value, alertasGeneradas = (int)alertas.Value });
        });

        g.MapPost("/{idBrote:int}/cerrar", async (int idBrote, CerrarBrote? d, Db db) =>
        {
            DateTime? fin = null;
            if (!string.IsNullOrWhiteSpace(d?.FechaFin))
            {
                if (Fecha(d.FechaFin) is not { } f) return Error("La fecha de cierre debe tener el formato AAAA-MM-DD.");
                fin = f.ToDateTime(TimeOnly.MinValue);
            }

            // Las alertas escaladas vuelven a DOSIS_ATRASADA, las que otro brote activo cubre pasan a ese brote y el resto se descarta.
            SqlParameter Salida(string nombre) => new(nombre, SqlDbType.Int) { Direction = ParameterDirection.Output };
            var restauradas = Salida("@AlertasRestauradas");
            var descartadas = Salida("@AlertasDescartadas");
            var reasignadas = Salida("@AlertasReasignadas");
            await db.ExecAsync("vac.usp_CerrarBrote", ("@IdBrote", idBrote), ("@FechaFin", fin),
                ("@AlertasRestauradas", restauradas), ("@AlertasDescartadas", descartadas), ("@AlertasReasignadas", reasignadas));
            return Results.Ok(new
            {
                idBrote,
                alertasRestauradas = (int)restauradas.Value,
                alertasDescartadas = (int)descartadas.Value,
                alertasReasignadas = (int)reasignadas.Value,
            });
        });
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record DeclararBrote(string? Ubigeo, string? Enfermedad, string? FechaInicio, int? CasosConfirmados);
record CerrarBrote(string? FechaFin);
record BroteFila(int IdBrote, string Enfermedad, string Ubigeo, string Distrito, string FechaInicio, string? FechaFin, string Estado,
                 int CasosConfirmados, int AlertasPendientes);
