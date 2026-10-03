using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace VacunApp.Tests;

/// <summary>Endurecimiento del módulo identidad: bloqueo por intentos, límite por IP, cabeceras y cookies.</summary>
[Collection("api")]
public class SeguridadTests(AppFactory app) : IAsyncLifetime
{
    public Task InitializeAsync() => Limpiar();
    public Task DisposeAsync() => Limpiar();

    async Task Limpiar() => await Sql("DELETE vac.Usuario WHERE NombreUsuario LIKE 't[_]s[_]%'");

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

    // Crea un usuario temporal (epidemiólogo) con una clave conocida.
    async Task<(string Nombre, string Clave)> NuevoUsuario()
    {
        var nombre = "t_s_" + Guid.NewGuid().ToString("N")[..8];
        var clave = "Aa1!" + Guid.NewGuid().ToString("N");
        await Sql("""
            INSERT vac.Usuario (NombreUsuario, NombreCompleto, ClaveHash, IdRol)
            VALUES (@n, 'Temporal', @h, (SELECT IdRol FROM vac.Rol WHERE Nombre = 'EPIDEMIOLOGO'))
            """, ("@n", nombre), ("@h", new PasswordHasher<object>().HashPassword(new object(), clave)));
        return (nombre, clave);
    }

    static Task<HttpResponseMessage> Login(HttpClient c, string usuario, string clave) =>
        c.PostAsJsonAsync("/api/login", new { usuario, clave });

    HttpClient Cliente() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    // ---------- Bloqueo por intentos fallidos ----------

    [Fact]
    public async Task Cinco_fallos_seguidos_bloquean_la_cuenta_incluso_con_la_clave_correcta()
    {
        var (nombre, clave) = await NuevoUsuario();
        var c = Cliente();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, nombre, "incorrecta-" + i)).StatusCode);

        var conClaveBuena = await Login(c, nombre, clave);

        Assert.Equal(HttpStatusCode.Unauthorized, conClaveBuena.StatusCode);
        Assert.NotNull(await Sql("SELECT BloqueadoHasta FROM vac.Usuario WHERE NombreUsuario = @n", ("@n", nombre)));
    }

    [Fact]
    public async Task Cuatro_fallos_no_bloquean_y_un_acierto_reinicia_el_contador()
    {
        var (nombre, clave) = await NuevoUsuario();
        var c = Cliente();
        for (var i = 0; i < 4; i++) await Login(c, nombre, "incorrecta-" + i);

        Assert.Equal(HttpStatusCode.OK, (await Login(c, nombre, clave)).StatusCode);
        Assert.Equal((byte)0, await Sql("SELECT IntentosFallidos FROM vac.Usuario WHERE NombreUsuario = @n", ("@n", nombre)));

        // tras el acierto vuelve a haber 5 intentos disponibles
        for (var i = 0; i < 4; i++) await Login(c, nombre, "incorrecta-" + i);
        Assert.Equal(HttpStatusCode.OK, (await Login(c, nombre, clave)).StatusCode);
    }

    [Fact]
    public async Task El_bloqueo_vence_y_la_cuenta_vuelve_a_funcionar()
    {
        var (nombre, clave) = await NuevoUsuario();
        var c = Cliente();
        for (var i = 0; i < 5; i++) await Login(c, nombre, "incorrecta-" + i);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, nombre, clave)).StatusCode);

        await Sql("UPDATE vac.Usuario SET BloqueadoHasta = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE NombreUsuario = @n", ("@n", nombre));

        Assert.Equal(HttpStatusCode.OK, (await Login(c, nombre, clave)).StatusCode);
    }

    [Fact]
    public async Task Una_cuenta_bloqueada_responde_igual_que_una_clave_incorrecta()
    {
        var (nombre, clave) = await NuevoUsuario();
        var c = Cliente();
        var claveMala = await (await Login(c, nombre, "incorrecta-0")).Content.ReadAsStringAsync();
        for (var i = 1; i < 5; i++) await Login(c, nombre, "incorrecta-" + i);

        var bloqueada = await (await Login(c, nombre, clave)).Content.ReadAsStringAsync();

        Assert.Equal(claveMala, bloqueada);   // no revela si la cuenta existe o está bloqueada
    }

    [Fact]
    public async Task Los_fallos_contra_un_usuario_inexistente_no_dejan_rastro_en_la_base()
    {
        var antes = await Sql("SELECT COUNT(*) FROM vac.Usuario WHERE BloqueadoHasta IS NOT NULL OR IntentosFallidos > 0");
        var c = Cliente();
        for (var i = 0; i < 6; i++) await Login(c, "no_existe_" + i, "x");

        Assert.Equal(antes, await Sql("SELECT COUNT(*) FROM vac.Usuario WHERE BloqueadoHasta IS NOT NULL OR IntentosFallidos > 0"));
    }

    // ---------- Límite por IP ----------

    [Fact]
    public async Task El_login_tiene_un_limite_de_solicitudes_por_minuto_por_IP()
    {
        using var limitada = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimit:LoginPerMinute"] = "3" })));
        var c = limitada.CreateClient();

        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) estados.Add((await Login(c, "no_existe", "x")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, estados[3]);
        Assert.Equal(HttpStatusCode.TooManyRequests, estados[4]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, estados.Take(3));
    }

    // ---------- Cabeceras y cookies ----------

    [Theory]
    [InlineData("/login.html")]
    [InlineData("/api/yo")]
    public async Task Las_respuestas_llevan_cabeceras_de_seguridad(string url)
    {
        var resp = await app.CreateClient().GetAsync(url);

        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(resp.Headers.Contains("Permissions-Policy"));
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task La_CSP_no_permite_scripts_en_linea()
    {
        var csp = (await app.CreateClient().GetAsync("/login.html")).Headers.GetValues("Content-Security-Policy").Single();
        var scriptSrc = csp.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("script-src"));

        Assert.DoesNotContain("unsafe-inline", scriptSrc);
        Assert.DoesNotContain("unsafe-eval", scriptSrc);
    }

    [Fact]
    public async Task Las_respuestas_de_la_API_no_se_guardan_en_cache()
    {
        var c = await app.SesionComoAsync("admin");

        var resp = await c.GetAsync("/api/yo");

        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Las_paginas_html_no_tienen_scripts_ni_manejadores_en_linea()
    {
        var c = app.CreateClient();
        foreach (var pagina in new[] { "/index.html", "/login.html" })
        {
            var html = await c.GetStringAsync(pagina);
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)[^>]*>", html);   // todo <script> debe traer src
            Assert.DoesNotMatch(@"\son[a-z]+\s*=", html);                // sin onclick=, onload=, ...
        }
    }

    [Fact]
    public async Task En_produccion_la_cookie_de_sesion_es_Secure()
    {
        using var produccion = app.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var c = produccion.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var resp = await Login(c, "admin", app.ClaveAdmin);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cookie = resp.Headers.GetValues("Set-Cookie").Single(x => x.Contains(".VacunApp"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Un_error_inesperado_en_produccion_no_filtra_detalles()
    {
        using var produccion = app.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var admin = produccion.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await Login(admin, "admin", app.ClaveAdmin);

        // TOP negativo: SQL Server responde con un error que no es de negocio (no es un THROW 5xxxx).
        var resp = await admin.GetAsync("/api/alertas?top=-5");
        var texto = await resp.Content.ReadAsStringAsync();

        Assert.True((int)resp.StatusCode is >= 400);
        Assert.DoesNotContain("SqlException", texto);
        Assert.DoesNotContain("   at ", texto);
        Assert.DoesNotContain("vac.", texto);
    }
}
