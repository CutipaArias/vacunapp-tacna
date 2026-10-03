using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Pacientes;

/// <summary>
/// RF-16 / CU15: el administrador consulta el historial de correcciones (U) y eliminaciones (D) de dosis.
/// Lo escribe el trigger trg_DosisAplicada_Auditoria (RN-23); aquí solo se lee, a través de vw_AuditoriaDosis.
/// </summary>
static class AuditoriaEndpoints
{
    const int TopPorDefecto = 100, TopMaximo = 500;

    public static void MapAuditoria(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auditoria", async (Db db, int? top, string? operacion, string? documento) =>
        {
            var n = top ?? TopPorDefecto;
            if (n is < 1 or > TopMaximo) return Error($"El límite debe estar entre 1 y {TopMaximo}.");
            var op = string.IsNullOrWhiteSpace(operacion) ? null : operacion.Trim().ToUpperInvariant();
            if (op is not (null or "U" or "D")) return Error("La operación debe ser U (corrección) o D (eliminación).");

            var r = await db.QueryAsync(
                """
                SELECT TOP (@Top) IdAuditoria, IdDosis, Operacion, Fecha, Usuario, NumeroDocumento, Paciente,
                       CodigoVacuna, NumeroDosis, FechaAplicacionAnterior, FechaAplicacionNueva,
                       LoteAnterior, LoteNuevo, DatosAnteriores, DatosNuevos
                FROM vac.vw_AuditoriaDosis
                WHERE (@Op IS NULL OR Operacion = @Op) AND (@Doc IS NULL OR NumeroDocumento = @Doc)
                ORDER BY IdAuditoria DESC
                """,
                ("@Top", n), ("@Op", op), ("@Doc", string.IsNullOrWhiteSpace(documento) ? null : documento.Trim()));

            return Results.Ok(r[0].Select(f => new AuditoriaFila(
                (long)f["IdAuditoria"]!, (long)f["IdDosis"]!, (string)f["Operacion"]!, (DateTime)f["Fecha"]!, (string)f["Usuario"]!,
                (string?)f["NumeroDocumento"], (string?)f["Paciente"], (string?)f["CodigoVacuna"], (byte?)f["NumeroDosis"],
                Fecha(f["FechaAplicacionAnterior"]), Fecha(f["FechaAplicacionNueva"]),
                (string?)f["LoteAnterior"], (string?)f["LoteNuevo"], (string?)f["DatosAnteriores"], (string?)f["DatosNuevos"])));
        }).RequireAuthorization(Politicas.Administrador);
    }

    static DateOnly? Fecha(object? v) => v is DateTime d ? DateOnly.FromDateTime(d) : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record AuditoriaFila(
    long IdAuditoria, long IdDosis, string Operacion, DateTime Fecha, string Usuario,
    string? NumeroDocumento, string? Paciente, string? CodigoVacuna, byte? NumeroDosis,
    DateOnly? FechaAplicacionAnterior, DateOnly? FechaAplicacionNueva,
    string? LoteAnterior, string? LoteNuevo, string? DatosAnteriores, string? DatosNuevos);
