using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>Módulo stock (RN-10…RN-13): cada dosis descuenta una unidad y la última unidad no se vende dos veces.</summary>
[Collection("api")]
public class StockTests(AppFactory app) : IAsyncLifetime
{
    // Pacientes: DNI 8804xxxx. Lotes: número PRB-STK-*. Se borran junto con sus dosis, movimientos y alertas.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DECLARE @p TABLE (Id INT);
        DECLARE @d TABLE (Id BIGINT);
        DECLARE @s TABLE (Id INT);
        INSERT @p SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8804%';
        INSERT @d SELECT IdDosis FROM vac.DosisAplicada WHERE IdPaciente IN (SELECT Id FROM @p);
        INSERT @s SELECT s.IdStock FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote LIKE 'PRB-STK-%';
        DELETE vac.DosisAplicada WHERE IdDosis IN (SELECT Id FROM @d);
        DELETE vac.AuditoriaDosis WHERE IdDosis IN (SELECT Id FROM @d);
        DELETE vac.Alerta WHERE IdPaciente IN (SELECT Id FROM @p) OR IdStock IN (SELECT Id FROM @s);
        DELETE vac.MovimientoStock WHERE IdStock IN (SELECT Id FROM @s);
        DELETE vac.StockLote WHERE IdStock IN (SELECT Id FROM @s);
        DELETE vac.LoteVacuna WHERE NumeroLote LIKE 'PRB-STK-%';
        DELETE vac.Paciente WHERE IdPaciente IN (SELECT Id FROM @p);
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

    static int dniSeq = 0;
    static string NuevoDni() => "8804" + Interlocked.Increment(ref dniSeq).ToString("D4");

    async Task<string> NuevoPaciente(HttpClient vac)
    {
        var dni = NuevoDni();
        (await vac.PostAsJsonAsync("/api/pacientes", new
        {
            tipoDocumento = "DNI", numeroDocumento = dni, nombres = "Stock Prueba", apellidoPaterno = "Tester",
            fechaNacimiento = DateTime.Today.AddMonths(-20).ToString("yyyy-MM-dd"), sexo = "M", ubigeo = "230101",
        })).EnsureSuccessStatusCode();
        return dni;
    }

    // Crea un lote de SPR exclusivo de la prueba con `cantidad` unidades en el establecimiento; devuelve su número.
    async Task<string> LoteConStock(int est, int cantidad, int umbral = 0)
    {
        var numero = "PRB-STK-" + Guid.NewGuid().ToString("N")[..8];
        await Sql("""
            INSERT vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
            SELECT IdVacuna, @n, 'Prueba', DATEADD(YEAR, 1, CAST(GETDATE() AS DATE)) FROM vac.Vacuna WHERE Codigo = 'SPR';
            INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo)
            SELECT IdLote, @e, @c, @u FROM vac.LoteVacuna WHERE NumeroLote = @n;
            """, ("@n", numero), ("@e", est), ("@c", cantidad), ("@u", umbral));
        return numero;
    }

    async Task<int> Existencias(string lote) => (int)(await Sql(
        "SELECT s.Cantidad FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote = @n", ("@n", lote)))!;

    static Task<HttpResponseMessage> Aplicar(HttpClient vac, string dni, int est, string lote) =>
        vac.PostAsJsonAsync("/api/dosis", new { documento = dni, vacuna = "SPR", dosis = 1, lote, idEstablecimiento = est });

    static async Task<int> MiEstablecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    [Fact]
    public async Task Una_dosis_aplicada_descuenta_una_unidad_y_deja_un_movimiento_de_salida()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await MiEstablecimiento(vac);
        var lote = await LoteConStock(est, 5);
        var dni = await NuevoPaciente(vac);

        var resp = await Aplicar(vac, dni, est, lote);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idDosis").GetInt64();
        Assert.Equal(4, await Existencias(lote));
        Assert.Equal(1, await Sql("SELECT COUNT(*) FROM vac.MovimientoStock WHERE IdDosis = @i AND Tipo = 'SALIDA' AND Cantidad = -1", ("@i", id)));
    }

    [Fact]
    public async Task Con_stock_cero_la_dosis_se_rechaza_con_un_mensaje_en_espanol_y_no_queda_registrada()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await MiEstablecimiento(vac);
        var lote = await LoteConStock(est, 0);
        var dni = await NuevoPaciente(vac);

        var resp = await Aplicar(vac, dni, est, lote);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("stock", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await Sql("SELECT COUNT(*) FROM vac.DosisAplicada d JOIN vac.Paciente p ON p.IdPaciente = d.IdPaciente WHERE p.NumeroDocumento = @d", ("@d", dni)));
        Assert.Equal(0, await Existencias(lote));
    }

    [Fact]
    public async Task Con_una_sola_unidad_dos_dosis_simultaneas_solo_aplican_una()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await MiEstablecimiento(vac);

        for (var ronda = 0; ronda < 10; ronda++)
        {
            var lote = await LoteConStock(est, 1);
            var a = await NuevoPaciente(vac);
            var b = await NuevoPaciente(vac);

            var respuestas = await Task.WhenAll(Aplicar(vac, a, est, lote), Aplicar(vac, b, est, lote));

            Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
            Assert.Equal(0, await Existencias(lote));
            Assert.Equal(1, await Sql(
                "SELECT COUNT(*) FROM vac.MovimientoStock m JOIN vac.StockLote s ON s.IdStock = m.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote = @n AND m.Tipo = 'SALIDA'", ("@n", lote)));
        }
    }

    [Fact]
    public async Task Al_llegar_al_umbral_se_crea_una_sola_alerta_de_stock_bajo()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await MiEstablecimiento(vac);
        var lote = await LoteConStock(est, 3, umbral: 2);

        (await Aplicar(vac, await NuevoPaciente(vac), est, lote)).EnsureSuccessStatusCode();   // 3 → 2: llega al umbral
        (await Aplicar(vac, await NuevoPaciente(vac), est, lote)).EnsureSuccessStatusCode();   // 2 → 1: sigue bajo, sin duplicar

        Assert.Equal(1, await Sql("""
            SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
            WHERE l.NumeroLote = @n AND a.TipoAlerta = 'STOCK_BAJO' AND a.Estado = 'PENDIENTE'
            """, ("@n", lote)));
    }
}
