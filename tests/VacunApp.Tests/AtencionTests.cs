using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// RF-07 / CU07: el vacunador atiende la cita del día (registra la dosis con las validaciones de T3 y marca la cita)
/// o registra la inasistencia (RN-18), que no consume stock. Alcance: solo su establecimiento (RN-22).
/// </summary>
[Collection("api")]
public class AtencionTests(AppFactory app) : IAsyncLifetime
{
    // Pacientes de estas pruebas: DNI 8810xxxx. Las franjas se rastrean por id y todo se borra antes y después.
    readonly List<int> franjas = [];
    int minuto;

    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        var ids = franjas.Count == 0 ? "-1" : string.Join(',', franjas);
        await Sql($"""
            DECLARE @p TABLE (Id INT);
            DECLARE @d TABLE (Id BIGINT);
            INSERT @p SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8810%';
            INSERT @d SELECT IdDosis FROM vac.DosisAplicada WHERE IdPaciente IN (SELECT Id FROM @p);
            DELETE vac.Cita WHERE IdPaciente IN (SELECT Id FROM @p) OR IdHorario IN ({ids});
            DELETE vac.DosisAplicada WHERE IdDosis IN (SELECT Id FROM @d);
            DELETE vac.AuditoriaDosis WHERE IdDosis IN (SELECT Id FROM @d);
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

    static async Task<int> Establecimiento(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/yo")).GetProperty("idEstablecimiento").GetInt32();

    async Task<int> OtroEstablecimiento(int mio) =>
        (short)(await Sql("SELECT TOP (1) IdEstablecimiento FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @m ORDER BY IdEstablecimiento", ("@m", mio)))!;

    async Task NuevoPaciente(string dni) =>
        await Sql("""
            INSERT vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, FechaNacimiento, Sexo, IdDistrito)
            SELECT 'DNI', @d, 'Prueba', 'Atencion', DATEADD(MONTH, -20, CAST(GETDATE() AS DATE)), 'M', MIN(IdDistrito) FROM vac.Distrito
            """, ("@d", dni));

    // Franja de la vacuna SPR en la fecha dada (días respecto de hoy) a una hora única por prueba; se inserta directo
    // porque las reglas de creación exigen una hora futura y aquí necesitamos "hoy" y "ayer".
    async Task<int> Franja(int est, int dias)
    {
        var id = (int)(await Sql("""
            INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
            SELECT @e, IdVacuna, DATEADD(MINUTE, @m, DATEADD(DAY, @d, CAST(CAST(GETDATE() AS DATE) AS DATETIME2(0)))), 5 FROM vac.Vacuna WHERE Codigo = 'SPR';
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@e", est), ("@d", dias), ("@m", 10 + ++minuto)))!;
        franjas.Add(id);
        return id;
    }

    async Task<int> CitaEn(string dni, int idHorario) =>
        (int)(await Sql("""
            INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema)
            SELECT @h, p.IdPaciente, e.IdEsquema FROM vac.Paciente p, vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
            WHERE p.NumeroDocumento = @d AND v.Codigo = 'SPR' AND e.NumeroDosis = 1;
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, ("@h", idHorario), ("@d", dni)))!;

    async Task<string> Estado(int idCita) => (string)(await Sql("SELECT Estado FROM vac.Cita WHERE IdCita = @i", ("@i", idCita)))!;

    async Task<int> Dosis(string dni) =>
        (int)(await Sql("SELECT COUNT(*) FROM vac.DosisAplicada d JOIN vac.Paciente p ON p.IdPaciente = d.IdPaciente WHERE p.NumeroDocumento = @d", ("@d", dni)))!;

    async Task<int> Stock(int est, string lote) =>
        (int)(await Sql("SELECT s.Cantidad FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE s.IdEstablecimiento = @e AND l.NumeroLote = @l", ("@e", est), ("@l", lote)))!;

    async Task<string> Lote(int est) =>
        (string)(await Sql("""
            SELECT TOP (1) l.NumeroLote FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
            WHERE s.IdEstablecimiento = @e AND v.Codigo = 'SPR' AND s.Cantidad > 5 AND l.FechaVencimiento >= CAST(GETDATE() AS DATE)
            ORDER BY l.FechaVencimiento DESC
            """, ("@e", est)))!;

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    static JsonContent Atender(string lote) => JsonContent.Create(new { numeroLote = lote });

    // ---- Atender ----

    [Fact]
    public async Task Atender_registra_la_dosis_con_el_vacunador_de_la_sesion_descuenta_stock_y_marca_la_cita()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));
        var antes = await Stock(est, lote);

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ATENDIDA", await Estado(cita));
        Assert.Equal(1, await Dosis("88100001"));
        Assert.Equal(antes - 1, await Stock(est, lote));
        Assert.Equal("vac01", (string)(await Sql("""
            SELECT u.NombreUsuario FROM vac.DosisAplicada d JOIN vac.Paciente p ON p.IdPaciente = d.IdPaciente
            JOIN vac.Usuario u ON u.IdVacunador = d.IdVacunador WHERE p.NumeroDocumento = '88100001'
            """))!);
        Assert.Equal(est, (short)(await Sql("SELECT d.IdEstablecimiento FROM vac.DosisAplicada d JOIN vac.Paciente p ON p.IdPaciente = d.IdPaciente WHERE p.NumeroDocumento = '88100001'"))!);
    }

    [Fact]
    public async Task Sin_stock_la_dosis_falla_y_la_cita_queda_programada()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));
        var antes = await Stock(est, lote);
        await Sql("UPDATE s SET Cantidad = 0 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE s.IdEstablecimiento = @e AND l.NumeroLote = @l", ("@e", est), ("@l", lote));
        try
        {
            var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("stock", await Error(r));
            Assert.Equal("PROGRAMADA", await Estado(cita));
            Assert.Equal(0, await Dosis("88100001"));
            Assert.Equal(0, await Stock(est, lote));
        }
        finally
        {
            await Sql("UPDATE s SET Cantidad = @c FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE s.IdEstablecimiento = @e AND l.NumeroLote = @l", ("@e", est), ("@l", lote), ("@c", antes));
        }
    }

    [Fact]
    public async Task Un_lote_inexistente_se_rechaza_con_la_validacion_de_la_dosis_y_la_cita_no_cambia()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender("NO-EXISTE"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("lote", await Error(r));
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task Si_la_dosis_ya_fue_registrada_no_se_duplica_y_la_cita_no_cambia()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));
        // Dosis registrada por el camino directo (RF-04) antes de atender la cita.
        var dni = (string)(await Sql("SELECT v.Dni FROM vac.Usuario u JOIN vac.Vacunador v ON v.IdVacunador = u.IdVacunador WHERE u.NombreUsuario = 'vac01'"))!;
        await Sql("DECLARE @d BIGINT; EXEC vac.usp_RegistrarDosis '88100001', 'SPR', 1, @l, @e, @v, NULL, NULL, @d OUTPUT;", ("@l", lote), ("@e", est), ("@v", dni));

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
        Assert.Equal(1, await Dosis("88100001"));
    }

    [Fact]
    public async Task Solo_se_atiende_el_dia_de_la_franja()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 2));

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("día", await Error(r));
        Assert.Equal("PROGRAMADA", await Estado(cita));
        Assert.Equal(0, await Dosis("88100001"));
    }

    [Fact]
    public async Task Una_cita_ya_atendida_no_se_atiende_dos_veces()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));
        (await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote))).EnsureSuccessStatusCode();
        var stock = await Stock(est, lote);

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1, await Dosis("88100001"));
        Assert.Equal(stock, await Stock(est, lote));
    }

    [Fact]
    public async Task Una_cita_de_otro_establecimiento_o_inexistente_recibe_el_mismo_403()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(await OtroEstablecimiento(est), 0));

        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsync("/api/citas/2147000000/atender", Atender(lote))).StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
        Assert.Equal(0, await Dosis("88100001"));
    }

    [Theory]
    [InlineData("ciud01")]
    [InlineData("jefe01")]
    [InlineData("epi01")]
    public async Task Solo_el_vacunador_atiende_y_registra_inasistencia(string usuario)
    {
        var vac = await app.SesionComoAsync("vac01");
        var otro = await app.SesionComoAsync(usuario);
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, -1));

        Assert.Equal(HttpStatusCode.Forbidden, (await otro.PostAsync($"/api/citas/{cita}/atender", Atender("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otro.PostAsync($"/api/citas/{cita}/inasistencia", null)).StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task Sin_sesion_no_se_atiende()
    {
        var anonimo = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync("/api/citas/1/atender", Atender("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync("/api/citas/1/inasistencia", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/citas/dia")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LOTE-DEMASIADO-LARGO-PARA-LA-COLUMNA")]
    public async Task El_numero_de_lote_es_obligatorio_y_acotado(string lote)
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 0));

        var r = await vac.PostAsync($"/api/citas/{cita}/atender", Atender(lote));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    // ---- Inasistencia ----

    [Fact]
    public async Task La_inasistencia_marca_la_cita_crea_la_alerta_y_no_consume_stock()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        await Sql("UPDATE a SET Estado = 'DESCARTADA', FechaAtencion = SYSDATETIME() FROM vac.Alerta a JOIN vac.Paciente p ON p.IdPaciente = a.IdPaciente WHERE p.NumeroDocumento = '88100001' AND a.Estado = 'PENDIENTE'");
        var cita = await CitaEn("88100001", await Franja(est, -1));
        var antes = await Stock(est, lote);

        var r = await vac.PostAsync($"/api/citas/{cita}/inasistencia", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alertaCreada").GetBoolean());
        Assert.Equal("NO_ASISTIO", await Estado(cita));
        Assert.Equal(1, (int)(await Sql("SELECT COUNT(*) FROM vac.Alerta a JOIN vac.Paciente p ON p.IdPaciente = a.IdPaciente WHERE p.NumeroDocumento = '88100001' AND a.TipoAlerta = 'INASISTENCIA' AND a.Estado = 'PENDIENTE'"))!);
        Assert.Equal(0, await Dosis("88100001"));
        Assert.Equal(antes, await Stock(est, lote));
    }

    [Fact]
    public async Task Despues_de_una_inasistencia_el_paciente_puede_volver_a_reservar_la_dosis()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, -1));
        (await vac.PostAsync($"/api/citas/{cita}/inasistencia", null)).EnsureSuccessStatusCode();

        var otra = await CitaEn("88100001", await Franja(est, 3));

        Assert.Equal("PROGRAMADA", await Estado(otra));
    }

    [Fact]
    public async Task No_se_registra_inasistencia_antes_de_la_hora_de_la_cita()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(est, 2));

        var r = await vac.PostAsync($"/api/citas/{cita}/inasistencia", null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("hora", await Error(r));
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    [Fact]
    public async Task La_inasistencia_de_otro_establecimiento_o_inexistente_recibe_el_mismo_403()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        await NuevoPaciente("88100001");
        var cita = await CitaEn("88100001", await Franja(await OtroEstablecimiento(est), -1));

        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsync($"/api/citas/{cita}/inasistencia", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vac.PostAsync("/api/citas/2147000000/inasistencia", null)).StatusCode);
        Assert.Equal("PROGRAMADA", await Estado(cita));
    }

    // ---- Citas del día ----

    [Fact]
    public async Task Las_citas_del_dia_son_solo_las_del_establecimiento_del_vacunador_y_traen_los_lotes_con_existencias()
    {
        var vac = await app.SesionComoAsync("vac01");
        var est = await Establecimiento(vac);
        var lote = await Lote(est);
        await NuevoPaciente("88100001");
        await NuevoPaciente("88100002");
        await NuevoPaciente("88100003");
        var mia = await CitaEn("88100001", await Franja(est, 0));
        var ajena = await CitaEn("88100002", await Franja(await OtroEstablecimiento(est), 0));
        var otroDia = await CitaEn("88100003", await Franja(est, 1));

        var r = await vac.GetFromJsonAsync<JsonElement>($"/api/citas/dia?fecha={DateTime.Today:yyyy-MM-dd}");

        var ids = r.GetProperty("citas").EnumerateArray().Select(c => c.GetProperty("idCita").GetInt32()).ToList();
        Assert.Contains(mia, ids);
        Assert.DoesNotContain(ajena, ids);
        Assert.DoesNotContain(otroDia, ids);
        Assert.Contains(r.GetProperty("lotes").EnumerateArray(), l => l.GetProperty("numeroLote").GetString() == lote);
    }

    [Fact]
    public async Task Las_citas_del_dia_validan_la_fecha()
    {
        var vac = await app.SesionComoAsync("vac01");
        Assert.Equal(HttpStatusCode.BadRequest, (await vac.GetAsync("/api/citas/dia?fecha=ayer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vac.GetAsync("/api/citas/dia")).StatusCode);
    }

    [Theory]
    [InlineData("ciud01")]
    [InlineData("jefe01")]
    public async Task Las_citas_del_dia_son_solo_para_el_vacunador(string usuario)
    {
        var otro = await app.SesionComoAsync(usuario);
        Assert.Equal(HttpStatusCode.Forbidden, (await otro.GetAsync("/api/citas/dia")).StatusCode);
    }
}
