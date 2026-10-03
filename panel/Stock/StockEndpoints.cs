using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Stock;

/// <summary>
/// RF-09 / CU09: el jefe de establecimiento ve el stock de su establecimiento, registra la llegada de lotes,
/// ajusta existencias contadas y revisa las alertas de stock bajo y de lotes por vencer (RN-12, RN-13).
/// Las reglas viven en la base (usp_IngresarLote, usp_AjustarStock, triggers); aquí se valida la forma de
/// la entrada y se aplica el alcance por establecimiento (RN-22).
/// </summary>
static class StockEndpoints
{
    const int CantidadMaxima = 1_000_000;

    public static void MapStock(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/stock").RequireAuthorization(Politicas.Jefe);

        g.MapGet("", async (short? idEstablecimiento, ClaimsPrincipal user, Db db) =>
        {
            var est = idEstablecimiento ?? Alcance.Establecimiento(user);
            if (est is not { } e || !Alcance.PuedeEstablecimiento(user, e)) return Results.Forbid();

            var filas = (await db.QueryAsync(
                """
                SELECT s.IdStock, v.Codigo AS Vacuna, l.NumeroLote, l.Laboratorio, l.FechaVencimiento, s.Cantidad, s.UmbralMinimo
                FROM vac.StockLote s
                JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
                JOIN vac.Vacuna v     ON v.IdVacuna = l.IdVacuna
                WHERE s.IdEstablecimiento = @Est
                ORDER BY v.Codigo, l.FechaVencimiento, l.NumeroLote
                """, ("@Est", e)))[0];

            return Results.Ok(filas.Select(f => new StockFila(
                (int)f["IdStock"]!, (string)f["Vacuna"]!, (string)f["NumeroLote"]!, (string)f["Laboratorio"]!,
                DateOnly.FromDateTime((DateTime)f["FechaVencimiento"]!), (int)f["Cantidad"]!, (int)f["UmbralMinimo"]!,
                (int)f["Cantidad"]! <= (int)f["UmbralMinimo"]!)));
        });

        g.MapPost("/ingresos", async (NuevoIngreso d, ClaimsPrincipal user, Db db) =>
        {
            var est = d.IdEstablecimiento ?? Alcance.Establecimiento(user);
            if (est is not { } e || !Alcance.PuedeEstablecimiento(user, e)) return Results.Forbid();
            if (ValidarIngreso(d) is { } error) return Error(error);

            var idStock = new SqlParameter("@IdStock", SqlDbType.Int) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_IngresarLote",
                ("@IdEstablecimiento", e), ("@CodigoVacuna", d.Vacuna!.Trim().ToUpperInvariant()),
                ("@NumeroLote", d.NumeroLote!.Trim()), ("@Laboratorio", d.Laboratorio!.Trim()),
                ("@FechaVencimiento", Fecha(d.FechaVencimiento)), ("@Cantidad", d.Cantidad),
                ("@UmbralMinimo", d.UmbralMinimo), ("@IdStock", idStock), ("@Usuario", user.Identity?.Name));
            return Results.Ok(new { idStock = (int)idStock.Value });
        });

        g.MapPost("/ajustes", async (NuevoAjuste d, ClaimsPrincipal user, Db db) =>
        {
            // Una existencia ajena y una inexistente reciben la misma respuesta: no se revela qué ids existen.
            var dueno = (await db.QueryAsync("SELECT IdEstablecimiento FROM vac.StockLote WHERE IdStock = @Id", ("@Id", d.IdStock)))[0];
            if (dueno.Count == 0 || !Alcance.PuedeEstablecimiento(user, (short)dueno[0]["IdEstablecimiento"]!)) return Results.Forbid();
            if (d.CantidadNueva is < 0 or > CantidadMaxima) return Error($"La cantidad debe estar entre 0 y {CantidadMaxima}.");
            if (string.IsNullOrWhiteSpace(d.Motivo)) return Error("Indique el motivo del ajuste.");
            if (d.Motivo.Trim().Length > 200) return Error("El motivo admite hasta 200 caracteres.");

            await db.ExecAsync("vac.usp_AjustarStock",
                ("@IdStock", d.IdStock), ("@CantidadNueva", d.CantidadNueva), ("@Motivo", d.Motivo.Trim()), ("@Usuario", user.Identity?.Name));
            return Results.Ok(new { idStock = d.IdStock, cantidad = d.CantidadNueva });
        });

