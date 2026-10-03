using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

[Collection("api")]
public class AuthTests(AppFactory app)
{
    HttpClient NuevoCliente() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    static Task<HttpResponseMessage> Login(HttpClient c, string usuario, string clave) =>
        c.PostAsJsonAsync("/api/login", new { usuario, clave });

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // Crea un usuario temporal con un hash válido (o sin clave) y lo elimina al terminar.
    async Task ConUsuarioTemporal(string nombre, bool activo, string? clave, Func<Task> prueba)
    {
        string? hash = clave is null ? null : new PasswordHasher<object>().HashPassword(new object(), clave);
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using (var ins = cn.CreateCommand())
        {
            ins.CommandText = """
                INSERT vac.Usuario (NombreUsuario, NombreCompleto, ClaveHash, IdRol, Activo)
                VALUES (@n, 'Temporal de prueba', @h, (SELECT IdRol FROM vac.Rol WHERE Nombre = 'EPIDEMIOLOGO'), @a)
                """;
            ins.Parameters.AddWithValue("@n", nombre);
            ins.Parameters.AddWithValue("@h", (object?)hash ?? DBNull.Value);
            ins.Parameters.AddWithValue("@a", activo);
            await ins.ExecuteNonQueryAsync();
        }
        try { await prueba(); }
        finally
        {
            await using var del = cn.CreateCommand();
            del.CommandText = "DELETE vac.Usuario WHERE NombreUsuario = @n";
            del.Parameters.AddWithValue("@n", nombre);
            await del.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Login_correcto_devuelve_200_con_cookie_HttpOnly_y_SameSite_Strict()
    {
        var resp = await Login(NuevoCliente(), "admin", app.ClaveAdmin);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"), c => c.Contains(".VacunApp", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_con_clave_incorrecta_devuelve_401_con_mensaje_generico()
    {
        var resp = await Login(NuevoCliente(), "admin", "clave-equivocada");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("Usuario o contraseña incorrectos.", (await Json(resp)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_con_usuario_inexistente_responde_igual_que_con_clave_incorrecta()
    {
        var inexistente = await Login(NuevoCliente(), "no_existe", "cualquiera");
        var claveMala = await Login(NuevoCliente(), "admin", "clave-equivocada");

        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(await claveMala.Content.ReadAsStringAsync(), await inexistente.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_de_usuario_inactivo_devuelve_401()
    {
        var clave = "Aa1!" + Guid.NewGuid().ToString("N");
        await ConUsuarioTemporal("t_inactivo", activo: false, clave, async () =>
        {
            var resp = await Login(NuevoCliente(), "t_inactivo", clave);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        });
    }

    [Fact]
    public async Task Login_de_usuario_sin_clave_asignada_devuelve_401()
    {
        await ConUsuarioTemporal("t_sinclave", activo: true, clave: null, async () =>
        {
            var resp = await Login(NuevoCliente(), "t_sinclave", "");
            Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest);
            var resp2 = await Login(NuevoCliente(), "t_sinclave", "algo");
            Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);
        });
    }

    [Fact]
    public async Task Login_rechaza_datos_vacios_o_demasiado_largos_con_400()
    {
        var c = NuevoCliente();

        Assert.Equal(HttpStatusCode.BadRequest, (await Login(c, "", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(c, "admin", "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(c, "admin", new string('a', 10_000))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(c, new string('u', 200), "x")).StatusCode);
    }

    [Fact]
    public async Task Yo_sin_sesion_devuelve_401()
    {
        var resp = await NuevoCliente().GetAsync("/api/yo");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Yo_con_sesion_devuelve_rol_y_establecimiento()
    {
        var c = NuevoCliente();
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "jefe01", app.ClaveGeneral)).StatusCode);

        var yo = await Json(await c.GetAsync("/api/yo"));

        Assert.Equal("jefe01", yo.GetProperty("usuario").GetString());
        Assert.Equal("JEFE_ESTABLECIMIENTO", yo.GetProperty("rol").GetString());
        Assert.True(yo.GetProperty("idEstablecimiento").GetInt32() > 0);
        Assert.False(yo.TryGetProperty("claveHash", out _));
    }

    [Fact]
    public async Task Yo_de_un_rol_regional_no_tiene_establecimiento()
    {
        var c = NuevoCliente();
        await Login(c, "epi01", app.ClaveGeneral);

        var yo = await Json(await c.GetAsync("/api/yo"));

        Assert.Equal("EPIDEMIOLOGO", yo.GetProperty("rol").GetString());
        Assert.Equal(JsonValueKind.Null, yo.GetProperty("idEstablecimiento").ValueKind);
    }

    [Fact]
    public async Task Logout_cierra_la_sesion()
    {
        var c = NuevoCliente();
        await Login(c, "vac01", app.ClaveGeneral);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/yo")).StatusCode);

        var salida = await c.PostAsync("/api/logout", content: null);

        Assert.True(salida.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/yo")).StatusCode);
    }

    [Fact]
    public async Task La_clave_se_guarda_solo_como_hash_PBKDF2_de_PasswordHasher()
    {
        app.CreateClient();   // el arranque de la API asigna los hashes
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT ClaveHash FROM vac.Usuario WHERE NombreUsuario = 'admin'";
        var hash = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(hash);
        Assert.DoesNotContain(app.ClaveAdmin, hash);
        Assert.StartsWith("AQAAAA", hash);   // formato v3 de PasswordHasher: PBKDF2 con sal incluida
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<object>().VerifyHashedPassword(new object(), hash, app.ClaveAdmin));
    }

    [Fact]
    public async Task El_arranque_asigna_hash_a_todos_los_usuarios_semilla_configurados()
    {
        app.CreateClient();   // el arranque de la API asigna los hashes
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM vac.Usuario WHERE NombreUsuario IN ('admin','epi01','jefe01','vac01','ciud01') AND ClaveHash IS NULL";
        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }
}
