using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Vigilancia;

/// <summary>
/// RF-13 / CU12 / RN-21: tablero de cobertura por distrito con semáforo y riesgo de sarampión, solo para el epidemiólogo y el
/// administrador. No calcula nada propio: la cobertura y el semáforo (ÓPTIMA ≥ 95 %, ACEPTABLE ≥ 80 %, CRÍTICA) salen de
/// <c>vac.usp_ReporteCoberturaDistrito</c> y el riesgo de <c>vac.vw_CoberturaSarampion</c>; aquí solo se unen por distrito.
/// </summary>
static class DashboardEndpoints
{
    // Solo para dibujar las líneas de referencia del gráfico: la clasificación de cada distrito la decide la base.
    const int UmbralOptima = 95, UmbralAceptable = 80;
    const string VacunaPorDefecto = "SPR";   // la meta del proyecto: sarampión con el esquema completo (2 dosis)

    public static void MapDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", async (string? vacuna, byte? dosis, Db db) =>
        {
            var codigo = string.IsNullOrWhiteSpace(vacuna) ? VacunaPorDefecto : vacuna.Trim().ToUpperInvariant();

            // Sin dosis se toma la última del esquema de esa vacuna (esquema completo).
            var esquema = (await db.QueryAsync(
                """
                SELECT v.Nombre, e.NumeroDosis, e.Descripcion
                FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
                WHERE v.Codigo = @codigo
                  AND e.NumeroDosis = ISNULL(@dosis, (SELECT MAX(x.NumeroDosis) FROM vac.EsquemaDosis x WHERE x.IdVacuna = e.IdVacuna))
                """,
                ("@codigo", codigo), ("@dosis", dosis)))[0].FirstOrDefault();
            if (esquema is null)
                return Results.Json(new { error = "Esa vacuna o esa dosis no existe en el esquema de vacunación." },
                    statusCode: StatusCodes.Status400BadRequest);
            var numeroDosis = Convert.ToInt32(esquema["NumeroDosis"]);

            var reporte = db.ExecAsync("vac.usp_ReporteCoberturaDistrito", ("@CodigoVacuna", codigo), ("@NumeroDosis", (byte)numeroDosis));
            var riesgos = db.QueryAsync("SELECT Ubigeo, NivelRiesgo, BroteActivo, CasosConfirmados FROM vac.vw_CoberturaSarampion");
            await Task.WhenAll(reporte, riesgos);
            var riesgoPorUbigeo = riesgos.Result[0].ToDictionary(f => (string)f["Ubigeo"]!);

            var distritos = reporte.Result[0].Select(f =>
            {
                riesgoPorUbigeo.TryGetValue((string)f["Ubigeo"]!, out var s);
                return new DistritoTablero(
                    (string)f["Ubigeo"]!, (string)f["Distrito"]!, (string)f["Provincia"]!,
                    Convert.ToInt32(f["Elegibles"]), Convert.ToInt32(f["Vacunados"]), (decimal)f["PorcentajeCobertura"]!,
                    (string)f["Clasificacion"]!,
                    s is null ? "BAJO" : (string)s["NivelRiesgo"]!,
                    s is not null && (bool)s["BroteActivo"]!,
                    s is null ? 0 : Convert.ToInt32(s["CasosConfirmados"]));
            }).ToList();   // el procedimiento ya los entrega de menor a mayor cobertura

            var elegibles = distritos.Sum(d => (long)d.Elegibles);
            var vacunados = distritos.Sum(d => (long)d.Vacunados);
            return Results.Ok(new
            {
                filtro = new { vacuna = codigo, nombre = (string)esquema["Nombre"]!, dosis = numeroDosis, descripcion = (string)esquema["Descripcion"]! },
                umbrales = new { optima = UmbralOptima, aceptable = UmbralAceptable, fuente = "vac.usp_ReporteCoberturaDistrito (RN-21)" },
                resumen = new
                {
                    distritos = distritos.Count,
                    optima = distritos.Count(d => d.Clasificacion == "ÓPTIMA"),
                    aceptable = distritos.Count(d => d.Clasificacion == "ACEPTABLE"),
                    critica = distritos.Count(d => d.Clasificacion == "CRÍTICA"),
                    elegibles, vacunados,
                    coberturaRegional = elegibles == 0 ? 0m : Math.Round(100m * vacunados / elegibles, 2),
                    distritosEnRiesgoAlto = distritos.Count(d => d.RiesgoSarampion == "ALTO"),
                    distritosEnRiesgoMedio = distritos.Count(d => d.RiesgoSarampion == "MEDIO"),
                },
                distritos,
            });
        }).RequireAuthorization(Politicas.Regional);
    }
}

record DistritoTablero(string Ubigeo, string Distrito, string Provincia, int Elegibles, int Vacunados, decimal Cobertura,
                       string Clasificacion, string RiesgoSarampion, bool BroteActivo, int CasosConfirmados);
