using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-08 / CU08: el jefe de establecimiento define franjas con cupo en su establecimiento (RN-22).</summary>
[Collection("api")]
public class HorariosTests(AppFactory app) : IAsyncLifetime
{
    // Las franjas de estas pruebas caen en enero de 2099 y los pacientes tienen DNI 8807xxxx: se borran siempre.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DELETE c FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario WHERE h.FechaHora >= '2099-01-01';
        DELETE FROM vac.HorarioAtencion WHERE FechaHora >= '2099-01-01';
        DELETE FROM vac.Paciente WHERE NumeroDocumento LIKE '8807%';
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

    static string Franja(int dia, int hora = 9) => $"2099-01-{dia:D2}T{hora:D2}:00";

    static object Nueva(string fechaHora, int cupo = 5, string vacuna = "SPR", int? est = null) =>
        new { idEstablecimiento = est, vacuna, fechaHora, cupoMaximo = cupo };

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    static async Task<int> IdHorario(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idHorario").GetInt32();

    static async Task<int> MiEstablecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<int> OtroEstablecimiento(int mio) =>
        (short)(await Sql("SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @m ORDER BY IdEstablecimiento", ("@m", mio)))!;

    async Task<List<JsonElement>> Listar(HttpClient c, string query = "?desde=2099-01-01&hasta=2099-01-31")
    {
        var r = await c.GetAsync("/api/horarios" + query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    // Reserva una cita directamente en la base para probar la ocupación (la API de reserva llega en T4.3).
    async Task<int> ReservarEnBase(int idHorario, string dni)
    {
        await Sql("""
            INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
            SELECT 'DNI', @d, 'Prueba', 'Horario', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', MIN(IdDistrito) FROM vac.Distrito
            """, ("@d", dni));
        var id = (int)(await Sql("SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento = @d", ("@d", dni)))!;
        await Sql("""
            DECLARE @e SMALLINT = (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1), @c INT;
            EXEC vac.usp_ReservarCita @p, @h, @e, NULL, @c OUTPUT;
            """, ("@p", id), ("@h", idHorario));
        return id;
    }

    // ---- Crear ----

    [Fact]
    public async Task El_jefe_crea_una_franja_en_su_establecimiento()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var est = await MiEstablecimiento(jefe);

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5), 8));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = await IdHorario(r);
        Assert.Equal(est, (short)(await Sql("SELECT IdEstablecimiento FROM vac.HorarioAtencion WHERE IdHorario = @i", ("@i", id)))!);
        Assert.Equal(8, (short)(await Sql("SELECT CupoMaximo FROM vac.HorarioAtencion WHERE IdHorario = @i", ("@i", id)))!);
    }

    [Fact]
    public async Task Una_franja_duplicada_se_rechaza_con_mensaje()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        (await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5)))).EnsureSuccessStatusCode();

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5), 3));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Ya existe una franja", await Error(r));
    }

    [Fact]
    public async Task El_mismo_horario_con_otra_vacuna_es_otra_franja()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        (await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5), vacuna: "SPR"))).EnsureSuccessStatusCode();

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5), vacuna: "BCG"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Una_franja_en_el_pasado_se_rechaza()
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva("2020-01-01T09:00"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("posterior", await Error(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(501)]
    public async Task El_cupo_fuera_de_rango_se_rechaza(int cupo)
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(6), cupo));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("cupo", await Error(r));
    }

    [Theory]
    [InlineData("no es una fecha")]
    [InlineData("")]
    [InlineData("2099-13-45T09:00")]
    public async Task La_fecha_con_formato_invalido_se_rechaza(string fecha)
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(fecha));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Una_vacuna_inexistente_se_rechaza()
    {
        var jefe = await app.SesionComoAsync("jefe01");

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(6), vacuna: "XXX"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task El_jefe_no_crea_franjas_en_otro_establecimiento()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));

        var r = await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(7), est: otro));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.HorarioAtencion WHERE FechaHora >= '2099-01-01'"))!);
    }

    // ---- Listar ----

    [Fact]
    public async Task El_listado_muestra_solo_mi_establecimiento_con_ocupacion()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var mio = await MiEstablecimiento(jefe);
        var otro = await OtroEstablecimiento(mio);
        var id = await IdHorario(await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(8), 3)));
        await Sql("INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo) SELECT @o, IdVacuna, '2099-01-08 10:00', 4 FROM vac.Vacuna WHERE Codigo = 'SPR'", ("@o", otro));
        await ReservarEnBase(id, "88070001");

        var filas = await Listar(jefe);

        var fila = Assert.Single(filas);
        Assert.Equal(id, fila.GetProperty("idHorario").GetInt32());
        Assert.Equal("SPR", fila.GetProperty("vacuna").GetString());
        Assert.Equal(3, fila.GetProperty("cupoMaximo").GetInt32());
        Assert.Equal(1, fila.GetProperty("ocupados").GetInt32());
        Assert.True(fila.GetProperty("activo").GetBoolean());
        Assert.StartsWith("2099-01-08T09:00", fila.GetProperty("fechaHora").GetString());
    }

    [Fact]
    public async Task El_listado_respeta_el_rango_de_fechas()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        (await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(5)))).EnsureSuccessStatusCode();
        (await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(20)))).EnsureSuccessStatusCode();

        Assert.Single(await Listar(jefe, "?desde=2099-01-01&hasta=2099-01-10"));
        Assert.Equal(2, (await Listar(jefe, "?desde=2099-01-01&hasta=2099-01-31")).Count);
    }

    [Fact]
    public async Task Un_rango_de_fechas_invalido_se_rechaza()
    {
        var jefe = await app.SesionComoAsync("jefe01");

        Assert.Equal(HttpStatusCode.BadRequest, (await jefe.GetAsync("/api/horarios?desde=hoy&hasta=2099-01-31")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await jefe.GetAsync("/api/horarios?desde=2099-02-01&hasta=2099-01-01")).StatusCode);
    }

    // ---- Editar ----

    [Fact]
    public async Task El_jefe_cambia_el_cupo_y_desactiva_la_franja()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var id = await IdHorario(await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(9), 3)));

        var r = await jefe.PutAsJsonAsync($"/api/horarios/{id}", new { cupoMaximo = 10, activo = false });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fila = Assert.Single(await Listar(jefe));
        Assert.Equal(10, fila.GetProperty("cupoMaximo").GetInt32());
        Assert.False(fila.GetProperty("activo").GetBoolean());
    }

    [Fact]
    public async Task No_se_baja_el_cupo_por_debajo_de_las_citas_reservadas()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var id = await IdHorario(await jefe.PostAsJsonAsync("/api/horarios", Nueva(Franja(9), 3)));
        await ReservarEnBase(id, "88070001");
        await ReservarEnBase(id, "88070002");

        var r = await jefe.PutAsJsonAsync($"/api/horarios/{id}", new { cupoMaximo = 1, activo = true });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("citas ya reservadas", await Error(r));
        Assert.Equal(3, (short)(await Sql("SELECT CupoMaximo FROM vac.HorarioAtencion WHERE IdHorario = @i", ("@i", id)))!);
    }

    [Fact]
    public async Task Una_franja_ajena_o_inexistente_recibe_la_misma_respuesta_403()
    {
        var jefe = await app.SesionComoAsync("jefe01");
        var otro = await OtroEstablecimiento(await MiEstablecimiento(jefe));
        await Sql("INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo) SELECT @o, IdVacuna, '2099-01-08 10:00', 4 FROM vac.Vacuna WHERE Codigo = 'SPR'", ("@o", otro));
        var ajena = (int)(await Sql("SELECT IdHorario FROM vac.HorarioAtencion WHERE FechaHora >= '2099-01-01'"))!;

        var rAjena = await jefe.PutAsJsonAsync($"/api/horarios/{ajena}", new { cupoMaximo = 1, activo = true });
        var rInexistente = await jefe.PutAsJsonAsync("/api/horarios/2147000000", new { cupoMaximo = 1, activo = true });

        Assert.Equal(HttpStatusCode.Forbidden, rAjena.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rInexistente.StatusCode);
        Assert.Equal(4, (short)(await Sql("SELECT CupoMaximo FROM vac.HorarioAtencion WHERE IdHorario = @i", ("@i", ajena)))!);
    }

    // ---- Roles ----

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Solo_el_jefe_gestiona_horarios(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/horarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/horarios", Nueva(Franja(10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/horarios/1", new { cupoMaximo = 1, activo = true })).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_los_horarios_devuelven_401()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/horarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/horarios", Nueva(Franja(10)))).StatusCode);
    }
}
