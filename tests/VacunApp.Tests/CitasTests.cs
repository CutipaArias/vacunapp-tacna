using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-05 / CU05: reservar cita para la dosis que corresponde (RN-14, RN-15, RN-17, RN-22).</summary>
[Collection("api")]
public class CitasTests(AppFactory app) : IAsyncLifetime
{
    // Franjas de febrero de 2099 y pacientes con DNI 8808xxxx: se borran (con sus vínculos y citas) siempre.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("""
        DELETE c FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario WHERE h.FechaHora >= '2099-02-01' AND h.FechaHora < '2099-03-01';
        DELETE c FROM vac.Cita c JOIN vac.Paciente p ON p.IdPaciente = c.IdPaciente WHERE p.NumeroDocumento LIKE '8808%';
        DELETE FROM vac.HorarioAtencion WHERE FechaHora >= '2099-02-01' AND FechaHora < '2099-03-01';
        DELETE v FROM vac.VinculoFamiliar v JOIN vac.Paciente p ON p.IdPaciente = v.IdPaciente WHERE p.NumeroDocumento LIKE '8808%';
        DELETE FROM vac.Paciente WHERE NumeroDocumento LIKE '8808%';
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

    static async Task<int> Establecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<int> OtroEstablecimiento(int mio) =>
        (short)(await Sql("SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @m ORDER BY IdEstablecimiento", ("@m", mio)))!;

    // Paciente de 20 meses (elegible a SPR 1); opcionalmente vinculado al ciudadano semilla.
    async Task<string> NuevoPaciente(string dni, bool vincularACiud01 = false)
    {
        await Sql("""
            INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
            SELECT 'DNI', @d, 'Prueba', 'Cita', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', MIN(IdDistrito) FROM vac.Distrito
            """, ("@d", dni));
        if (vincularACiud01)
            await Sql("""
                INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
                SELECT u.IdUsuario, p.IdPaciente, 'MADRE' FROM vac.Usuario u, vac.Paciente p WHERE u.NombreUsuario = 'ciud01' AND p.NumeroDocumento = @d
                """, ("@d", dni));
        return dni;
    }

