using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>Matriz rol × endpoint (RN-22): se niega por defecto y solo se permite lo necesario para el rol.</summary>
[Collection("api")]
public class AutorizacionTests(AppFactory app)
{
    // Endpoints de consulta regional: solo epidemiólogo y administrador. Alertas y pendientes ya no están aquí:
    // desde T5.2 también los consulta el personal de establecimiento, con alcance (AlertasTests).
    [Theory]
    [InlineData("/api/resumen")]
    [InlineData("/api/sarampion")]
    [InlineData("/api/cobertura")]
    [InlineData("/api/campanas")]
    [InlineData("/api/dashboard")]
    public async Task Consultas_regionales_solo_para_admin_y_epidemiologo(string url)
    {
        foreach (var (usuario, esperado) in new[]
        {
            ("admin", HttpStatusCode.OK), ("epi01", HttpStatusCode.OK),
            ("jefe01", HttpStatusCode.Forbidden), ("vac01", HttpStatusCode.Forbidden), ("ciud01", HttpStatusCode.Forbidden),
        })
        {
            var c = await app.SesionComoAsync(usuario);
            Assert.True(esperado == await EstadoAsync(c, url), $"{usuario} en {url}");
        }
    }

    // Una negativa (401/403) llega al instante. Si la autorización pasa y la consulta tarda (hoy
    // usp_ListarPendientes demora decenas de segundos), no se espera: basta saber que se ejecutó.
    static async Task<HttpStatusCode> EstadoAsync(HttpClient c, string url)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { return (await c.GetAsync(url, limite.Token)).StatusCode; }
        catch (OperationCanceledException) { return HttpStatusCode.OK; }
    }

    [Theory]
    [InlineData("/api/resumen")]
    [InlineData("/api/sarampion")]
    [InlineData("/api/cobertura")]
    [InlineData("/api/alertas")]
    [InlineData("/api/pendientes?ubigeo=230104&top=5")]
    [InlineData("/api/campanas")]
    [InlineData("/api/dashboard")]
    [InlineData("/api/paciente/70000001")]
    [InlineData("/api/catalogos")]
    public async Task Sin_sesion_todos_los_endpoints_devuelven_401(string url)
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_no_se_puede_registrar_una_dosis()
    {
        var resp = await app.CreateClient().PostAsJsonAsync("/api/dosis", CuerpoDosis(1));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Catalogos_del_vacunador_solo_incluye_personal_de_su_establecimiento()
    {
        var vac = await Catalogos("vac01");
        var yo = await (await app.SesionComoAsync("vac01")).GetFromJsonAsync<JsonElement>("/api/yo");
        var miEst = yo.GetProperty("idEstablecimiento").GetInt32();

        var personal = vac.GetProperty("personal").EnumerateArray().ToList();
        Assert.NotEmpty(personal);
        Assert.All(personal, p => Assert.Equal(miEst, p.GetProperty("IdEstablecimiento").GetInt32()));
    }

    [Fact]
    public async Task Catalogos_del_ciudadano_no_incluye_personal()
    {
        var ciud = await Catalogos("ciud01");

        Assert.Empty(ciud.GetProperty("personal").EnumerateArray());
    }

    [Fact]
    public async Task Catalogos_del_epidemiologo_incluye_personal_de_toda_la_region()
    {
        var epi = await Catalogos("epi01");

        var establecimientos = epi.GetProperty("personal").EnumerateArray()
            .Select(p => p.GetProperty("IdEstablecimiento").GetInt32()).Distinct().Count();
        Assert.True(establecimientos > 1);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("epi01")]
    [InlineData("jefe01")]
    [InlineData("ciud01")]
    public async Task Solo_el_vacunador_registra_dosis(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        var resp = await c.PostAsJsonAsync("/api/dosis", CuerpoDosis(1));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task El_vacunador_no_registra_dosis_en_otro_establecimiento()
    {
        var c = await app.SesionComoAsync("vac01");

        var resp = await c.PostAsJsonAsync("/api/dosis", CuerpoDosis(short.MaxValue));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_pierde_el_acceso_aunque_conserve_su_cookie()
    {
        var clave = "Aa1!" + Guid.NewGuid().ToString("N");
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await Ejecutar(cn, """
            INSERT vac.Usuario (NombreUsuario, NombreCompleto, ClaveHash, IdRol)
            VALUES ('t_baja', 'Temporal de prueba', @h, (SELECT IdRol FROM vac.Rol WHERE Nombre = 'EPIDEMIOLOGO'))
            """, ("@h", new PasswordHasher<object>().HashPassword(new object(), clave)));
        try
        {
            var c = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            (await c.PostAsJsonAsync("/api/login", new { usuario = "t_baja", clave })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/yo")).StatusCode);

            await Ejecutar(cn, "UPDATE vac.Usuario SET Activo = 0 WHERE NombreUsuario = 't_baja'");

            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/yo")).StatusCode);
        }
        finally { await Ejecutar(cn, "DELETE vac.Usuario WHERE NombreUsuario = 't_baja'"); }
    }

    async Task<JsonElement> Catalogos(string usuario) =>
        await (await app.SesionComoAsync(usuario)).GetFromJsonAsync<JsonElement>("/api/catalogos");

    static object CuerpoDosis(short idEstablecimiento) => new
    {
        documento = "70000001", vacuna = "SPR", dosis = 1, lote = "X", idEstablecimiento,
        dniVacunador = "00000000", fecha = (string?)null,
    };

    static async Task Ejecutar(SqlConnection cn, string sql, params (string, object)[] args)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
