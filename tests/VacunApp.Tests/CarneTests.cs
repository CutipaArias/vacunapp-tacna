using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>RF-15 / CU14: carné de vacunación. El ciudadano solo ve a sus hijos vinculados (RN-17).</summary>
[Collection("api")]
public class CarneTests(AppFactory app)
{
    async Task<List<string>> Documentos(string sql)
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        var lista = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) lista.Add(rd.GetString(0));
        return lista;
    }

    Task<List<string>> Vinculados() => Documentos(
        "SELECT p.NumeroDocumento FROM vac.VinculoFamiliar v JOIN vac.Usuario u ON u.IdUsuario = v.IdUsuario " +
        "JOIN vac.Paciente p ON p.IdPaciente = v.IdPaciente WHERE u.NombreUsuario = 'ciud01' ORDER BY 1");

    async Task<string> NoVinculado() => (await Documentos(
        "SELECT TOP (1) p.NumeroDocumento FROM vac.Paciente p WHERE p.IdPaciente NOT IN " +
        "(SELECT IdPaciente FROM vac.VinculoFamiliar) ORDER BY p.IdPaciente")).Single();

    [Fact]
    public async Task El_ciudadano_ve_el_carne_de_un_hijo_vinculado()
    {
        var c = await app.SesionComoAsync("ciud01");
        var hijo = (await Vinculados()).First();

        var resp = await c.GetAsync("/api/paciente/" + hijo);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(hijo, json.GetProperty("datos").GetProperty("NumeroDocumento").GetString());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("aplicadas").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("pendientes").ValueKind);
    }

    [Fact]
    public async Task El_ciudadano_no_ve_el_carne_de_un_paciente_no_vinculado()
    {
        var c = await app.SesionComoAsync("ciud01");

        var resp = await c.GetAsync("/api/paciente/" + await NoVinculado());

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Para_el_ciudadano_un_documento_inexistente_responde_igual_que_uno_ajeno()
    {
        var c = await app.SesionComoAsync("ciud01");

        var ajeno = await c.GetAsync("/api/paciente/" + await NoVinculado());
        var inexistente = await c.GetAsync("/api/paciente/00000001");

        Assert.Equal(HttpStatusCode.Forbidden, inexistente.StatusCode);   // no revela qué DNI existen
        Assert.Equal(ajeno.StatusCode, inexistente.StatusCode);
        Assert.Equal(await ajeno.Content.ReadAsStringAsync(), await inexistente.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mis_pacientes_lista_solo_los_hijos_vinculados()
    {
        var c = await app.SesionComoAsync("ciud01");

        var lista = await c.GetFromJsonAsync<JsonElement>("/api/mis-pacientes");

        var docs = lista.EnumerateArray().Select(p => p.GetProperty("numeroDocumento").GetString()!).OrderBy(x => x).ToList();
        Assert.Equal(await Vinculados(), docs);
        Assert.All(lista.EnumerateArray(), p => Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("nombre").GetString())));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    [InlineData("jefe01")]
    [InlineData("vac01")]
    public async Task Mis_pacientes_es_solo_para_el_ciudadano(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/mis-pacientes")).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_mis_pacientes_devuelve_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/mis-pacientes")).StatusCode);
    }

    [Theory]
    [InlineData("vac01")]
    [InlineData("jefe01")]
    [InlineData("epi01")]
    [InlineData("admin")]
    public async Task El_personal_ve_el_carne_de_cualquier_paciente(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/paciente/" + await NoVinculado())).StatusCode);
    }

    [Fact]
    public async Task Para_el_personal_un_documento_inexistente_devuelve_400_con_mensaje()
    {
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.GetAsync("/api/paciente/00000001");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("no registrado", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Un_documento_con_texto_SQL_se_trata_como_un_valor_y_no_se_ejecuta()
    {
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.GetAsync("/api/paciente/" + Uri.EscapeDataString("' OR 1=1; DROP TABLE vac.Paciente;--"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True((int)(await ContarPacientes()) > 1000);   // la tabla sigue intacta
    }

    async Task<int> ContarPacientes()
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM vac.Paciente";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
