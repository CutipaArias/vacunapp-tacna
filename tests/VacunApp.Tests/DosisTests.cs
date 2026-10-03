using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-04 / CU04: el vacunador registra dosis; la base aplica RN-03 a RN-09 y devuelve el motivo.</summary>
[Collection("api")]
public class DosisTests(AppFactory app) : IAsyncLifetime
{
    // Pacientes de estas pruebas: DNI 8801xxxx. Se borran con sus dosis y auditoría antes y después.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DECLARE @p TABLE (Id INT);
        DECLARE @d TABLE (Id BIGINT);
        INSERT @p SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8801%';
        INSERT @d SELECT IdDosis FROM vac.DosisAplicada WHERE IdPaciente IN (SELECT Id FROM @p);
        DELETE vac.DosisAplicada WHERE IdDosis IN (SELECT Id FROM @d);
        DELETE vac.AuditoriaDosis WHERE IdDosis IN (SELECT Id FROM @d);
        DELETE vac.Alerta WHERE IdPaciente IN (SELECT Id FROM @p);
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

    static string Hoy() => DateTime.Today.ToString("yyyy-MM-dd");

    // Registra por la API un paciente nacido hace `meses` meses (o `dias` días) y devuelve su DNI.
    async Task<string> NuevoPaciente(HttpClient vac, int meses = 20, int dias = 0)
    {
        var dni = "8801" + Random.Shared.Next(0, 10_000).ToString("D4");
        var nac = DateTime.Today.AddMonths(-meses).AddDays(-dias);
        var resp = await vac.PostAsJsonAsync("/api/pacientes", new
        {
            tipoDocumento = "DNI", numeroDocumento = dni, nombres = "Dosis Prueba", apellidoPaterno = "Tester",
            fechaNacimiento = nac.ToString("yyyy-MM-dd"), sexo = "M", ubigeo = "230101",
        });
        resp.EnsureSuccessStatusCode();
        return dni;
    }

    async Task<int> MiEstablecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<string> LoteVigente() =>
        (string)(await Sql("""
            SELECT TOP (1) l.NumeroLote FROM vac.LoteVacuna l JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
            WHERE v.Codigo = 'SPR' AND l.FechaVencimiento >= CAST(GETDATE() AS DATE) ORDER BY l.FechaVencimiento DESC
            """))!;

    async Task<string> LoteVencido() =>
        (string)(await Sql("""
            SELECT TOP (1) l.NumeroLote FROM vac.LoteVacuna l JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
            WHERE v.Codigo = 'SPR' AND l.FechaVencimiento < DATEADD(YEAR, -1, CAST(GETDATE() AS DATE)) ORDER BY l.FechaVencimiento
            """))!;

    static object Dosis(string dni, int est, string lote, int dosis = 1, string vacuna = "SPR", string? fecha = null, string? dniVacunador = null) =>
        new { documento = dni, vacuna, dosis, lote, idEstablecimiento = est, dniVacunador, fecha };

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    [Fact]
    public async Task Una_dosis_valida_se_registra_con_el_vacunador_y_el_establecimiento_de_la_sesion()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var est = await MiEstablecimiento(vac);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, await LoteVigente()));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idDosis").GetInt64();
        Assert.Equal(est, (short)(await Sql("SELECT IdEstablecimiento FROM vac.DosisAplicada WHERE IdDosis = @i", ("@i", id)))!);
        Assert.Equal("vac01", await Sql(
            "SELECT u.NombreUsuario FROM vac.DosisAplicada d JOIN vac.Usuario u ON u.IdVacunador = d.IdVacunador WHERE d.IdDosis = @i", ("@i", id)));
    }

    [Fact]
    public async Task El_vacunador_no_puede_registrar_a_nombre_de_otro_vacunador()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var est = await MiEstablecimiento(vac);
        // DNI de otro vacunador del mismo establecimiento: se debe ignorar y usar siempre el de la sesión.
        var otro = (string)(await Sql("""
            SELECT TOP (1) v.Dni FROM vac.Vacunador v WHERE v.IdEstablecimiento = @e
              AND v.IdVacunador NOT IN (SELECT IdVacunador FROM vac.Usuario WHERE NombreUsuario = 'vac01')
            """, ("@e", est)))!;

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, await LoteVigente(), dniVacunador: otro));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idDosis").GetInt64();
        Assert.NotEqual(otro, (string)(await Sql("SELECT v.Dni FROM vac.DosisAplicada d JOIN vac.Vacunador v ON v.IdVacunador = d.IdVacunador WHERE d.IdDosis = @i", ("@i", id)))!);
    }

    [Fact]
    public async Task RN03_una_edad_menor_a_la_minima_se_rechaza_con_el_motivo()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac, meses: 0, dias: 10);   // recién nacido: SPR1 exige 12 meses

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), await LoteVigente()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("edad", await Error(resp));
    }

    [Fact]
    public async Task RN05_la_segunda_dosis_exige_la_primera()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), await LoteVigente(), dosis: 2));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("dosis anterior", await Error(resp));
    }

    [Fact]
    public async Task RN06_no_se_respeta_el_intervalo_si_se_aplica_la_segunda_el_mismo_dia()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var est = await MiEstablecimiento(vac);
        var lote = await LoteVigente();
        Assert.Equal(HttpStatusCode.OK, (await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, lote, dosis: 1))).StatusCode);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, lote, dosis: 2));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("intervalo", await Error(resp));
    }

    [Fact]
    public async Task RN07_la_misma_dosis_no_se_registra_dos_veces()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var est = await MiEstablecimiento(vac);
        var lote = await LoteVigente();
        await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, lote));

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, est, lote));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("ya fue registrada", await Error(resp));
    }

    [Fact]
    public async Task RN08_un_lote_vencido_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), await LoteVencido()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("vencido", await Error(resp));
    }

    [Fact]
    public async Task Un_lote_que_no_existe_para_esa_vacuna_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), "LOTE-INEXISTENTE"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("lote", await Error(resp));
    }

    [Fact]
    public async Task Una_fecha_futura_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var manana = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), await LoteVigente(), fecha: manana));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("futura", await Error(resp));
    }

    [Theory]
    [InlineData("no-es-fecha")]
    [InlineData("2026-13-45")]
    public async Task Una_fecha_mal_escrita_devuelve_400_con_mensaje(string fecha)
    {
        var vac = await app.SesionComoAsync("vac01");

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis("70000001", await MiEstablecimiento(vac), "X", fecha: fecha));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await Error(resp)));
    }

    [Fact]
    public async Task Una_vacuna_o_dosis_fuera_del_esquema_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, await MiEstablecimiento(vac), await LoteVigente(), dosis: 9));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("esquema", await Error(resp));
    }

    [Fact]
    public async Task Un_paciente_no_registrado_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis("87654321", await MiEstablecimiento(vac), await LoteVigente()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("no registrado", await Error(resp));
    }

    [Fact]
    public async Task Registrar_en_un_establecimiento_ajeno_devuelve_403_y_no_guarda_nada()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente(vac);
        var ajeno = (int)(short)(await Sql("SELECT MAX(IdEstablecimiento) FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @e", ("@e", await MiEstablecimiento(vac))))!;

        var resp = await vac.PostAsJsonAsync("/api/dosis", Dosis(dni, ajeno, await LoteVigente()));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.DosisAplicada d JOIN vac.Paciente p ON p.IdPaciente = d.IdPaciente WHERE p.NumeroDocumento = @d", ("@d", dni)))!);
    }
}
