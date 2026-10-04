using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// RF-13 / CU12 / RN-21: tablero de cobertura por distrito con semáforo (ÓPTIMA ≥ 95 %, ACEPTABLE ≥ 80 %, CRÍTICA) y riesgo de
/// sarampión. Solo para epidemiólogo y administrador. Los umbrales viven en <c>vac.usp_ReporteCoberturaDistrito</c> y el riesgo
/// en <c>vac.vw_CoberturaSarampion</c>: el tablero los reutiliza, así que aquí se comprueba que no los reescribe.
/// </summary>
[Collection("api")]
public class DashboardTests(AppFactory app)
{
    static readonly string[] Semaforo = ["ÓPTIMA", "ACEPTABLE", "CRÍTICA"];
    static readonly string[] Riesgo = ["ALTO", "MEDIO", "BAJO"];

    async Task<(HttpStatusCode estado, JsonElement cuerpo)> Consultar(string usuario, string url = "/api/dashboard")
    {
        var c = await app.SesionComoAsync(usuario);
        var r = await c.GetAsync(url);
        var texto = await r.Content.ReadAsStringAsync();   // un 403 no trae cuerpo
        return (r.StatusCode, texto.Length == 0 ? default : JsonDocument.Parse(texto).RootElement);
    }

    [Theory]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task Regionales_reciben_el_tablero_con_su_forma(string usuario)
    {
        var (estado, j) = await Consultar(usuario);

        Assert.True(estado == HttpStatusCode.OK, $"{usuario}: {j}");
        Assert.Equal("SPR", j.GetProperty("filtro").GetProperty("vacuna").GetString());
        Assert.Equal(2, j.GetProperty("filtro").GetProperty("dosis").GetInt32());
        Assert.Equal(95, j.GetProperty("umbrales").GetProperty("optima").GetDecimal());
        Assert.Equal(80, j.GetProperty("umbrales").GetProperty("aceptable").GetDecimal());

        var distritos = j.GetProperty("distritos").EnumerateArray().ToList();
        Assert.NotEmpty(distritos);
        foreach (var d in distritos)
        {
            Assert.Matches(@"^\d{6}$", d.GetProperty("ubigeo").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("distrito").GetString()));
            Assert.Contains(d.GetProperty("clasificacion").GetString(), Semaforo);
            Assert.Contains(d.GetProperty("riesgoSarampion").GetString(), Riesgo);
            Assert.True(d.GetProperty("vacunados").GetInt32() <= d.GetProperty("elegibles").GetInt32());
        }

