using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// RF-10 / CU10: declarar y cerrar brotes es exclusivo del epidemiólogo y el administrador (RN-22).
/// RN-19: un solo brote activo por enfermedad y distrito. Al declarar se generan alertas de las dosis pendientes
/// de la zona y se informa cuántas; al cerrar se devuelven a DOSIS_ATRASADA las que el brote había escalado y se descartan las demás (RN-20).
/// </summary>
[Collection("api")]
public class BrotesTests(AppFactory app) : IAsyncLifetime
{
    // Todas las pruebas usan rubéola en Tarata (230401): la semilla no tiene brotes ahí. Declarar un brote
    // convierte alertas DOSIS_ATRASADA de la zona en ZONA_BROTE; al terminar se restauran y se borra lo creado.
    const string Ubigeo = "230401";
    const string Enfermedad = "Rubéola";
    List<long> escalables = [];

    public async Task InitializeAsync()
    {
        await Limpiar();
        escalables = await Ids("""
            SELECT a.IdAlerta FROM vac.Alerta a
            JOIN vac.Paciente p ON p.IdPaciente = a.IdPaciente
            JOIN vac.Distrito d ON d.IdDistrito = p.IdDistrito
            WHERE d.Ubigeo = @u AND a.TipoAlerta = 'DOSIS_ATRASADA' AND a.Estado = 'PENDIENTE'
            """);
    }

    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        var ids = escalables.Count == 0 ? "-1" : string.Join(',', escalables);
        await Sql($"""
            DECLARE @b TABLE (Id INT);
            INSERT @b SELECT b.IdBrote FROM vac.Brote b
                      JOIN vac.Distrito d ON d.IdDistrito = b.IdDistrito
                      JOIN vac.Enfermedad e ON e.IdEnfermedad = b.IdEnfermedad
                      WHERE d.Ubigeo = @u AND e.Nombre = @e;
            UPDATE vac.Alerta SET TipoAlerta = 'DOSIS_ATRASADA', IdBrote = NULL, Estado = 'PENDIENTE', FechaAtencion = NULL
             WHERE IdBrote IN (SELECT Id FROM @b) AND IdAlerta IN ({ids});
            DELETE vac.Alerta WHERE IdBrote IN (SELECT Id FROM @b);
            DELETE vac.Brote WHERE IdBrote IN (SELECT Id FROM @b);
            """);
    }

    async Task<object?> Sql(string sql, params (string, object)[] args)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", Ubigeo);
        cmd.Parameters.AddWithValue("@e", Enfermedad);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }

    async Task<List<long>> Ids(string sql)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", Ubigeo);
        var lista = new List<long>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) lista.Add(rd.GetInt64(0));
        return lista;
    }

    static string Hoy => DateTime.Today.ToString("yyyy-MM-dd");
    static object Declaracion(string? ubigeo = Ubigeo, string? enfermedad = Enfermedad, string? fecha = null, int casos = 3) =>
        new { ubigeo, enfermedad, fechaInicio = fecha ?? Hoy, casosConfirmados = casos };

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // Si falla, el mensaje trae el cuerpo de la respuesta: así una falla intermitente se explica sola.
    static async Task<HttpResponseMessage> Exito(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r;
    }

    async Task<int> Declarar(HttpClient c) =>
        (await Json(await Exito(await c.PostAsJsonAsync("/api/brotes", Declaracion())))).GetProperty("idBrote").GetInt32();

    static async Task Cerrar(HttpClient c, int idBrote) => await Exito(await c.PostAsJsonAsync($"/api/brotes/{idBrote}/cerrar", new { }));

    async Task<int> Pendientes(int idBrote) =>
        (int)(await Sql("SELECT COUNT(*) FROM vac.Alerta WHERE IdBrote = @b AND Estado = 'PENDIENTE'", ("@b", idBrote)))!;

    // ---------------------------------------------------------------- Acceso (RN-22)

    [Fact]
    public async Task Sin_sesion_recibe_401()
    {
        var c = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/brotes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/brotes", Declaracion())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/brotes/1/cerrar", new { })).StatusCode);
    }

    [Theory]
    [InlineData("vac01")]
    [InlineData("jefe01")]
    [InlineData("ciud01")]
    public async Task Roles_sin_alcance_regional_reciben_403_y_no_cambian_nada(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/brotes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/brotes", Declaracion())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/brotes/1/cerrar", new { })).StatusCode);
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.Brote b JOIN vac.Distrito d ON d.IdDistrito = b.IdDistrito WHERE d.Ubigeo = @u"))!);
    }

    // ---------------------------------------------------------------- Consulta

    [Theory]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task Epidemiologo_y_administrador_ven_los_brotes_y_las_enfermedades(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);
        var r = await c.GetAsync("/api/brotes");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);

        var enfermedades = j.GetProperty("enfermedades").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Sarampión", enfermedades);
        Assert.Contains(Enfermedad, enfermedades);

        var brotes = j.GetProperty("brotes").EnumerateArray().ToList();
        var activos = brotes.Where(b => b.GetProperty("estado").GetString() == "ACTIVO").ToList();
        Assert.True(activos.Count >= 3, "la semilla trae 3 brotes activos de sarampión");
        var b0 = activos[0];
        foreach (var campo in new[] { "idBrote", "enfermedad", "ubigeo", "distrito", "fechaInicio", "casosConfirmados", "alertasPendientes" })
            Assert.True(b0.TryGetProperty(campo, out _), $"falta el campo {campo}");
    }

    // ---------------------------------------------------------------- Declarar (RF-10, RN-19)

    [Fact]
    public async Task Declarar_devuelve_cuantas_alertas_genero()
    {
        var c = await app.SesionComoAsync("epi01");
        var r = await Exito(await c.PostAsJsonAsync("/api/brotes", Declaracion()));
        var j = await Json(r);
        var id = j.GetProperty("idBrote").GetInt32();
        var generadas = j.GetProperty("alertasGeneradas").GetInt32();

        Assert.True(id > 0);
        Assert.Equal(await Pendientes(id), generadas);

        var lista = await Json(await c.GetAsync("/api/brotes"));
        var fila = lista.GetProperty("brotes").EnumerateArray().Single(b => b.GetProperty("idBrote").GetInt32() == id);
        Assert.Equal("ACTIVO", fila.GetProperty("estado").GetString());
        Assert.Equal(generadas, fila.GetProperty("alertasPendientes").GetInt32());
        Assert.Equal(Ubigeo, fila.GetProperty("ubigeo").GetString());
    }

    [Fact]
    public async Task Un_segundo_brote_activo_de_lo_mismo_se_rechaza()
    {
        var c = await app.SesionComoAsync("epi01");
        await Declarar(c);

        var r = await c.PostAsJsonAsync("/api/brotes", Declaracion());
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(50022, (await Json(r)).GetProperty("codigo").GetInt32());
    }

    [Fact]
    public async Task Dos_declaraciones_simultaneas_dejan_un_solo_brote_activo()
    {
        var c1 = await app.SesionComoAsync("epi01");
        var c2 = await app.SesionComoAsync("admin");

        var rs = await Task.WhenAll(c1.PostAsJsonAsync("/api/brotes", Declaracion()), c2.PostAsJsonAsync("/api/brotes", Declaracion()));

        Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
        var rechazada = Assert.Single(rs, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(50022, (await Json(rechazada)).GetProperty("codigo").GetInt32());
        Assert.Equal(1, (int)(await Sql("""
            SELECT COUNT(*) FROM vac.Brote b JOIN vac.Distrito d ON d.IdDistrito = b.IdDistrito
            JOIN vac.Enfermedad e ON e.IdEnfermedad = b.IdEnfermedad
            WHERE d.Ubigeo = @u AND e.Nombre = @e AND b.FechaFin IS NULL
            """))!);
    }

    [Fact]
    public async Task Declarar_con_datos_invalidos_da_400_con_su_codigo()
    {
        var c = await app.SesionComoAsync("epi01");
        var manana = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");

        async Task<(HttpStatusCode, int?)> Probar(object cuerpo)
        {
            var r = await c.PostAsJsonAsync("/api/brotes", cuerpo);
            var j = await Json(r);
            return (r.StatusCode, j.TryGetProperty("codigo", out var k) ? k.GetInt32() : null);
        }

        Assert.Equal((HttpStatusCode.BadRequest, 50024), await Probar(Declaracion(casos: -1)));
        Assert.Equal((HttpStatusCode.BadRequest, 50025), await Probar(Declaracion(fecha: manana)));
        Assert.Equal((HttpStatusCode.BadRequest, 50020), await Probar(Declaracion(ubigeo: "999999")));
        Assert.Equal((HttpStatusCode.BadRequest, 50021), await Probar(Declaracion(enfermedad: "Enfermedad inventada")));
        // Forma de la entrada: se rechaza en la API, sin llegar a la base.
        Assert.Equal((HttpStatusCode.BadRequest, (int?)null), await Probar(Declaracion(ubigeo: "abc")));
        Assert.Equal((HttpStatusCode.BadRequest, (int?)null), await Probar(Declaracion(ubigeo: null)));
        Assert.Equal((HttpStatusCode.BadRequest, (int?)null), await Probar(Declaracion(enfermedad: " ")));
        Assert.Equal((HttpStatusCode.BadRequest, (int?)null), await Probar(Declaracion(fecha: "03/10/2026")));
        Assert.Equal((HttpStatusCode.BadRequest, (int?)null), await Probar(Declaracion(casos: 40000)));
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.Brote b JOIN vac.Distrito d ON d.IdDistrito = b.IdDistrito WHERE d.Ubigeo = @u"))!);
    }

    // ---------------------------------------------------------------- Cerrar (RN-20)

    [Fact]
    public async Task Cerrar_descarta_las_alertas_pendientes_y_el_brote_queda_cerrado()
    {
        var c = await app.SesionComoAsync("admin");
        var id = await Declarar(c);
        Assert.True(await Pendientes(id) > 0, "Tarata debe tener dosis pendientes de rubéola");

        await Cerrar(c, id);

        Assert.Equal(0, await Pendientes(id));
        var lista = await Json(await c.GetAsync("/api/brotes"));
        var fila = lista.GetProperty("brotes").EnumerateArray().Single(b => b.GetProperty("idBrote").GetInt32() == id);
        Assert.Equal("CERRADO", fila.GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Cerrar_devuelve_a_DOSIS_ATRASADA_las_alertas_que_el_brote_habia_escalado()
    {
        var c = await app.SesionComoAsync("admin");
        var id = await Declarar(c);
        var antes = await Pendientes(id);
        var escaladas = await Ids($"SELECT IdAlerta FROM vac.Alerta WHERE IdBrote = {id} AND TipoAlerta = 'ZONA_BROTE' AND Estado = 'PENDIENTE'");
        var deLaZona = escaladas.Intersect(escalables).ToList();
        Assert.True(deLaZona.Count > 0, "Tarata debe tener alertas de atraso que el brote escale");

        var r = await Json(await Exito(await c.PostAsJsonAsync($"/api/brotes/{id}/cerrar", new { })));

        var ids = string.Join(',', deLaZona);
        var vueltas = (int)(await Sql($"SELECT COUNT(*) FROM vac.Alerta WHERE IdAlerta IN ({ids}) AND TipoAlerta = 'DOSIS_ATRASADA' AND Estado = 'PENDIENTE' AND IdBrote IS NULL"))!;
        Assert.True(deLaZona.Count == vueltas, $"alertas escaladas={deLaZona.Count}, de vuelta a DOSIS_ATRASADA={vueltas}; respuesta={r}");
        Assert.True(r.GetProperty("alertasRestauradas").GetInt32() >= deLaZona.Count, r.ToString());
        Assert.Equal(antes, r.GetProperty("alertasRestauradas").GetInt32() + r.GetProperty("alertasDescartadas").GetInt32());
        Assert.Equal(0, await Pendientes(id));
    }

    [Fact]
    public async Task Cerrar_un_brote_ya_cerrado_o_inexistente_da_400_50023()
    {
        var c = await app.SesionComoAsync("epi01");
        var id = await Declarar(c);
        await Cerrar(c, id);

        foreach (var idMalo in new[] { id, 2_000_000_000 })
        {
            var r = await c.PostAsJsonAsync($"/api/brotes/{idMalo}/cerrar", new { });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(50023, (await Json(r)).GetProperty("codigo").GetInt32());
        }
    }

    [Fact]
    public async Task El_cierre_no_puede_ser_anterior_al_inicio()
    {
        var c = await app.SesionComoAsync("epi01");
        var id = await Declarar(c);

        var r = await c.PostAsJsonAsync($"/api/brotes/{id}/cerrar", new { fechaFin = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(50026, (await Json(r)).GetProperty("codigo").GetInt32());

        var mala = await c.PostAsJsonAsync($"/api/brotes/{id}/cerrar", new { fechaFin = "ayer" });
        Assert.Equal(HttpStatusCode.BadRequest, mala.StatusCode);
        Assert.True(await Pendientes(id) > 0, "un cierre rechazado no debe tocar las alertas");
    }

    [Fact]
    public async Task Tras_cerrar_se_puede_declarar_otro_de_lo_mismo()
    {
        var c = await app.SesionComoAsync("epi01");
        var primero = await Declarar(c);
        await Cerrar(c, primero);

        var segundo = await Declarar(c);
        Assert.NotEqual(primero, segundo);
    }
}