    async Task<int> NuevaFranja(int est, int dia, int cupo = 5, int hora = 9, string vacuna = "SPR", bool activa = true)
    {
        var id = (int)(await Sql("""
            INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo, Activo)
            SELECT @e, IdVacuna, DATETIME2FROMPARTS(2099, 2, @dia, @hora, 0, 0, 0, 0), @c, @a FROM vac.Vacuna WHERE Codigo = @v;
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@e", est), ("@dia", dia), ("@hora", hora), ("@c", cupo), ("@a", activa), ("@v", vacuna)))!;
        return id;
    }

    static object Reserva(string documento, int idHorario, string vacuna = "SPR", int dosis = 1) =>
        new { documento, vacuna, dosis, idHorario };

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    async Task<int> Ocupados(int idHorario) =>
        (int)(await Sql("SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @h AND Estado <> 'CANCELADA'", ("@h", idHorario)))!;

    // ---- Reservar ----

    [Fact]
    public async Task El_ciudadano_reserva_para_un_hijo_vinculado()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var jefe = await app.SesionComoAsync("jefe01");
        var dni = await NuevoPaciente("88080001", vincularACiud01: true);
        var franja = await NuevaFranja(await Establecimiento(jefe), 3);

        var r = await ciud.PostAsJsonAsync("/api/citas", Reserva(dni, franja));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idCita").GetInt32();
        Assert.Equal("PROGRAMADA", (string)(await Sql("SELECT Estado FROM vac.Cita WHERE IdCita = @i", ("@i", id)))!);
        Assert.Equal(1, await Ocupados(franja));
    }

    [Fact]
    public async Task El_ciudadano_no_reserva_para_un_paciente_no_vinculado()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var jefe = await app.SesionComoAsync("jefe01");
        var dni = await NuevoPaciente("88080001");
        var franja = await NuevaFranja(await Establecimiento(jefe), 3);

        var ajeno = await ciud.PostAsJsonAsync("/api/citas", Reserva(dni, franja));
        var inexistente = await ciud.PostAsJsonAsync("/api/citas", Reserva("88089999", franja));

        // Mismo 403 para un paciente ajeno y uno inexistente: no se revela qué documentos existen.
        Assert.Equal(HttpStatusCode.Forbidden, ajeno.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inexistente.StatusCode);
        Assert.Equal(0, await Ocupados(franja));
    }

    [Fact]
    public async Task El_vacunador_reserva_para_cualquier_paciente_en_su_establecimiento()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente("88080001");
        var franja = await NuevaFranja(await Establecimiento(vac), 3);

        var r = await vac.PostAsJsonAsync("/api/citas", Reserva(dni, franja));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, await Ocupados(franja));
    }

    [Fact]
    public async Task El_vacunador_y_el_jefe_no_reservan_en_franjas_de_otro_establecimiento()
    {
        var vac = await app.SesionComoAsync("vac01");
        var jefe = await app.SesionComoAsync("jefe01");
        var dni = await NuevoPaciente("88080001");
        var franja = await NuevaFranja(await OtroEstablecimiento(await Establecimiento(vac)), 3);

        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsJsonAsync("/api/citas", Reserva(dni, franja))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jefe.PostAsJsonAsync("/api/citas", Reserva(dni, franja))).StatusCode);
        Assert.Equal(0, await Ocupados(franja));
    }

    [Fact]
    public async Task El_ciudadano_si_puede_reservar_en_cualquier_establecimiento()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente("88080001", vincularACiud01: true);
        var franja = await NuevaFranja(await OtroEstablecimiento(await Establecimiento(vac)), 3);

        Assert.Equal(HttpStatusCode.OK, (await ciud.PostAsJsonAsync("/api/citas", Reserva(dni, franja))).StatusCode);
    }

    [Fact]
    public async Task Una_segunda_cita_para_la_misma_dosis_se_rechaza_con_mensaje_claro()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var dni = await NuevoPaciente("88080001");
        var f1 = await NuevaFranja(est, 3);
        var f2 = await NuevaFranja(est, 4);
        (await vac.PostAsJsonAsync("/api/citas", Reserva(dni, f1))).EnsureSuccessStatusCode();

        var r = await vac.PostAsJsonAsync("/api/citas", Reserva(dni, f2));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("cita activa", await Error(r));
        Assert.Equal(0, await Ocupados(f2));
    }

    [Fact]
    public async Task Una_franja_llena_se_rechaza_y_dice_que_no_hay_cupos()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var franja = await NuevaFranja(est, 3, cupo: 1);
        await NuevoPaciente("88080001");
        await NuevoPaciente("88080002");
        (await vac.PostAsJsonAsync("/api/citas", Reserva("88080001", franja))).EnsureSuccessStatusCode();

        var r = await vac.PostAsJsonAsync("/api/citas", Reserva("88080002", franja));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("cupos", await Error(r));
        Assert.Equal(1, await Ocupados(franja));
    }

    [Fact]
    public async Task Una_dosis_que_no_corresponde_al_paciente_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var franja = await NuevaFranja(await Establecimiento(vac), 3);
        var dni = await NuevoPaciente("88080001");   // sin 1.ª dosis: la 2.ª no es elegible

        var r = await vac.PostAsJsonAsync("/api/citas", Reserva(dni, franja, dosis: 2));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("no es elegible", await Error(r));
    }

    [Fact]
    public async Task Una_franja_inexistente_o_inactiva_se_rechaza()
    {
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente("88080001");
        var inactiva = await NuevaFranja(await Establecimiento(vac), 3, activa: false);

        var r1 = await vac.PostAsJsonAsync("/api/citas", Reserva(dni, inactiva));
        var r2 = await vac.PostAsJsonAsync("/api/citas", Reserva(dni, 2147000000));

        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    [Fact]
    public async Task Un_paciente_inexistente_se_informa_al_personal()
    {
        var vac = await app.SesionComoAsync("vac01");
        var franja = await NuevaFranja(await Establecimiento(vac), 3);

        var r = await vac.PostAsJsonAsync("/api/citas", Reserva("88089999", franja));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("paciente", await Error(r));
    }

    [Theory]
    [InlineData("", "SPR", 1)]
    [InlineData("88080001", "", 1)]
    [InlineData("88080001", "SPR", 0)]
    [InlineData("88080001", "SPR", 9)]
    public async Task La_entrada_incompleta_se_rechaza(string documento, string vacuna, int dosis)
    {
        var vac = await app.SesionComoAsync("vac01");
        var franja = await NuevaFranja(await Establecimiento(vac), 3);

        var r = await vac.PostAsJsonAsync("/api/citas", new { documento, vacuna, dosis, idHorario = franja });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- Franjas disponibles ----

    [Fact]
    public async Task Las_franjas_disponibles_excluyen_llenas_inactivas_y_otras_vacunas()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var libre = await NuevaFranja(est, 3, cupo: 4);
        var llena = await NuevaFranja(est, 4, cupo: 1);
        await NuevaFranja(est, 5, activa: false);
        await NuevaFranja(est, 6, vacuna: "BCG");
        await NuevoPaciente("88080001");
        (await vac.PostAsJsonAsync("/api/citas", Reserva("88080001", llena))).EnsureSuccessStatusCode();

        var r = await vac.GetAsync("/api/agenda/franjas?vacuna=SPR&desde=2099-02-01&hasta=2099-02-28");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fila = Assert.Single((await r.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList());
        Assert.Equal(libre, fila.GetProperty("idHorario").GetInt32());
        Assert.Equal(4, fila.GetProperty("libres").GetInt32());
    }

    [Fact]
    public async Task El_personal_solo_ve_franjas_de_su_establecimiento_y_el_ciudadano_las_de_cualquiera()
    {
        var vac = await app.SesionComoAsync("vac01");
        var ciud = await app.SesionComoAsync("ciud01");
        var mio = await Establecimiento(vac);
        var otro = await OtroEstablecimiento(mio);
        await NuevaFranja(mio, 3);
        var franjaOtro = await NuevaFranja(otro, 4);
        const string rango = "&vacuna=SPR&desde=2099-02-01&hasta=2099-02-28";

        var delPersonal = (await vac.GetFromJsonAsync<JsonElement>("/api/agenda/franjas?x=1" + rango)).EnumerateArray().ToList();
        var delCiudadano = (await ciud.GetFromJsonAsync<JsonElement>($"/api/agenda/franjas?idEstablecimiento={otro}" + rango)).EnumerateArray().ToList();

        Assert.Single(delPersonal);
        Assert.Equal(franjaOtro, Assert.Single(delCiudadano).GetProperty("idHorario").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await vac.GetAsync($"/api/agenda/franjas?idEstablecimiento={otro}" + rango)).StatusCode);
    }

    // ---- Citas del paciente ----

    [Fact]
    public async Task Las_citas_de_un_paciente_las_ve_su_tutor_y_no_otro_ciudadano()
    {
        var ciud = await app.SesionComoAsync("ciud01");
        var vac = await app.SesionComoAsync("vac01");
        var dni = await NuevoPaciente("88080001", vincularACiud01: true);
        var otro = await NuevoPaciente("88080002");
        var franja = await NuevaFranja(await Establecimiento(vac), 3);
        (await ciud.PostAsJsonAsync("/api/citas", Reserva(dni, franja))).EnsureSuccessStatusCode();

        var propias = (await ciud.GetFromJsonAsync<JsonElement>($"/api/citas?documento={dni}")).EnumerateArray().ToList();

        var cita = Assert.Single(propias);
        Assert.Equal("PROGRAMADA", cita.GetProperty("estado").GetString());
        Assert.Equal("SPR", cita.GetProperty("vacuna").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await ciud.GetAsync($"/api/citas?documento={otro}")).StatusCode);
        Assert.Single((await vac.GetFromJsonAsync<JsonElement>($"/api/citas?documento={dni}")).EnumerateArray().ToList());
    }

    // ---- Roles ----

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    public async Task Los_roles_regionales_no_reservan_citas(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/citas", Reserva("88080001", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/agenda/franjas?vacuna=SPR")).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_las_citas_devuelven_401()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/citas", Reserva("88080001", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/agenda/franjas?vacuna=SPR")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/citas?documento=88080001")).StatusCode);
    }
}