        var r = j.GetProperty("resumen");
        Assert.Equal(distritos.Count, r.GetProperty("distritos").GetInt32());
        Assert.Equal(distritos.Count,
            r.GetProperty("optima").GetInt32() + r.GetProperty("aceptable").GetInt32() + r.GetProperty("critica").GetInt32());
        Assert.Equal(distritos.Sum(d => d.GetProperty("elegibles").GetInt64()), r.GetProperty("elegibles").GetInt64());
        Assert.Equal(distritos.Sum(d => d.GetProperty("vacunados").GetInt64()), r.GetProperty("vacunados").GetInt64());
    }

    [Fact]
    public async Task Los_distritos_vienen_de_menor_a_mayor_cobertura_para_priorizar()
    {
        var (_, j) = await Consultar("epi01");

        var cob = j.GetProperty("distritos").EnumerateArray().Select(d => d.GetProperty("cobertura").GetDecimal()).ToList();
        Assert.Equal(cob.Order().ToList(), cob);
    }

    [Fact]
    public async Task El_semaforo_coincide_con_los_umbrales_de_la_base()
    {
        var (_, j) = await Consultar("epi01");

        foreach (var d in j.GetProperty("distritos").EnumerateArray())
        {
            var cob = d.GetProperty("cobertura").GetDecimal();
            var esperado = cob >= 95 ? "ÓPTIMA" : cob >= 80 ? "ACEPTABLE" : "CRÍTICA";
            Assert.True(esperado == d.GetProperty("clasificacion").GetString(), $"{d.GetProperty("distrito")} con {cob} %");
        }
    }

    [Fact]
    public async Task Cobertura_y_clasificacion_son_las_del_reporte_de_la_base()
    {
        var (_, j) = await Consultar("epi01", "/api/dashboard?vacuna=SPR&dosis=1");
        var enBase = await Filas("""
            SELECT Ubigeo, Vacunados, Elegibles, CAST(PorcentajeCobertura AS DECIMAL(5,2)) AS Cob,
                   CASE WHEN PorcentajeCobertura >= 95 THEN 'ÓPTIMA' WHEN PorcentajeCobertura >= 80 THEN 'ACEPTABLE' ELSE 'CRÍTICA' END AS Clas
            FROM vac.vw_CoberturaDistrito WHERE CodigoVacuna = 'SPR' AND NumeroDosis = 1
            """);

        var api = j.GetProperty("distritos").EnumerateArray().ToDictionary(d => d.GetProperty("ubigeo").GetString()!);
        Assert.Equal(enBase.Count, api.Count);
        foreach (var f in enBase)
        {
            var d = api[(string)f["Ubigeo"]!];
            Assert.Equal((int)f["Vacunados"]!, d.GetProperty("vacunados").GetInt32());
            Assert.Equal((int)f["Elegibles"]!, d.GetProperty("elegibles").GetInt32());
            Assert.Equal((decimal)f["Cob"]!, d.GetProperty("cobertura").GetDecimal());
            Assert.Equal((string)f["Clas"]!, d.GetProperty("clasificacion").GetString());
        }
    }

    [Fact]
    public async Task El_riesgo_de_sarampion_es_el_de_la_vista_y_marca_el_brote_activo()
    {
        var (_, j) = await Consultar("epi01");
        var vista = (await Filas("SELECT Ubigeo, NivelRiesgo, BroteActivo, CasosConfirmados FROM vac.vw_CoberturaSarampion"))
            .ToDictionary(f => (string)f["Ubigeo"]!);

        foreach (var d in j.GetProperty("distritos").EnumerateArray())
        {
            var f = vista[d.GetProperty("ubigeo").GetString()!];
            Assert.Equal((string)f["NivelRiesgo"]!, d.GetProperty("riesgoSarampion").GetString());
            Assert.Equal((bool)f["BroteActivo"]!, d.GetProperty("broteActivo").GetBoolean());
            Assert.Equal((int)f["CasosConfirmados"]!, d.GetProperty("casosConfirmados").GetInt32());
        }
    }

    [Fact]
    public async Task Otra_vacuna_y_dosis_cambian_el_filtro_y_los_datos()
    {
        var (estado, j) = await Consultar("epi01", "/api/dashboard?vacuna=BCG&dosis=1");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Equal("BCG", j.GetProperty("filtro").GetProperty("vacuna").GetString());
        Assert.Equal(1, j.GetProperty("filtro").GetProperty("dosis").GetInt32());
        Assert.NotEmpty(j.GetProperty("distritos").EnumerateArray());
    }

    [Fact]
    public async Task Filtro_vacio_equivale_a_sin_filtro()
    {
        var (estado, j) = await Consultar("epi01", "/api/dashboard?vacuna=&dosis=");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Equal("SPR", j.GetProperty("filtro").GetProperty("vacuna").GetString());
    }

    [Theory]
    [InlineData("/api/dashboard?vacuna=NOEXISTE&dosis=1")]
    [InlineData("/api/dashboard?vacuna=SPR&dosis=9")]
    [InlineData("/api/dashboard?vacuna=SPR'%20OR%201=1--&dosis=1")]
    public async Task Una_combinacion_inexistente_da_400_en_espanol_sin_detalles_internos(string url)
    {
        var (estado, j) = await Consultar("epi01", url);

        Assert.True(estado == HttpStatusCode.BadRequest, j.ToString());
        var error = j.GetProperty("error").GetString()!;
        Assert.Contains("vacuna", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", error);
        Assert.DoesNotContain("vac.", error);
    }

    [Fact]
    public async Task Una_dosis_mal_formada_da_400_no_500()
    {
        var c = await app.SesionComoAsync("epi01");   // el enlace de parámetros responde 400 con texto plano, no JSON

        var r = await c.GetAsync("/api/dashboard?vacuna=SPR&dosis=abc");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("jefe01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Los_demas_roles_reciben_403(string usuario)
    {
        var (estado, _) = await Consultar(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, estado);
    }

    [Fact]
    public async Task Sin_sesion_recibe_401()
    {
        var r = await app.CreateClient().GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Responde_en_menos_de_2_segundos_RNF01()
    {
        var c = await app.SesionComoAsync("epi01");
        (await c.GetAsync("/api/dashboard")).EnsureSuccessStatusCode();   // calienta el plan y la caché

        var reloj = Stopwatch.StartNew();
        var r = await c.GetAsync("/api/dashboard");
        reloj.Stop();

        r.EnsureSuccessStatusCode();
        Assert.True(reloj.ElapsedMilliseconds < 2000, $"{reloj.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Chart_js_se_sirve_desde_el_propio_sitio_por_la_politica_CSP()
    {
        var r = await app.CreateClient().GetAsync("/vendor/chart.umd.js");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("javascript", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Chart.js v", (await r.Content.ReadAsStringAsync())[..200]);
    }

    async Task<List<Dictionary<string, object?>>> Filas(string sql)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await using var rd = await cmd.ExecuteReaderAsync();
        var filas = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync())
            filas.Add(Enumerable.Range(0, rd.FieldCount).ToDictionary(i => rd.GetName(i), i => rd.IsDBNull(i) ? null : rd.GetValue(i)));
        return filas;
    }
}