        g.MapGet("/alertas", async (ClaimsPrincipal user, Db db) =>
        {
            if (Alcance.Establecimiento(user) is not { } e) return Results.Forbid();

            // Sin SQL Server Agent en el hosting, las alertas de vencimiento se calculan al consultar.
            await db.ExecAsync("vac.usp_GenerarAlertasStock", ("@Dias", 30), ("@AlertasCreadas", new SqlParameter("@AlertasCreadas", SqlDbType.Int) { Direction = ParameterDirection.Output }));

            var filas = (await db.QueryAsync(
                """
                SELECT a.IdAlerta, a.TipoAlerta, a.FechaGeneracion, v.Codigo AS Vacuna, l.NumeroLote, l.FechaVencimiento, s.Cantidad, s.UmbralMinimo
                FROM vac.Alerta a
                JOIN vac.StockLote s  ON s.IdStock = a.IdStock
                JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
                JOIN vac.Vacuna v     ON v.IdVacuna = l.IdVacuna
                WHERE a.Estado = 'PENDIENTE' AND s.IdEstablecimiento = @Est
                ORDER BY a.TipoAlerta, l.FechaVencimiento, l.NumeroLote
                """, ("@Est", e)))[0];

            return Results.Ok(filas.Select(f => new AlertaStockFila(
                (long)f["IdAlerta"]!, (string)f["TipoAlerta"]!, (DateTime)f["FechaGeneracion"]!, (string)f["Vacuna"]!,
                (string)f["NumeroLote"]!, DateOnly.FromDateTime((DateTime)f["FechaVencimiento"]!), (int)f["Cantidad"]!, (int)f["UmbralMinimo"]!)));
        });
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static string? ValidarIngreso(NuevoIngreso d)
    {
        if (string.IsNullOrWhiteSpace(d.Vacuna) || d.Vacuna.Trim().Length > 10) return "Indique la vacuna.";
        if (string.IsNullOrWhiteSpace(d.NumeroLote) || d.NumeroLote.Trim().Length > 20) return "Indique el número de lote (hasta 20 caracteres).";
        if (string.IsNullOrWhiteSpace(d.Laboratorio) || d.Laboratorio.Trim().Length > 50) return "Indique el laboratorio (hasta 50 caracteres).";
        if (Fecha(d.FechaVencimiento) is null) return "La fecha de vencimiento debe tener el formato AAAA-MM-DD.";
        if (d.Cantidad is < 1 or > CantidadMaxima) return $"La cantidad debe estar entre 1 y {CantidadMaxima}.";
        if (d.UmbralMinimo is < 0 or > CantidadMaxima) return $"El umbral debe estar entre 0 y {CantidadMaxima}.";
        return null;
    }

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record NuevoIngreso(short? IdEstablecimiento, string? Vacuna, string? NumeroLote, string? Laboratorio, string? FechaVencimiento, int Cantidad, int? UmbralMinimo);
record NuevoAjuste(int IdStock, int CantidadNueva, string? Motivo);
record StockFila(int IdStock, string Vacuna, string NumeroLote, string Laboratorio, DateOnly FechaVencimiento, int Cantidad, int UmbralMinimo, bool BajoUmbral);
record AlertaStockFila(long IdAlerta, string TipoAlerta, DateTime FechaGeneracion, string Vacuna, string NumeroLote, DateOnly FechaVencimiento, int Cantidad, int UmbralMinimo);
