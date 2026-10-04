using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// RF-14 / CU13: el epidemiólogo y el administrador crean, listan y cierran campañas con metas de dosis por distrito.
/// Las reglas (fechas, metas, duplicados, cierre) viven en vac.usp_CrearCampana y vac.usp_CerrarCampana; la API valida la forma.
/// Los demás roles reciben 403, exista o no la campaña que nombran: no pueden sondear qué campañas hay.
/// </summary>
[Collection("api")]
public class CampanasTests(AppFactory app) : IAsyncLifetime
{
    const string Prefijo = "Prueba T5.4";
    static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.Today);
    static string F(DateOnly d) => d.ToString("yyyy-MM-dd");

    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql($"""
        DELETE cd FROM vac.CampanaDistrito cd JOIN vac.Campana c ON c.IdCampana = cd.IdCampana WHERE c.Nombre LIKE '{Prefijo}%';
        DELETE vac.Campana WHERE Nombre LIKE '{Prefijo}%';
        """);

    static object Cuerpo(string nombre, DateOnly inicio, DateOnly fin, object? metas = null, string? descripcion = "Seguimiento") => new
    {
        nombre, fechaInicio = F(inicio), fechaFin = F(fin), descripcion,
        metas = metas ?? new[] { new { ubigeo = "230104", metaDosis = 500 }, new { ubigeo = "230401", metaDosis = 200 } },
    };

    async Task<(HttpStatusCode estado, JsonElement cuerpo)> Enviar(HttpClient c, string url, object? cuerpo)
    {
        var r = await c.PostAsJsonAsync(url, cuerpo);
        var texto = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, texto.Length == 0 || !texto.TrimStart().StartsWith('{') ? default : JsonDocument.Parse(texto).RootElement);
    }

    async Task<int> Crear(HttpClient c, string nombre, DateOnly inicio, DateOnly fin)
    {
        var (estado, j) = await Enviar(c, "/api/campanas", Cuerpo(nombre, inicio, fin));
        Assert.True(estado == HttpStatusCode.OK, j.ToString());
        return j.GetProperty("idCampana").GetInt32();
    }

    async Task<JsonElement> Listar(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/campanas");

    static JsonElement Buscar(JsonElement lista, int id) => lista.EnumerateArray().Single(x => x.GetProperty("idCampana").GetInt32() == id);

    // ---------- crear y listar ----------

    [Theory]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task Los_regionales_crean_una_campana_con_metas_por_distrito(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        var id = await Crear(c, $"{Prefijo} crear {usuario}", Hoy, Hoy.AddDays(30));

        Assert.Equal(2, Convert.ToInt32(await Sql($"SELECT COUNT(*) FROM vac.CampanaDistrito WHERE IdCampana = {id}")));
        Assert.Equal(700, Convert.ToInt32(await Sql($"SELECT SUM(MetaDosis) FROM vac.CampanaDistrito WHERE IdCampana = {id}")));
    }

    [Fact]
    public async Task La_lista_trae_el_periodo_el_estado_la_meta_y_el_avance()
    {
        var c = await app.SesionComoAsync("epi01");
        var vigente = await Crear(c, $"{Prefijo} vigente", Hoy.AddDays(-5), Hoy.AddDays(30));
        var programada = await Crear(c, $"{Prefijo} programada", Hoy.AddDays(3), Hoy.AddDays(30));
        var finalizada = await Crear(c, $"{Prefijo} finalizada", Hoy.AddDays(-30), Hoy.AddDays(-10));

        var lista = await Listar(c);

        var v = Buscar(lista, vigente);
        Assert.Equal("VIGENTE", v.GetProperty("estado").GetString());
        Assert.Equal($"{Prefijo} vigente", v.GetProperty("nombre").GetString());
        Assert.Equal("Seguimiento", v.GetProperty("descripcion").GetString());
        Assert.Equal(F(Hoy.AddDays(-5)), v.GetProperty("fechaInicio").GetString());
        Assert.Equal(F(Hoy.AddDays(30)), v.GetProperty("fechaFin").GetString());
        Assert.Equal(700, v.GetProperty("metaTotal").GetInt32());
        Assert.Equal(0, v.GetProperty("dosisAplicadas").GetInt32());
        Assert.Equal(0m, v.GetProperty("porcentajeAvance").GetDecimal());
        Assert.Equal(2, v.GetProperty("distritos").GetArrayLength());
        Assert.Equal(0, v.GetProperty("porVacuna").GetArrayLength());
        Assert.Equal("PROGRAMADA", Buscar(lista, programada).GetProperty("estado").GetString());
        Assert.Equal("FINALIZADA", Buscar(lista, finalizada).GetProperty("estado").GetString());
    }

    [Fact]
    public async Task El_avance_coincide_con_la_vista_y_con_las_dosis_vinculadas()
    {
        var c = await app.SesionComoAsync("epi01");
        var lista = await Listar(c);
        Assert.NotEmpty(lista.EnumerateArray());   // la semilla trae campañas

        foreach (var camp in lista.EnumerateArray().Where(x => !x.GetProperty("nombre").GetString()!.StartsWith(Prefijo)))
        {
            var id = camp.GetProperty("idCampana").GetInt32();
            var enVista = Convert.ToInt32(await Sql($"SELECT ISNULL(SUM(DosisAplicadas), 0) FROM vac.vw_AvanceCampana WHERE IdCampana = {id}"));
            var metaVista = Convert.ToInt32(await Sql($"SELECT ISNULL(SUM(MetaDosis), 0) FROM vac.vw_AvanceCampana WHERE IdCampana = {id}"));
            Assert.Equal(enVista, camp.GetProperty("dosisAplicadas").GetInt32());
            Assert.Equal(metaVista, camp.GetProperty("metaTotal").GetInt32());
            Assert.Equal(camp.GetProperty("distritos").EnumerateArray().Sum(d => d.GetProperty("dosisAplicadas").GetInt32()),
                         camp.GetProperty("dosisAplicadas").GetInt32());
            var porVacuna = Convert.ToInt32(await Sql($"SELECT COUNT(*) FROM vac.DosisAplicada WHERE IdCampana = {id}"));
            Assert.Equal(porVacuna, camp.GetProperty("porVacuna").EnumerateArray().Sum(x => x.GetProperty("dosis").GetInt32()));
            if (metaVista > 0)
                Assert.Equal(Math.Round(100m * enVista / metaVista, 2), camp.GetProperty("porcentajeAvance").GetDecimal());
        }
    }

    [Fact]
    public async Task Una_campana_duplicada_se_rechaza_y_no_se_crea_dos_veces()
    {
        var c = await app.SesionComoAsync("epi01");
        await Crear(c, $"{Prefijo} duplicada", Hoy, Hoy.AddDays(10));

        var (estado, j) = await Enviar(c, "/api/campanas", Cuerpo($"{Prefijo} duplicada", Hoy, Hoy.AddDays(10)));

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Equal(50062, j.GetProperty("codigo").GetInt32());
        Assert.Equal(1, Convert.ToInt32(await Sql($"SELECT COUNT(*) FROM vac.Campana WHERE Nombre = '{Prefijo} duplicada'")));
    }

    // ---------- validación: 400 en español, sin detalles internos ----------

    public static IEnumerable<object[]> CuerposInvalidos()
    {
        var ok = new[] { new { ubigeo = "230104", metaDosis = 500 } };
        yield return ["nombre en blanco", new { nombre = "  ", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = ok }];
        yield return ["nombre demasiado largo", new { nombre = Prefijo + new string('x', 100), fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = ok }];
        yield return ["descripcion demasiado larga", new { nombre = $"{Prefijo} d", descripcion = new string('x', 301), fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = ok }];
        yield return ["fecha mal formada", new { nombre = $"{Prefijo} f", fechaInicio = "03/10/2026", fechaFin = F(Hoy.AddDays(5)), metas = ok }];
        yield return ["fin antes del inicio", new { nombre = $"{Prefijo} fi", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(-1)), metas = ok }];
        yield return ["sin metas", new { nombre = $"{Prefijo} m", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)) }];
        yield return ["metas vacias", new { nombre = $"{Prefijo} mv", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = Array.Empty<object>() }];
        yield return ["ubigeo corto", new { nombre = $"{Prefijo} u", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "2301", metaDosis = 5 } } }];
        yield return ["ubigeo inexistente", new { nombre = $"{Prefijo} ui", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "999999", metaDosis = 5 } } }];
        yield return ["meta cero", new { nombre = $"{Prefijo} m0", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "230104", metaDosis = 0 } } }];
        yield return ["meta negativa", new { nombre = $"{Prefijo} mn", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "230104", metaDosis = -3 } } }];
        yield return ["meta excesiva", new { nombre = $"{Prefijo} me", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "230104", metaDosis = 2_000_000 } } }];
        yield return ["distrito repetido", new { nombre = $"{Prefijo} dr", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "230104", metaDosis = 5 }, new { ubigeo = "230104", metaDosis = 6 } } }];
        yield return ["meta sin valor", new { nombre = $"{Prefijo} ms", fechaInicio = F(Hoy), fechaFin = F(Hoy.AddDays(5)), metas = new[] { new { ubigeo = "230104" } } }];
    }

    [Theory]
    [MemberData(nameof(CuerposInvalidos))]
    public async Task Un_cuerpo_invalido_da_400_en_espanol_y_no_crea_nada(string caso, object cuerpo)
    {
        var c = await app.SesionComoAsync("epi01");
        var antes = Convert.ToInt32(await Sql("SELECT COUNT(*) FROM vac.Campana"));

        var (estado, j) = await Enviar(c, "/api/campanas", cuerpo);

        Assert.True(estado == HttpStatusCode.BadRequest, $"{caso}: {estado} {j}");
        var error = j.GetProperty("error").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(error), caso);
        foreach (var interno in new[] { "SqlException", "vac.", "CK_", "FK_", "constraint", "Exception" })
            Assert.DoesNotContain(interno, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(antes, Convert.ToInt32(await Sql("SELECT COUNT(*) FROM vac.Campana")));
    }

    [Fact]
    public async Task Un_cuerpo_que_no_es_json_da_400_no_500()
    {
        var c = await app.SesionComoAsync("epi01");

        var r = await c.PostAsync("/api/campanas", new StringContent("{no es json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Un_nombre_con_comillas_o_sql_se_guarda_tal_cual_sin_inyeccion()
    {
        var c = await app.SesionComoAsync("epi01");
        var nombre = $"{Prefijo} '; DROP TABLE vac.Campana;--";

        var id = await Crear(c, nombre, Hoy, Hoy.AddDays(5));

        Assert.Equal(nombre, (string)(await Sql($"SELECT Nombre FROM vac.Campana WHERE IdCampana = {id}"))!);
        Assert.NotNull(await Sql("SELECT OBJECT_ID('vac.Campana')"));
    }

    // ---------- cerrar ----------

    [Fact]
    public async Task Cerrar_una_campana_vigente_la_termina_hoy()
    {
        var c = await app.SesionComoAsync("epi01");
        var id = await Crear(c, $"{Prefijo} cerrar", Hoy.AddDays(-5), Hoy.AddDays(30));

        var (estado, j) = await Enviar(c, $"/api/campanas/{id}/cerrar", null);

        Assert.True(estado == HttpStatusCode.OK, j.ToString());
        Assert.Equal(id, j.GetProperty("idCampana").GetInt32());
        Assert.Equal(F(Hoy), j.GetProperty("fechaFin").GetString());
        Assert.Equal(F(Hoy), Buscar(await Listar(c), id).GetProperty("fechaFin").GetString());
    }

    [Fact]
    public async Task Cerrar_dos_veces_la_misma_campana_da_400_50069()
    {
        var c = await app.SesionComoAsync("admin");
        var id = await Crear(c, $"{Prefijo} cerrar dos", Hoy.AddDays(-5), Hoy.AddDays(30));
        await Enviar(c, $"/api/campanas/{id}/cerrar", null);

        var (estado, j) = await Enviar(c, $"/api/campanas/{id}/cerrar", null);

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Equal(50069, j.GetProperty("codigo").GetInt32());
    }

    [Fact]
    public async Task Cerrar_una_campana_que_no_empezo_da_400_50068()
    {
        var c = await app.SesionComoAsync("epi01");
        var id = await Crear(c, $"{Prefijo} futura", Hoy.AddDays(3), Hoy.AddDays(30));

        var (estado, j) = await Enviar(c, $"/api/campanas/{id}/cerrar", null);

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Equal(50068, j.GetProperty("codigo").GetInt32());
    }

    [Fact]
    public async Task Cerrar_una_campana_inexistente_da_400_50067_a_un_regional()
    {
        var c = await app.SesionComoAsync("epi01");

        var (estado, j) = await Enviar(c, "/api/campanas/32000/cerrar", null);

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Equal(50067, j.GetProperty("codigo").GetInt32());
    }

    [Fact]
    public async Task Un_id_fuera_de_rango_no_rompe_nada()
    {
        var c = await app.SesionComoAsync("epi01");

        foreach (var id in new[] { "0", "-4", "40000", "99999999999" })
        {
            var (estado, j) = await Enviar(c, $"/api/campanas/{id}/cerrar", null);

            Assert.True(estado == HttpStatusCode.BadRequest, $"id {id}: {estado}");
            Assert.Equal(50067, j.GetProperty("codigo").GetInt32());
        }
    }

    // ---------- autorización: el mismo 403 exista o no la campaña ----------

    [Theory]
    [InlineData("jefe01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Los_demas_roles_reciben_el_mismo_403_en_todas_las_rutas(string usuario)
    {
        var admin = await app.SesionComoAsync("admin");
        var existente = await Crear(admin, $"{Prefijo} ajena {usuario}", Hoy.AddDays(-5), Hoy.AddDays(30));
        var c = await app.SesionComoAsync(usuario);
        var antes = Convert.ToInt32(await Sql("SELECT COUNT(*) FROM vac.Campana"));

        var lista = await c.GetAsync("/api/campanas");
        var crear = await c.PostAsJsonAsync("/api/campanas", Cuerpo($"{Prefijo} no debe crearse", Hoy, Hoy.AddDays(5)));
        var cerrarExistente = await c.PostAsync($"/api/campanas/{existente}/cerrar", null);
        var cerrarInexistente = await c.PostAsync("/api/campanas/32000/cerrar", null);

        Assert.All(new[] { lista, crear, cerrarExistente, cerrarInexistente }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(await cerrarExistente.Content.ReadAsStringAsync(), await cerrarInexistente.Content.ReadAsStringAsync());
        Assert.Equal(antes, Convert.ToInt32(await Sql("SELECT COUNT(*) FROM vac.Campana")));
        Assert.Equal(F(Hoy.AddDays(30)), Buscar(await Listar(admin), existente).GetProperty("fechaFin").GetString());   // sigue abierta
    }

    [Fact]
    public async Task Sin_sesion_recibe_401_en_todas_las_rutas()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/campanas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/campanas", Cuerpo($"{Prefijo} x", Hoy, Hoy.AddDays(5)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync("/api/campanas/1/cerrar", null)).StatusCode);
    }

    [Fact]
    public async Task La_lista_responde_en_menos_de_2_segundos_RNF01()
    {
        var c = await app.SesionComoAsync("epi01");
        await Listar(c);   // calienta

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        await Listar(c);
        reloj.Stop();

        Assert.True(reloj.ElapsedMilliseconds < 2000, $"{reloj.ElapsedMilliseconds} ms");
    }

    async Task<object?> Sql(string sql)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }
}
