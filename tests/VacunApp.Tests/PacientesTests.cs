using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-03 / CU03: registro de pacientes por el vacunador (RN-01, RN-02).</summary>
[Collection("api")]
public class PacientesTests(AppFactory app) : IAsyncLifetime
{
    // Los pacientes de estas pruebas usan DNI 8800xxxx y se borran antes y después.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            DECLARE @ids TABLE (Id INT);
            INSERT @ids SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento LIKE '8800%';
            DELETE vac.Alerta WHERE IdPaciente IN (SELECT Id FROM @ids);
            DELETE vac.VinculoFamiliar WHERE IdPaciente IN (SELECT Id FROM @ids);
            DELETE vac.Paciente WHERE IdPaciente IN (SELECT Id FROM @ids);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    static string DniNuevo() => "8800" + Random.Shared.Next(0, 10_000).ToString("D4");

    static object Paciente(string? dni = null, string ubigeo = "230101", string fecha = "2025-03-10",
        string nombres = "Camila Prueba", string paterno = "Quispe", string sexo = "F", string tipo = "DNI") => new
    {
        tipoDocumento = tipo, numeroDocumento = dni ?? DniNuevo(), nombres, apellidoPaterno = paterno, apellidoMaterno = "Mamani",
        fechaNacimiento = fecha, sexo, ubigeo, direccion = "Av. Prueba 123", telefono = "952000111",
    };

    static async Task<string> Error(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    [Fact]
    public async Task El_vacunador_registra_un_paciente_y_queda_guardado()
    {
        var dni = DniNuevo();
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(dni));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("idPaciente").GetInt32() > 0);
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT Nombres FROM vac.Paciente WHERE NumeroDocumento = @d";
        cmd.Parameters.AddWithValue("@d", dni);
        Assert.Equal("Camila Prueba", (string?)await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Un_documento_repetido_se_rechaza_con_mensaje()
    {
        var dni = DniNuevo();
        var c = await app.SesionComoAsync("vac01");
        await c.PostAsJsonAsync("/api/pacientes", Paciente(dni));

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(dni));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Ya existe un paciente", await Error(resp));
    }

    [Fact]
    public async Task Una_fecha_de_nacimiento_futura_se_rechaza()
    {
        var c = await app.SesionComoAsync("vac01");
        var manana = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(fecha: manana));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("futura", await Error(resp));
    }

    [Fact]
    public async Task Un_ubigeo_inexistente_se_rechaza()
    {
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(ubigeo: "150101"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("ubigeo", await Error(resp));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345abc")]
    public async Task Un_DNI_mal_formado_se_rechaza(string dni)
    {
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(dni));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("nombres", "")]
    [InlineData("paterno", "   ")]
    [InlineData("sexo", "X")]
    [InlineData("tipo", "LIBRETA")]
    [InlineData("fecha", "1800-01-01")]
    [InlineData("fecha", "no-es-fecha")]
    public async Task Los_datos_obligatorios_o_invalidos_se_rechazan_con_400(string campo, string valor)
    {
        var c = await app.SesionComoAsync("vac01");
        var cuerpo = campo switch
        {
            "nombres" => Paciente(nombres: valor),
            "paterno" => Paciente(paterno: valor),
            "sexo" => Paciente(sexo: valor),
            "tipo" => Paciente(tipo: valor),
            _ => Paciente(fecha: valor),
        };

        var resp = await c.PostAsJsonAsync("/api/pacientes", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await Error(resp)));
    }

    [Fact]
    public async Task Los_apostrofes_y_el_texto_con_SQL_se_guardan_tal_cual_sin_ejecutarse()
    {
        var dni = DniNuevo();
        var c = await app.SesionComoAsync("vac01");
        var nombre = "O'Brien'; DROP TABLE vac.Paciente;--";

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente(dni, nombres: nombre));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT Nombres FROM vac.Paciente WHERE NumeroDocumento = @d";
        cmd.Parameters.AddWithValue("@d", dni);
        Assert.Equal(nombre, (string?)await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    [InlineData("jefe01")]
    [InlineData("ciud01")]
    public async Task Solo_el_vacunador_registra_pacientes(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        var resp = await c.PostAsJsonAsync("/api/pacientes", Paciente());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_no_se_registran_pacientes()
    {
        var resp = await app.CreateClient().PostAsJsonAsync("/api/pacientes", Paciente());

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
