using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Vigilancia;

/// <summary>
/// RF-14 / CU13: el epidemiólogo y el administrador crean, listan y cierran campañas con metas de dosis por distrito.
/// Las reglas (fechas, metas, duplicados, cierre) viven en la base (usp_CrearCampana, usp_CerrarCampana); aquí solo se valida la
/// forma de la entrada. Todo el grupo exige la política Regional, así que los demás roles reciben el mismo 403 exista o no la
/// campaña que nombran. El avance sale de vw_AvanceCampana y, por vacuna, de las dosis vinculadas a cada campaña.
/// </summary>
static partial class CampanasEndpoints
{
    const int MaxDistritos = 100;

    public static void MapCampanas(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/campanas").RequireAuthorization(Politicas.Regional);

        g.MapGet("", async (Db db) =>
        {
            var r = await db.QueryAsync(
                """
                SELECT IdCampana, Nombre, Descripcion, FechaInicio, FechaFin,
                       CASE WHEN FechaInicio > CAST(GETDATE() AS DATE) THEN 'PROGRAMADA'
                            WHEN FechaFin    < CAST(GETDATE() AS DATE) THEN 'FINALIZADA'
                            ELSE 'VIGENTE' END AS Estado
                FROM vac.Campana;
                SELECT IdCampana, Distrito, MetaDosis, DosisAplicadas, PorcentajeAvance FROM vac.vw_AvanceCampana;
                SELECT da.IdCampana, v.Codigo AS Vacuna, COUNT(*) AS Dosis
                FROM vac.DosisAplicada da
                JOIN vac.EsquemaDosis e ON e.IdEsquema = da.IdEsquema
                JOIN vac.Vacuna v       ON v.IdVacuna  = e.IdVacuna
                WHERE da.IdCampana IS NOT NULL
                GROUP BY da.IdCampana, v.Codigo;
                """);

            var distritos = r[1].ToLookup(f => Convert.ToInt32(f["IdCampana"]));
            var vacunas = r[2].ToLookup(f => Convert.ToInt32(f["IdCampana"]));
            int Orden(string estado) => estado switch { "VIGENTE" => 0, "PROGRAMADA" => 1, _ => 2 };

            return Results.Ok(r[0]
                .OrderBy(f => Orden((string)f["Estado"]!)).ThenByDescending(f => (DateTime)f["FechaInicio"]!).ThenByDescending(f => Convert.ToInt32(f["IdCampana"]))
                .Select(f =>
                {
                    var id = Convert.ToInt32(f["IdCampana"]);
                    var filas = distritos[id].OrderBy(d => (string)d["Distrito"]!).ToList();
                    var meta = filas.Sum(d => Convert.ToInt64(d["MetaDosis"]));
                    var aplicadas = filas.Sum(d => Convert.ToInt64(d["DosisAplicadas"]));
                    return new CampanaFila(
                        id, (string)f["Nombre"]!, f["Descripcion"] as string, Fecha((DateTime)f["FechaInicio"]!), Fecha((DateTime)f["FechaFin"]!),
                        (string)f["Estado"]!, meta, aplicadas, meta == 0 ? 0 : Math.Round(100m * aplicadas / meta, 2),
                        filas.Select(d => new DistritoMeta((string)d["Distrito"]!, Convert.ToInt32(d["MetaDosis"]),
                                                           Convert.ToInt32(d["DosisAplicadas"]), (decimal)d["PorcentajeAvance"]!)),
                        vacunas[id].OrderByDescending(v => Convert.ToInt32(v["Dosis"])).ThenBy(v => (string)v["Vacuna"]!)
                                   .Select(v => new DosisVacuna((string)v["Vacuna"]!, Convert.ToInt32(v["Dosis"]))));
                }));
        });

        g.MapPost("", async (CrearCampana d, Db db) =>
        {
            var nombre = d.Nombre?.Trim();
            if (string.IsNullOrEmpty(nombre)) return Error("Indique el nombre de la campaña.");
            if (nombre.Length > 100) return Error("El nombre de la campaña no puede pasar de 100 caracteres.");
            var descripcion = d.Descripcion?.Trim();
            if (descripcion is { Length: > 300 }) return Error("La descripción no puede pasar de 300 caracteres.");
            if (ParsearFecha(d.FechaInicio) is not { } inicio || ParsearFecha(d.FechaFin) is not { } fin)
                return Error("Las fechas deben tener el formato AAAA-MM-DD.");
            if (d.Metas is not { Count: > 0 } metas || metas.Count > MaxDistritos)
                return Error($"Indique entre 1 y {MaxDistritos} distritos con su meta de dosis.");
            if (metas.Any(m => m is null || m.Ubigeo is not { Length: 6 } u || !UbigeoRegex().IsMatch(u)))
                return Error("Cada ubigeo debe tener 6 dígitos.");
            if (metas.Any(m => m.MetaDosis is null)) return Error("Indique la meta de dosis de cada distrito.");

            var idCampana = new SqlParameter("@IdCampana", SqlDbType.SmallInt) { Direction = ParameterDirection.Output };
            var json = JsonSerializer.Serialize(metas.Select(m => new { ubigeo = m.Ubigeo, metaDosis = m.MetaDosis }));
            await db.ExecAsync("vac.usp_CrearCampana",
                ("@Nombre", nombre), ("@FechaInicio", inicio.ToDateTime(TimeOnly.MinValue)), ("@FechaFin", fin.ToDateTime(TimeOnly.MinValue)),
                ("@Descripcion", descripcion), ("@Metas", json), ("@IdCampana", idCampana));
            return Results.Ok(new { idCampana = (short)idCampana.Value });
        });

        g.MapPost("/{idCampana:long}/cerrar", async (long idCampana, Db db) =>
        {
            // El identificador de la tabla es SMALLINT: un valor fuera de rango no puede existir.
            if (idCampana is < 1 or > short.MaxValue)
                return Results.Json(new { error = "La campaña no existe.", codigo = 50067 }, statusCode: StatusCodes.Status400BadRequest);

            var fin = new SqlParameter("@FechaFin", SqlDbType.Date) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_CerrarCampana", ("@IdCampana", (short)idCampana), ("@FechaFin", fin));
            return Results.Ok(new { idCampana, fechaFin = Fecha((DateTime)fin.Value) });
        });
    }

    static string Fecha(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static DateOnly? ParsearFecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex UbigeoRegex();
}

record CrearCampana(string? Nombre, string? Descripcion, string? FechaInicio, string? FechaFin, List<MetaDistrito>? Metas);
record MetaDistrito(string? Ubigeo, long? MetaDosis);
record CampanaFila(int IdCampana, string Nombre, string? Descripcion, string FechaInicio, string FechaFin, string Estado,
                   long MetaTotal, long DosisAplicadas, decimal PorcentajeAvance, IEnumerable<DistritoMeta> Distritos, IEnumerable<DosisVacuna> PorVacuna);
record DistritoMeta(string Distrito, int MetaDosis, int DosisAplicadas, decimal PorcentajeAvance);
record DosisVacuna(string Vacuna, int Dosis);
