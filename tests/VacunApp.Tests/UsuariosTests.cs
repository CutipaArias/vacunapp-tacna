using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

[Collection("api")]
public class UsuariosTests(AppFactory app) : IAsyncLifetime
{
    // Todos los usuarios que crean estas pruebas empiezan con t_u_ y se borran antes y después.
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar()
    {
        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "DELETE vac.Usuario WHERE NombreUsuario LIKE 't[_]u[_]%'";
        await cmd.ExecuteNonQueryAsync();
    }

    static string ClaveNueva() => "Aa1!" + Guid.NewGuid().ToString("N");
    static string NombreNuevo() => "t_u_" + Guid.NewGuid().ToString("N")[..8];

    static object Epidemiologo(string usuario, string clave, string rol = "EPIDEMIOLOGO", int? est = null) =>
        new { usuario, nombre = "Usuario de prueba", clave, rol, idEstablecimiento = est, idVacunador = (int?)null };

    async Task<HttpClient> Admin() => await app.SesionComoAsync("admin");

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task El_administrador_crea_un_usuario_que_puede_iniciar_sesion()
    {
        var nombre = NombreNuevo();
        var clave = ClaveNueva();

        var resp = await (await Admin()).PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, clave));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True((await Json(resp)).GetProperty("idUsuario").GetInt32() > 0);
        var nuevo = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await nuevo.PostAsJsonAsync("/api/login", new { usuario = nombre, clave })).StatusCode);
        Assert.Equal("EPIDEMIOLOGO", (await Json(await nuevo.GetAsync("/api/yo"))).GetProperty("rol").GetString());
    }

    [Fact]
    public async Task La_clave_del_usuario_creado_se_guarda_solo_como_hash()
    {
        var nombre = NombreNuevo();
        var clave = ClaveNueva();
        await (await Admin()).PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, clave));

        await using var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT ClaveHash FROM vac.Usuario WHERE NombreUsuario = @n";
        cmd.Parameters.AddWithValue("@n", nombre);
        var hash = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(hash);
        Assert.DoesNotContain(clave, hash);
        Assert.StartsWith("AQAAAA", hash);
    }

    [Fact]
    public async Task Un_nombre_de_usuario_repetido_se_rechaza_con_400_y_mensaje()
    {
        var nombre = NombreNuevo();
        var admin = await Admin();
        await admin.PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, ClaveNueva()));

        var resp = await admin.PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre.ToUpperInvariant(), ClaveNueva()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Ya existe", (await Json(resp)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Una_clave_debil_se_rechaza_con_la_politica_de_claves()
    {
        var resp = await (await Admin()).PostAsJsonAsync("/api/usuarios", Epidemiologo(NombreNuevo(), "corta"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("contraseña", (await Json(resp)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Un_rol_inexistente_se_rechaza()
    {
        var resp = await (await Admin()).PostAsJsonAsync("/api/usuarios", Epidemiologo(NombreNuevo(), ClaveNueva(), rol: "SUPERUSUARIO"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Un_jefe_sin_establecimiento_se_rechaza()
    {
        var resp = await (await Admin()).PostAsJsonAsync("/api/usuarios",
            Epidemiologo(NombreNuevo(), ClaveNueva(), rol: "JEFE_ESTABLECIMIENTO", est: null));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("establecimiento", (await Json(resp)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("x'; DROP TABLE vac.Usuario;--")]
    [InlineData("a b")]
    [InlineData("ab")]
    public async Task Un_nombre_de_usuario_con_caracteres_no_permitidos_se_rechaza(string nombre)
    {
        var resp = await (await Admin()).PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, ClaveNueva()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("epi01")]
    [InlineData("jefe01")]
    [InlineData("vac01")]
    [InlineData("ciud01")]
    public async Task Solo_el_administrador_gestiona_usuarios(string usuario)
    {
        var c = await app.SesionComoAsync(usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await c.PostAsJsonAsync("/api/usuarios", Epidemiologo(NombreNuevo(), ClaveNueva()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await c.PutAsJsonAsync("/api/usuarios/1", new { nombre = "x", rol = "ADMINISTRADOR", activo = true })).StatusCode);
    }

    [Fact]
    public async Task Sin_sesion_la_gestion_de_usuarios_devuelve_401()
    {
        var anonimo = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonimo.PostAsJsonAsync("/api/usuarios", Epidemiologo(NombreNuevo(), ClaveNueva()))).StatusCode);
    }

    [Fact]
    public async Task El_listado_no_expone_hashes_de_contraseña()
    {
        var resp = await (await Admin()).GetAsync("/api/usuarios");
        var texto = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"nombreUsuario\":\"admin\"", texto);
        Assert.DoesNotContain("hash", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", texto);
    }

    [Fact]
    public async Task Desactivar_un_usuario_le_impide_iniciar_sesion()
    {
        var nombre = NombreNuevo();
        var clave = ClaveNueva();
        var admin = await Admin();
        var id = (await Json(await admin.PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, clave)))).GetProperty("idUsuario").GetInt32();

        var resp = await admin.PutAsJsonAsync($"/api/usuarios/{id}",
            new { nombre = "Usuario de prueba", rol = "EPIDEMIOLOGO", idEstablecimiento = (int?)null, idVacunador = (int?)null, activo = false });

        Assert.True(resp.IsSuccessStatusCode);
        var intento = await app.CreateClient().PostAsJsonAsync("/api/login", new { usuario = nombre, clave });
        Assert.Equal(HttpStatusCode.Unauthorized, intento.StatusCode);
    }

    [Fact]
    public async Task El_administrador_no_puede_desactivar_su_propia_cuenta()
    {
        var admin = await Admin();
        var yo = await Json(await admin.GetAsync("/api/usuarios"));
        var id = yo.EnumerateArray().First(u => u.GetProperty("nombreUsuario").GetString() == "admin").GetProperty("idUsuario").GetInt32();

        var resp = await admin.PutAsJsonAsync($"/api/usuarios/{id}",
            new { nombre = "Administrador del sistema", rol = "ADMINISTRADOR", idEstablecimiento = (int?)null, idVacunador = (int?)null, activo = false });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/yo")).StatusCode);
    }

    [Fact]
    public async Task Se_puede_reasignar_rol_y_establecimiento()
    {
        var nombre = NombreNuevo();
        var admin = await Admin();
        var id = (await Json(await admin.PostAsJsonAsync("/api/usuarios", Epidemiologo(nombre, ClaveNueva())))).GetProperty("idUsuario").GetInt32();
        var jefe = await Json(await (await app.SesionComoAsync("jefe01")).GetAsync("/api/yo"));
        var est = jefe.GetProperty("idEstablecimiento").GetInt32();

        var resp = await admin.PutAsJsonAsync($"/api/usuarios/{id}",
            new { nombre = "Ahora es jefe", rol = "JEFE_ESTABLECIMIENTO", idEstablecimiento = est, idVacunador = (int?)null, activo = true });

        Assert.True(resp.IsSuccessStatusCode);
        var fila = (await Json(await admin.GetAsync("/api/usuarios"))).EnumerateArray().First(u => u.GetProperty("idUsuario").GetInt32() == id);
        Assert.Equal("JEFE_ESTABLECIMIENTO", fila.GetProperty("rol").GetString());
        Assert.Equal(est, fila.GetProperty("idEstablecimiento").GetInt32());
    }
}
