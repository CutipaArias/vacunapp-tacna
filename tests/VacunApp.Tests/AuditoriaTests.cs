using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-16 / CU15: el administrador revisa el historial de correcciones y eliminaciones de dosis (RN-23).</summary>
[Collection("api")]
public class AuditoriaTests(AppFactory app) : IAsyncLifetime
{
    // Pacientes de estas pruebas: DNI 8803xxxx.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DECLARE @p TABLE (Id INT);
        INSERT @p SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8803%';
        DECLARE @d TABLE (Id BIGINT);
        INSERT @d SELECT IdDosis FROM vac.DosisAplicada WHERE IdPaciente IN (SELECT Id FROM @p);
        DELETE vac.DosisAplicada WHERE IdDosis IN (SELECT Id FROM @d);
        DELETE vac.AuditoriaDosis WHERE DatosAnteriores LIKE '%"IdPaciente":' + ISNULL((SELECT CAST(MIN(Id) AS VARCHAR) FROM @p), '-1') + ',%';
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

    // Registra por la API un paciente y una dosis SPR1 y devuelve (dni, idDosis).
    async Task<(string Dni, long IdDosis)> PacienteConDosis()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = "8803" + Random.Shared.Next(0, 10_000).ToString("D4");
        (await vac.PostAsJsonAsync("/api/pacientes", new
        {
            tipoDocumento = "DNI", numeroDocumento = dni, nombres = "Auditoria", apellidoPaterno = "Prueba",
            fechaNacimiento = DateTime.Today.AddMonths(-20).ToString("yyyy-MM-dd"), sexo = "F", ubigeo = "230101",
        })).EnsureSuccessStatusCode();
        var est = (await vac.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();
        var lote = (string)(await Sql("""
            SELECT TOP (1) l.NumeroLote FROM vac.LoteVacuna l JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
            WHERE v.Codigo = 'SPR' AND l.FechaVencimiento >= CAST(GETDATE() AS DATE) ORDER BY l.FechaVencimiento DESC
            """))!;
        var resp = await vac.PostAsJsonAsync("/api/dosis", new { documento = dni, vacuna = "SPR", dosis = 1, lote, idEstablecimiento = est });
        resp.EnsureSuccessStatusCode();
        return (dni, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idDosis").GetInt64());
    }

    // Corrige la fecha de la dosis como lo haría la aplicación: con el usuario en el contexto de sesión.
    async Task CorregirFecha(long idDosis, string usuario)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            EXEC sp_set_session_context @key = N'usuario', @value = @u;
            UPDATE vac.DosisAplicada SET FechaAplicacion = DATEADD(DAY, -1, FechaAplicacion) WHERE IdDosis = @d;
            """;
        cmd.Parameters.AddWithValue("@u", usuario);
        cmd.Parameters.AddWithValue("@d", idDosis);
        await cmd.ExecuteNonQueryAsync();
    }

    async Task<JsonElement> Auditoria(string query = "")
    {
        var admin = await app.SesionComoAsync("admin");
        var resp = await admin.GetAsync("/api/auditoria" + query);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Una_correccion_aparece_en_la_auditoria_con_el_usuario_y_las_fechas()
    {
        var (dni, id) = await PacienteConDosis();
        await CorregirFecha(id, "vac01");

        var filas = (await Auditoria($"?documento={dni}")).EnumerateArray().ToList();

        var fila = Assert.Single(filas);
        Assert.Equal("U", fila.GetProperty("operacion").GetString());
        Assert.Equal("vac01", fila.GetProperty("usuario").GetString());
        Assert.Equal(dni, fila.GetProperty("numeroDocumento").GetString());
        Assert.Equal("SPR", fila.GetProperty("codigoVacuna").GetString());
        var anterior = DateTime.Parse(fila.GetProperty("fechaAplicacionAnterior").GetString()!);
        var nueva = DateTime.Parse(fila.GetProperty("fechaAplicacionNueva").GetString()!);
        Assert.Equal(anterior.AddDays(-1), nueva);
    }

    [Fact]
    public async Task Una_eliminacion_aparece_como_D_y_sin_datos_nuevos()
    {
        var (dni, id) = await PacienteConDosis();
        await Sql("DELETE vac.DosisAplicada WHERE IdDosis = @d", ("@d", id));

        var fila = Assert.Single((await Auditoria($"?documento={dni}")).EnumerateArray().ToList());

        Assert.Equal("D", fila.GetProperty("operacion").GetString());
        Assert.Equal(JsonValueKind.Null, fila.GetProperty("fechaAplicacionNueva").ValueKind);
        Assert.Contains("FechaAplicacion", fila.GetProperty("datosAnteriores").GetString());
    }

    [Fact]
    public async Task El_filtro_por_operacion_separa_correcciones_y_eliminaciones()
    {
        var (dni, id) = await PacienteConDosis();
        await CorregirFecha(id, "vac01");
        await Sql("DELETE vac.DosisAplicada WHERE IdDosis = @d", ("@d", id));

        var soloD = (await Auditoria($"?documento={dni}&operacion=D")).EnumerateArray().ToList();
        var todas = (await Auditoria($"?documento={dni}")).EnumerateArray().ToList();

        Assert.Single(soloD);
        Assert.Equal(2, todas.Count);
    }

    [Fact]
    public async Task El_resultado_va_de_lo_mas_reciente_a_lo_mas_antiguo_y_respeta_el_limite()
    {
        var (dni, id) = await PacienteConDosis();
        await CorregirFecha(id, "vac01");
        await Sql("DELETE vac.DosisAplicada WHERE IdDosis = @d", ("@d", id));

        var una = (await Auditoria($"?documento={dni}&top=1")).EnumerateArray().ToList();

        Assert.Single(una);
        Assert.Equal("D", una[0].GetProperty("operacion").GetString());   // la más reciente primero
    }

    [Theory]
    [InlineData("?operacion=X")]
    [InlineData("?top=0")]
    [InlineData("?top=-3")]
    [InlineData("?top=501")]
    public async Task Los_parametros_fuera_de_rango_devuelven_400_con_mensaje(string query)
    {
        var admin = await app.SesionComoAsync("admin");

        var resp = await admin.GetAsync("/api/auditoria" + query);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Un_documento_con_texto_SQL_se_busca_como_valor_y_no_devuelve_nada()
    {
        var filas = await Auditoria("?documento=" + Uri.EscapeDataString("' OR 1=1;--"));

        Assert.Empty(filas.EnumerateArray());
    }

    [Theory]
    [InlineData("epi01")]
    [InlineData("jefe01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Solo_el_administrador_consulta_la_auditoria(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/auditoria")).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_la_auditoria_devuelve_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/auditoria")).StatusCode);
    }
}
