using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-06 / CU06: cancelar y reprogramar citas hasta 24 horas antes (RN-16), con alcance RN-17 y RN-22.</summary>
[Collection("api")]
public class CitasGestionTests(AppFactory app) : IAsyncLifetime
{
    // Franjas de marzo de 2099 (lejanas) y franjas "cercanas" creadas aquí; pacientes con DNI 8809xxxx. Todo se borra.
    readonly List<int> cercanas = [];

    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        var ids = cercanas.Count == 0 ? "-1" : string.Join(',', cercanas);
        await Sql($"""
            DELETE c FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario WHERE h.FechaHora >= '2099-03-01' AND h.FechaHora < '2099-04-01';
            DELETE c FROM vac.Cita c JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente WHERE p.NumeroDocumento LIKE '8809%';
            DELETE FROM vac.Cita WHERE IdHorario IN ({ids});
            DELETE FROM vac.HorarioAtencion WHERE (FechaHora >= '2099-03-01' AND FechaHora < '2099-04-01') OR IdHorario IN ({ids});
            DELETE v FROM vac.VinculoFamiliar v JOIN vac.Paciente p ON p.IdPaciente = v.IdPaciente WHERE p.NumeroDocumento LIKE '8809%';
            DELETE FROM vac.Paciente WHERE NumeroDocumento LIKE '8809%';
            """);
        cercanas.Clear();
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

    static async Task<int> Establecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<int> OtroEstablecimiento(int mio) =>
        (short)(await Sql("SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @m ORDER BY IdEstablecimiento", ("@m", mio)))!;

    async Task NuevoPaciente(string dni, bool vincularACiud01 = false)
    {
        await Sql("""
            INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
            SELECT 'DNI', @d, 'Prueba', 'Gestion', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', MIN(IdDistrito) FROM vac.Distrito
            """, ("@d", dni));
        if (vincularACiud01)
            await Sql("""
                INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
                SELECT u.IdUsuario, p.IdPaciente, 'MADRE' FROM vac.Usuario u, vac.Paciente p WHERE u.NombreUsuario = 'ciud01' AND p.NumeroDocumento = @d
                """, ("@d", dni));
    }

