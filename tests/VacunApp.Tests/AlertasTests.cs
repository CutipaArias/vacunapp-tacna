using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// RF-11 / RF-12 / CU11: alertas y pendientes por alcance (RN-22). El epidemiólogo y el administrador ven toda la región;
/// el vacunador y el jefe, el distrito de su establecimiento y, en inasistencias, las citas de su establecimiento.
/// Una alerta fuera de alcance y una inexistente reciben la misma respuesta 403.
/// </summary>
[Collection("api")]
public class AlertasTests(AppFactory app) : IAsyncLifetime
{
    // Pacientes de estas pruebas: DNI 8820xxxx. Las existencias de stock tocadas se restauran siempre.
    string ubigeoPropio = "", ubigeoAjeno = "", nombreDistritoPropio = "", nombreEstPropio = "", nombreEstAjeno = "";
    int estPropio, estAjeno;
    readonly List<int> franjas = [];
    readonly List<(int idStock, int cantidad)> stockTocado = [];

    public async Task InitializeAsync()
    {
        await Limpiar();
        estPropio = Convert.ToInt32(await Sql("SELECT IdEstablecimiento FROM vac.Usuario WHERE NombreUsuario = 'vac01'"));
        ubigeoPropio = (string)(await Sql("SELECT d.Ubigeo FROM vac.EstablecimientoSalud es JOIN vac.Distrito d ON d.IdDistrito = es.IdDistrito WHERE es.IdEstablecimiento = @e", ("@e", estPropio)))!;
        nombreDistritoPropio = (string)(await Sql("SELECT Nombre FROM vac.Distrito WHERE Ubigeo = @u", ("@u", ubigeoPropio)))!;
        nombreEstPropio = (string)(await Sql("SELECT Nombre FROM vac.EstablecimientoSalud WHERE IdEstablecimiento = @e", ("@e", estPropio)))!;
        ubigeoAjeno = ubigeoPropio == "230401" ? "230101" : "230401";
        estAjeno = Convert.ToInt32(await Sql("SELECT TOP (1) s.IdEstablecimiento FROM vac.StockLote s WHERE s.IdEstablecimiento <> @e AND s.Cantidad > 0 ORDER BY s.IdEstablecimiento", ("@e", estPropio)));
        nombreEstAjeno = (string)(await Sql("SELECT Nombre FROM vac.EstablecimientoSalud WHERE IdEstablecimiento = @e", ("@e", estAjeno)))!;
    }

    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        foreach (var (id, cantidad) in stockTocado) await Sql("UPDATE vac.StockLote SET Cantidad = @c WHERE IdStock = @i", ("@c", cantidad), ("@i", id));
        stockTocado.Clear();
        var ids = franjas.Count == 0 ? "-1" : string.Join(',', franjas);
        await Sql($"""
            DECLARE @p TABLE (Id INT);
            INSERT @p SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8820%';
            DELETE vac.Cita WHERE IdPaciente IN (SELECT Id FROM @p) OR IdHorario IN ({ids});
            DELETE vac.Alerta WHERE IdPaciente IN (SELECT Id FROM @p);
            DELETE vac.HorarioAtencion WHERE IdHorario IN ({ids});
            DELETE vac.Paciente WHERE IdPaciente IN (SELECT Id FROM @p);
            """);
        franjas.Clear();
    }

    async Task<object?> Sql(string sql, params (string, object)[] args)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }

    // Paciente de 20 meses en el distrito dado, con una alerta pendiente del tipo indicado para SPR 1.ª dosis.
    // (El alta en un distrito con brote ya trae alertas automáticas: se descartan para dejar solo la propia.)
    async Task<long> PacienteConAlerta(string dni, string ubigeo, string tipo = "DOSIS_ATRASADA")
    {
        await Sql("""
            INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
            SELECT 'DNI', @d, 'Prueba', 'Alertas', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', IdDistrito FROM vac.Distrito WHERE Ubigeo = @u;
            UPDATE a SET Estado = 'DESCARTADA', FechaAtencion = SYSDATETIME() FROM vac.Alerta a JOIN vac.Paciente p ON p.IdPaciente = a.IdPaciente
             WHERE p.NumeroDocumento = @d AND a.Estado = 'PENDIENTE';
            """, ("@d", dni), ("@u", ubigeo));
        return (long)(await Sql("""
            INSERT vac.Alerta (IdPaciente, IdEsquema, TipoAlerta)
            SELECT p.IdPaciente, e.IdEsquema, @t FROM vac.Paciente p, vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
             WHERE p.NumeroDocumento = @d AND v.Codigo = 'SPR' AND e.NumeroDosis = 1;
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """, ("@d", dni), ("@t", tipo)))!;
    }

    async Task<string> EstadoAlerta(long id) => (string)(await Sql("SELECT Estado FROM vac.Alerta WHERE IdAlerta = @i", ("@i", id)))!;

    static async Task<List<JsonElement>> Lista(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} → {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    static long Id(JsonElement f) => f.GetProperty("IdAlerta").GetInt64();

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // ---------------------------------------------------------------- Acceso

    [Fact]
    public async Task Sin_sesion_recibe_401()
    {
        var c = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/alertas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/alertas/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/pendientes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/alertas/1/atender", new { estado = "ATENDIDA" })).StatusCode);
    }

    [Fact]
    public async Task El_ciudadano_recibe_403_en_todo()
    {
        var c = await app.SesionComoAsync("ciud01");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/alertas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/alertas/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/pendientes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/alertas/1/atender", new { estado = "ATENDIDA" })).StatusCode);
    }

    // ---------------------------------------------------------------- Alertas de pacientes (RF-11)

    [Theory]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task El_nivel_regional_ve_los_distritos_de_toda_la_region(string usuario)
    {
        var propia = await PacienteConAlerta("88200001", ubigeoPropio);
        var ajena = await PacienteConAlerta("88200002", ubigeoAjeno);
        var c = await app.SesionComoAsync(usuario);

        Assert.Contains(await Lista(c, "/api/alertas?documento=88200001"), f => Id(f) == propia);
        Assert.Contains(await Lista(c, "/api/alertas?documento=88200002"), f => Id(f) == ajena);
        Assert.Contains(await Lista(c, $"/api/alertas?ubigeo={ubigeoAjeno}&top=1000"), f => f.GetProperty("Ubigeo").GetString() == ubigeoAjeno);
    }

    [Theory]
    [InlineData("vac01")]
    [InlineData("jefe01")]
    public async Task El_personal_ve_solo_las_alertas_de_su_distrito(string usuario)
    {
        var propia = await PacienteConAlerta("88200001", ubigeoPropio);
        var ajena = await PacienteConAlerta("88200002", ubigeoAjeno);
        var c = await app.SesionComoAsync(usuario);

        var propias = await Lista(c, "/api/alertas?documento=88200001");
        var ajenas = await Lista(c, "/api/alertas?documento=88200002");
        Assert.Contains(propias, f => Id(f) == propia);
        Assert.Empty(ajenas);

        var todas = await Lista(c, "/api/alertas?top=1000");
        Assert.NotEmpty(todas);
        Assert.All(todas, f => Assert.Equal(ubigeoPropio, f.GetProperty("Ubigeo").GetString()));
        Assert.DoesNotContain(todas, f => Id(f) == ajena);
    }

    [Fact]
    public async Task El_personal_que_pide_otro_distrito_recibe_403()
    {
        var c = await app.SesionComoAsync("vac01");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/alertas?ubigeo={ubigeoAjeno}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/alertas?ubigeo={ubigeoPropio}&top=1")).StatusCode);
    }

    [Fact]
    public async Task El_filtro_por_tipo_funciona_y_un_tipo_invalido_da_400()
    {
        var inas = await PacienteConAlerta("88200001", ubigeoPropio, "INASISTENCIA");
        await PacienteConAlerta("88200002", ubigeoPropio, "DOSIS_ATRASADA");
        var c = await app.SesionComoAsync("epi01");

        var filas = await Lista(c, "/api/alertas?tipo=INASISTENCIA&documento=88200001");
        Assert.Equal(inas, Id(Assert.Single(filas)));
        Assert.Empty(await Lista(c, "/api/alertas?tipo=INASISTENCIA&documento=88200002"));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/alertas?tipo=INVENTADA")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/alertas?tipo=STOCK_BAJO")).StatusCode);   // el stock tiene su propia consulta
    }

    [Fact]
    public async Task La_inasistencia_se_ve_en_el_establecimiento_de_la_cita_aunque_el_paciente_viva_en_otro_distrito()
    {
        var alerta = await PacienteConAlerta("88200001", ubigeoAjeno, "INASISTENCIA");
        var ajenaSinCita = await PacienteConAlerta("88200002", ubigeoAjeno, "INASISTENCIA");
        var idHorario = Convert.ToInt32(await Sql("""
            INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
            SELECT @e, IdVacuna, DATEADD(HOUR, -3, SYSDATETIME()), 5 FROM vac.Vacuna WHERE Codigo = 'SPR';
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@e", estPropio)));
        franjas.Add(idHorario);
        await Sql("""
            INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema, Estado)
            SELECT @h, p.IdPaciente, e.IdEsquema, 'NO_ASISTIO' FROM vac.Paciente p, vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
             WHERE p.NumeroDocumento = '88200001' AND v.Codigo = 'SPR' AND e.NumeroDosis = 1
            """, ("@h", idHorario));

        var c = await app.SesionComoAsync("vac01");
        Assert.Equal(alerta, Id(Assert.Single(await Lista(c, "/api/alertas?documento=88200001"))));
        Assert.Empty(await Lista(c, "/api/alertas?documento=88200002"));
        _ = ajenaSinCita;
    }

    // ---------------------------------------------------------------- Atender o descartar (CU11)

    [Fact]
    public async Task El_personal_atiende_una_alerta_de_su_distrito_y_deja_de_figurar()
    {
        var alerta = await PacienteConAlerta("88200001", ubigeoPropio);
        var c = await app.SesionComoAsync("vac01");

        var r = await c.PostAsJsonAsync($"/api/alertas/{alerta}/atender", new { estado = "ATENDIDA" });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Assert.Equal("ATENDIDA", await EstadoAlerta(alerta));
        Assert.Empty(await Lista(c, "/api/alertas?documento=88200001"));

        var otra = await c.PostAsJsonAsync($"/api/alertas/{alerta}/atender", new { estado = "DESCARTADA" });
        Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);
        Assert.Equal(50041, (await Json(otra)).GetProperty("codigo").GetInt32());
    }

    [Fact]
    public async Task Un_estado_invalido_da_400_y_la_alerta_sigue_pendiente()
    {
        var alerta = await PacienteConAlerta("88200001", ubigeoPropio);
        var c = await app.SesionComoAsync("jefe01");

        foreach (var cuerpo in new object[] { new { estado = "BORRADA" }, new { estado = "" }, new { } })
        {
            var r = await c.PostAsJsonAsync($"/api/alertas/{alerta}/atender", cuerpo);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal("PENDIENTE", await EstadoAlerta(alerta));
    }

    [Fact]
    public async Task Una_alerta_ajena_y_una_inexistente_dan_el_mismo_403_sin_cambios()
    {
        var ajena = await PacienteConAlerta("88200002", ubigeoAjeno);
        var c = await app.SesionComoAsync("vac01");

        var deAjena = await c.PostAsJsonAsync($"/api/alertas/{ajena}/atender", new { estado = "DESCARTADA" });
        var inexistente = await c.PostAsJsonAsync("/api/alertas/2000000000/atender", new { estado = "DESCARTADA" });

        Assert.Equal(HttpStatusCode.Forbidden, deAjena.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inexistente.StatusCode);
        Assert.Equal(await deAjena.Content.ReadAsStringAsync(), await inexistente.Content.ReadAsStringAsync());
        Assert.Equal("PENDIENTE", await EstadoAlerta(ajena));
    }

    [Fact]
    public async Task El_nivel_regional_puede_atender_cualquier_alerta_de_pacientes()
    {
        var ajena = await PacienteConAlerta("88200002", ubigeoAjeno);
        var c = await app.SesionComoAsync("epi01");

        var r = await c.PostAsJsonAsync($"/api/alertas/{ajena}/atender", new { estado = "DESCARTADA" });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        Assert.Equal("DESCARTADA", await EstadoAlerta(ajena));
    }

    // ---------------------------------------------------------------- Alertas de stock

    async Task<long> AlertaStockEn(int est)
    {
        var (idStock, cantidad) = await ExistenciaDe(est);
        stockTocado.Add((idStock, cantidad));
        await Sql("UPDATE vac.StockLote SET Cantidad = 0 WHERE IdStock = @i", ("@i", idStock));
        return (long)(await Sql("SELECT TOP (1) IdAlerta FROM vac.Alerta WHERE IdStock = @i AND Estado = 'PENDIENTE'", ("@i", idStock)))!;
    }

    async Task<(int, int)> ExistenciaDe(int est)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT TOP (1) IdStock, Cantidad FROM vac.StockLote WHERE IdEstablecimiento = @e AND Cantidad > 0 ORDER BY IdStock";
        cmd.Parameters.AddWithValue("@e", est);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "el establecimiento necesita existencias para esta prueba");
        return (Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)));
    }

    [Theory]
    [InlineData("vac01")]
    [InlineData("jefe01")]
    public async Task El_personal_ve_solo_las_alertas_de_stock_de_su_establecimiento(string usuario)
    {
        var propia = await AlertaStockEn(estPropio);
        var ajena = await AlertaStockEn(estAjeno);
        var c = await app.SesionComoAsync(usuario);

        var filas = (await c.GetFromJsonAsync<JsonElement>("/api/alertas/stock")).EnumerateArray().ToList();
        Assert.Contains(filas, f => f.GetProperty("idAlerta").GetInt64() == propia);
        Assert.DoesNotContain(filas, f => f.GetProperty("idAlerta").GetInt64() == ajena);
        Assert.All(filas, f => Assert.Equal(nombreEstPropio, f.GetProperty("establecimiento").GetString()));
    }

    [Fact]
    public async Task El_nivel_regional_ve_las_alertas_de_stock_de_todos_los_establecimientos()
    {
        var propia = await AlertaStockEn(estPropio);
        var ajena = await AlertaStockEn(estAjeno);
        var c = await app.SesionComoAsync("epi01");

        var filas = (await c.GetFromJsonAsync<JsonElement>("/api/alertas/stock")).EnumerateArray().ToList();
        Assert.Contains(filas, f => f.GetProperty("idAlerta").GetInt64() == propia);
        Assert.Contains(filas, f => f.GetProperty("idAlerta").GetInt64() == ajena);
    }

    [Fact]
    public async Task Las_alertas_de_stock_no_se_atienden_a_mano_porque_se_cierran_al_reponer()
    {
        var stock = await AlertaStockEn(estPropio);

        foreach (var usuario in new[] { "vac01", "epi01" })
        {
            var c = await app.SesionComoAsync(usuario);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/alertas/{stock}/atender", new { estado = "DESCARTADA" })).StatusCode);
        }
        Assert.Equal("PENDIENTE", await EstadoAlerta(stock));
    }

    // ---------------------------------------------------------------- Pendientes (RF-12)

    [Theory]
    [InlineData("vac01")]
    [InlineData("jefe01")]
    public async Task El_personal_ve_solo_los_pendientes_de_su_distrito(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        var filas = (await c.GetFromJsonAsync<JsonElement>("/api/pendientes?top=50")).EnumerateArray().ToList();
        Assert.NotEmpty(filas);
        Assert.All(filas, f => Assert.Equal(nombreDistritoPropio, f.GetProperty("Distrito").GetString()));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/pendientes?ubigeo={ubigeoAjeno}&top=5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/pendientes?ubigeo={ubigeoPropio}&soloBrote=true&top=5")).StatusCode);
    }

    [Theory]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task El_nivel_regional_ve_los_pendientes_de_cualquier_distrito(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        var filas = (await c.GetFromJsonAsync<JsonElement>($"/api/pendientes?ubigeo={ubigeoAjeno}&top=20")).EnumerateArray().ToList();
        Assert.NotEmpty(filas);
        var nombre = (string)(await Sql("SELECT Nombre FROM vac.Distrito WHERE Ubigeo = @u", ("@u", ubigeoAjeno)))!;
        Assert.All(filas, f => Assert.Equal(nombre, f.GetProperty("Distrito").GetString()));
    }
}
