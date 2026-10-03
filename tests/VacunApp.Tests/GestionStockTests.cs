using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-09 / CU09: el jefe de establecimiento registra ingresos y ajustes de stock y ve sus alertas (RN-22).</summary>
[Collection("api")]
public class GestionStockTests(AppFactory app) : IAsyncLifetime
{
    // Lotes de estas pruebas: número PRB-GST-*. Se borran con sus existencias, movimientos y alertas.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DECLARE @s TABLE (Id INT);
        INSERT @s SELECT s.IdStock FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote LIKE 'PRB-GST-%';
        DELETE vac.Alerta WHERE IdStock IN (SELECT Id FROM @s);
        DELETE vac.MovimientoStock WHERE IdStock IN (SELECT Id FROM @s);
        DELETE vac.StockLote WHERE IdStock IN (SELECT Id FROM @s);
        DELETE vac.LoteVacuna WHERE NumeroLote LIKE 'PRB-GST-%';
        """);

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

    static string NuevoLote() => "PRB-GST-" + Guid.NewGuid().ToString("N")[..8];
    static string Vence(int dias) => DateTime.Today.AddDays(dias).ToString("yyyy-MM-dd");

    static async Task<int> MiEstablecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<int> OtroEstablecimiento(int mio) =>
        (short)(await Sql("SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @m ORDER BY IdEstablecimiento", ("@m", mio)))!;

    static object Ingreso(string lote, int cantidad = 100, int? umbral = 10, int vence = 365, int? est = null) =>
        new { idEstablecimiento = est, vacuna = "SPR", numeroLote = lote, laboratorio = "Laboratorio de prueba", fechaVencimiento = Vence(vence), cantidad, umbralMinimo = umbral };

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    static async Task<int> IdStock(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idStock").GetInt32();

    async Task<JsonElement> Listar(HttpClient jefe, string query = "")
    {
        var resp = await jefe.GetAsync("/api/stock" + query);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- Lectura ----

    [Fact]
    public async Task El_jefe_ve_el_stock_de_su_establecimiento_y_solo_ese()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var est = await MiEstablecimiento(jefe);
        var lote = NuevoLote();
        (await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 30))).EnsureSuccessStatusCode();

        var filas = (await Listar(jefe)).EnumerateArray().ToList();

        Assert.Equal((int)(await Sql("SELECT COUNT(*) FROM vac.StockLote WHERE IdEstablecimiento = @e", ("@e", est)))!, filas.Count);
        var fila = Assert.Single(filas, f => f.GetProperty("numeroLote").GetString() == lote);
        Assert.Equal(30, fila.GetProperty("cantidad").GetInt32());
        Assert.Equal("SPR", fila.GetProperty("vacuna").GetString());
        Assert.Equal(10, fila.GetProperty("umbralMinimo").GetInt32());
    }

    [Fact]
    public async Task Pedir_el_stock_de_otro_establecimiento_devuelve_403()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));

        Assert.Equal(HttpStatusCode.Forbidden, (await jefe.GetAsync($"/api/stock?idEstablecimiento={otro}")).StatusCode);
    }

    // ---- Ingresos ----

    [Fact]
    public async Task Un_ingreso_crea_el_lote_la_existencia_y_el_movimiento_con_el_usuario_de_la_sesion()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var lote = NuevoLote();

        var resp = await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 80, umbral: 15));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = await IdStock(resp);
        Assert.Equal(80, await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", id)));
        Assert.Equal("jefe01", await Sql("SELECT Usuario FROM vac.MovimientoStock WHERE IdStock = @i AND Tipo = 'ENTRADA'", ("@i", id)));
    }

    [Fact]
    public async Task Un_segundo_ingreso_del_mismo_lote_suma_a_la_existencia()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var lote = NuevoLote();
        var id = await IdStock(await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 40)));

        (await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 25, umbral: null))).EnsureSuccessStatusCode();

        Assert.Equal(65, await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", id)));
    }

    [Fact]
    public async Task Ingresar_en_otro_establecimiento_devuelve_403_y_no_crea_nada()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));
        var lote = NuevoLote();

        var resp = await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, est: otro));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, await Sql("SELECT COUNT(*) FROM vac.LoteVacuna WHERE NumeroLote = @n", ("@n", lote)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Un_ingreso_con_cantidad_cero_o_negativa_devuelve_400_con_mensaje(int cantidad)
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var resp = await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote(), cantidad));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await Error(resp)));
    }

    [Fact]
    public async Task Un_lote_vencido_se_rechaza_con_el_motivo_de_la_base()
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var resp = await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote(), vence: -30));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("vencido", await Error(resp), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Una_vacuna_inexistente_o_una_fecha_mal_escrita_devuelven_400()
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var vacuna = await jefe.PostAsJsonAsync("/api/stock/ingresos",
            new { vacuna = "XXX", numeroLote = NuevoLote(), laboratorio = "Lab", fechaVencimiento = Vence(100), cantidad = 5 });
        var fecha = await jefe.PostAsJsonAsync("/api/stock/ingresos",
            new { vacuna = "SPR", numeroLote = NuevoLote(), laboratorio = "Lab", fechaVencimiento = "31/12/2030", cantidad = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, vacuna.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fecha.StatusCode);
    }

    // ---- Ajustes ----

    [Fact]
    public async Task Un_ajuste_fija_la_cantidad_y_registra_el_movimiento_con_la_diferencia()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var id = await IdStock(await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote(), 100)));

        var resp = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = id, cantidadNueva = 93, motivo = "Conteo físico" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(93, await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", id)));
        Assert.Equal(-7, await Sql("SELECT Cantidad FROM vac.MovimientoStock WHERE IdStock = @i AND Tipo = 'AJUSTE'", ("@i", id)));
        Assert.Equal("jefe01", await Sql("SELECT Usuario FROM vac.MovimientoStock WHERE IdStock = @i AND Tipo = 'AJUSTE'", ("@i", id)));
    }

    [Fact]
    public async Task Ajustar_la_existencia_de_otro_establecimiento_o_inexistente_devuelve_403()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));
        var ajena = (int)(await Sql("SELECT TOP (1) IdStock FROM vac.StockLote WHERE IdEstablecimiento = @e", ("@e", otro)))!;
        var antes = (int)(await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", ajena)))!;

        var deOtro = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = ajena, cantidadNueva = antes + 1, motivo = "x" });
        var inexistente = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = -7, cantidadNueva = 1, motivo = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, deOtro.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inexistente.StatusCode);
        Assert.Equal(antes, await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", ajena)));
    }

    [Fact]
    public async Task Un_ajuste_negativo_sin_motivo_o_sin_diferencia_devuelve_400()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var id = await IdStock(await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote(), 100)));

        var negativo = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = id, cantidadNueva = -1, motivo = "Conteo" });
        var sinMotivo = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = id, cantidadNueva = 50, motivo = " " });
        var igual = await jefe.PostAsJsonAsync("/api/stock/ajustes", new { idStock = id, cantidadNueva = 100, motivo = "Conteo" });

        Assert.Equal(HttpStatusCode.BadRequest, negativo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, igual.StatusCode);
        Assert.Equal(100, await Sql("SELECT Cantidad FROM vac.StockLote WHERE IdStock = @i", ("@i", id)));
    }

    // ---- Alertas ----

    [Fact]
    public async Task El_jefe_ve_la_alerta_de_stock_bajo_y_deja_de_verla_al_reponer()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var lote = NuevoLote();
        var id = await IdStock(await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 5, umbral: 10)));

        var antes = (await jefe.GetFromJsonAsync<JsonElement>("/api/stock/alertas")).EnumerateArray().ToList();
        (await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 100, umbral: null))).EnsureSuccessStatusCode();
        var despues = (await jefe.GetFromJsonAsync<JsonElement>("/api/stock/alertas")).EnumerateArray().ToList();

        var alerta = Assert.Single(antes, a => a.GetProperty("numeroLote").GetString() == lote);
        Assert.Equal("STOCK_BAJO", alerta.GetProperty("tipoAlerta").GetString());
        Assert.DoesNotContain(despues, a => a.GetProperty("numeroLote").GetString() == lote);
    }

    [Fact]
    public async Task Un_lote_que_vence_en_menos_de_30_dias_aparece_como_alerta_de_vencimiento()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var lote = NuevoLote();
        (await jefe.PostAsJsonAsync("/api/stock/ingresos", Ingreso(lote, 500, umbral: 10, vence: 12))).EnsureSuccessStatusCode();

        var alertas = (await jefe.GetFromJsonAsync<JsonElement>("/api/stock/alertas")).EnumerateArray().ToList();

        var alerta = Assert.Single(alertas, a => a.GetProperty("numeroLote").GetString() == lote);
        Assert.Equal("LOTE_POR_VENCER", alerta.GetProperty("tipoAlerta").GetString());
    }

    [Fact]
    public async Task Las_alertas_de_otro_establecimiento_no_se_muestran()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));
        var lote = NuevoLote();
        await Sql("""
            INSERT vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
            SELECT IdVacuna, @n, 'Prueba', DATEADD(YEAR, 1, CAST(GETDATE() AS DATE)) FROM vac.Vacuna WHERE Codigo = 'SPR';
            INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo)
            SELECT IdLote, @e, 1, 10 FROM vac.LoteVacuna WHERE NumeroLote = @n;
            """, ("@n", lote), ("@e", otro));

        var alertas = (await jefe.GetFromJsonAsync<JsonElement>("/api/stock/alertas")).EnumerateArray().ToList();

        Assert.DoesNotContain(alertas, a => a.GetProperty("numeroLote").GetString() == lote);
        Assert.Equal(1, await Sql("SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote = @n", ("@n", lote)));
    }

    // ---- Roles ----

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Solo_el_jefe_gestiona_el_stock(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/stock/alertas")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/stock/ajustes", new { idStock = 1, cantidadNueva = 1, motivo = "x" })).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_el_stock_devuelve_401()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/stock/ingresos", Ingreso(NuevoLote()))).StatusCode);
    }
}