    async Task<int> FranjaLejana(int est, int dia, int cupo = 5) =>
        (int)(await Sql("""
            INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
            SELECT @e, IdVacuna, DATETIME2FROMPARTS(2099, 3, @d, 9, 0, 0, 0, 0), @c FROM vac.Vacuna WHERE Codigo = 'SPR';
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@e", est), ("@d", dia), ("@c", cupo)))!;

    async Task<int> FranjaEnHoras(int est, int horas)
    {
        var id = (int)(await Sql("""
            INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
            SELECT @e, IdVacuna, DATEADD(SECOND, @h * 3600, CAST(SYSDATETIME() AS DATETIME2(0))), 5 FROM vac.Vacuna WHERE Codigo = 'SPR';
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@e", est), ("@h", horas)))!;
        cercanas.Add(id);
        return id;
    }

    // Reserva por SQL (sin pasar por la API) para no depender de la regla de 24 h al preparar el escenario.
    async Task<int> CitaEn(string dni, int idHorario) =>
        (int)(await Sql("""
            DECLARE @c INT, @e SMALLINT = (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1);
            EXEC vac.usp_ReservarCita @p, @h, @e, NULL, @c OUTPUT;
            SELECT @c;
            """,
            ("@p", (int)(await Sql("SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento = @d", ("@d", dni)))!), ("@h", idHorario)))!;

    async Task<string> Estado(int idCita) => (string)(await Sql("SELECT Estado FROM vac.Cita WHERE IdCita = @i", ("@i", idCita)))!;
    async Task<int> HorarioDe(int idCita) => (int)(await Sql("SELECT IdHorario FROM vac.Cita WHERE IdCita = @i", ("@i", idCita)))!;

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    // ---- Cancelar ----

    [Fact]
    public async Task El_ciudadano_cancela_la_cita_de_su_hijo_y_el_cupo_queda_libre()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001", vincularACiud01: true);
        var franja = await FranjaLejana(await Establecimiento(vac), 3, cupo: 1);
        var cita = await CitaEn("88090001", franja);

        var r = await ciud.PostAsync($"/api/citas/{cita}/cancelar", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("CANCELADA", await Estado(cita));
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @h AND Estado <> 'CANCELADA'", ("@h", franja)))!);
    }

    [Fact]
    public async Task No_se_cancela_con_menos_de_24_horas()
    {
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001");
        var cita = await CitaEn("88090001", await FranjaEnHoras(await Establecimiento(vac), 23));

        var r = await vac.PostAsync($"/api/citas/{cita}/cancelar", null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("24 horas", await Error(r));
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task Con_mas_de_24_horas_el_personal_si_cancela()
    {
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001");
        var cita = await CitaEn("88090001", await FranjaEnHoras(await Establecimiento(vac), 25));

        Assert.Equal(HttpStatusCode.OK, (await vac.PostAsync($"/api/citas/{cita}/cancelar", null)).StatusCode);
        Assert.Equal("CANCELADA", await Estado(cita));
    }

    [Fact]
    public async Task Una_cita_ajena_o_inexistente_recibe_el_mismo_403_para_el_ciudadano()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001");   // no vinculado a ciud01
        var cita = await CitaEn("88090001", await FranjaLejana(await Establecimiento(vac), 3));

        Assert.Equal(HttpStatusCode.Forbidden, (await ciud.PostAsync($"/api/citas/{cita}/cancelar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ciud.PostAsync("/api/citas/2147000000/cancelar", null)).StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task El_personal_no_cancela_citas_de_otro_establecimiento()
    {
        var vac = await app.SesionComoAsync("vac01");
        var jefe = await app.SesionComoAsync("jefe01");
        await NuevoPaciente("88090001");
        var cita = await CitaEn("88090001", await FranjaLejana(await OtroEstablecimiento(await Establecimiento(vac)), 3));

        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsync($"/api/citas/{cita}/cancelar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jefe.PostAsync($"/api/citas/{cita}/cancelar", null)).StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task Una_cita_ya_cancelada_no_se_cancela_otra_vez()
    {
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001");
        var cita = await CitaEn("88090001", await FranjaLejana(await Establecimiento(vac), 3));
        (await vac.PostAsync($"/api/citas/{cita}/cancelar", null)).EnsureSuccessStatusCode();

        var r = await vac.PostAsync($"/api/citas/{cita}/cancelar", null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("programada", await Error(r));
    }

    // ---- Reprogramar ----

    [Fact]
    public async Task El_ciudadano_reprograma_y_la_franja_anterior_queda_libre()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88090001", vincularACiud01: true);
        var vieja = await FranjaLejana(est, 3, cupo: 1);
        var nueva = await FranjaLejana(est, 4);
        var cita = await CitaEn("88090001", vieja);

        var r = await ciud.PostAsJsonAsync($"/api/citas/{cita}/reprogramar", new { idHorario = nueva });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(nueva, await HorarioDe(cita));
        Assert.Equal("PROGRAMADA", await Estado(cita));
        Assert.Equal(0, (int)(await Sql("SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @h AND Estado <> 'CANCELADA'", ("@h", vieja)))!);
    }

    [Fact]
    public async Task Si_la_nueva_franja_esta_llena_la_cita_original_se_conserva()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88090001");
        await NuevoPaciente("88090002");
        var vieja = await FranjaLejana(est, 3);
        var llena = await FranjaLejana(est, 4, cupo: 1);
        var cita = await CitaEn("88090001", vieja);
        await CitaEn("88090002", llena);

        var r = await vac.PostAsJsonAsync($"/api/citas/{cita}/reprogramar", new { idHorario = llena });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("cupos", await Error(r));
        Assert.Equal(vieja, await HorarioDe(cita));
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task No_se_reprograma_con_menos_de_24_horas()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88090001");
        var cita = await CitaEn("88090001", await FranjaEnHoras(est, 23));

        var r = await vac.PostAsJsonAsync($"/api/citas/{cita}/reprogramar", new { idHorario = await FranjaLejana(est, 4) });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("24 horas", await Error(r));
    }

    [Fact]
    public async Task El_personal_no_reprograma_hacia_otro_establecimiento()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88090001");
        var vieja = await FranjaLejana(est, 3);
        var ajena = await FranjaLejana(await OtroEstablecimiento(est), 4);
        var cita = await CitaEn("88090001", vieja);

        var r = await vac.PostAsJsonAsync($"/api/citas/{cita}/reprogramar", new { idHorario = ajena });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(vieja, await HorarioDe(cita));
    }

    [Fact]
    public async Task Una_franja_nueva_inexistente_se_rechaza_sin_tocar_la_cita()
    {
        var vac = await app.SesionComoAsync("vac01");
        await NuevoPaciente("88090001");
        var vieja = await FranjaLejana(await Establecimiento(vac), 3);
        var cita = await CitaEn("88090001", vieja);

        var r = await vac.PostAsJsonAsync($"/api/citas/{cita}/reprogramar", new { idHorario = 2147000000 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(vieja, await HorarioDe(cita));
    }

    // ---- Roles ----

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    public async Task Los_roles_regionales_no_gestionan_citas(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/citas/1/cancelar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/citas/1/reprogramar", new { idHorario = 1 })).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_cancelar_y_reprogramar_devuelven_401()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync("/api/citas/1/cancelar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/citas/1/reprogramar", new { idHorario = 1 })).StatusCode);
    }
}
