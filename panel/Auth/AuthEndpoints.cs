using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Auth;

static class AuthEndpoints
{
    public const string ClaimEstablecimiento = "est";
    const string MensajeCredenciales = "Usuario o contraseña incorrectos.";

    public static IServiceCollection AddAutenticacion(this IServiceCollection services, IWebHostEnvironment env)
    {
        services.AddSingleton<IPasswordHasher<CuentaUsuario>, PasswordHasher<CuentaUsuario>>();
        services.AddSingleton<UsuarioRepo>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = ".VacunApp.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = false;
                // Es una API: sin sesión se responde 401/403, nunca una redirección HTML.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });
        services.AddAuthorization();
        return services;
    }

    public static void MapAutenticacion(this IEndpointRouteBuilder app)
    {
        // Hash de una clave descartable: se verifica aunque el usuario no exista, para que
        // "usuario inexistente" y "clave incorrecta" tarden lo mismo y respondan igual.
        var hashFalso = new PasswordHasher<CuentaUsuario>().HashPassword(null!, Guid.NewGuid().ToString("N"));

        app.MapPost("/api/login", async (
            LoginRequest req, HttpContext ctx, UsuarioRepo repo, IPasswordHasher<CuentaUsuario> hasher) =>
        {
            var usuario = req.Usuario?.Trim();
            if (string.IsNullOrEmpty(usuario) || usuario.Length > 30 ||
                string.IsNullOrEmpty(req.Clave) || req.Clave.Length > PoliticaClave.Maximo)
                return Results.Json(new { error = "Ingrese usuario y contraseña válidos." }, statusCode: StatusCodes.Status400BadRequest);

            var cuenta = await repo.BuscarAsync(usuario);
            var resultado = hasher.VerifyHashedPassword(cuenta!, cuenta?.ClaveHash ?? hashFalso, req.Clave);

            if (cuenta is null || !cuenta.Activo || cuenta.ClaveHash is null || resultado == PasswordVerificationResult.Failed)
                return Results.Json(new { error = MensajeCredenciales }, statusCode: StatusCodes.Status401Unauthorized);

            if (resultado == PasswordVerificationResult.SuccessRehashNeeded)
                await repo.ActualizarHashAsync(cuenta.IdUsuario, hasher.HashPassword(cuenta, req.Clave));

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, cuenta.IdUsuario.ToString()),
                new(ClaimTypes.Name, cuenta.NombreUsuario),
                new(ClaimTypes.Role, cuenta.Rol),
            };
            if (cuenta.IdEstablecimiento is { } est) claims.Add(new Claim(ClaimEstablecimiento, est.ToString()));

            var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identidad);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Ok(Perfil(principal, cuenta.NombreCompleto));
        });

        app.MapPost("/api/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        app.MapGet("/api/yo", (ClaimsPrincipal user) => Results.Ok(Perfil(user, null))).RequireAuthorization();
    }

    static object Perfil(ClaimsPrincipal user, string? nombreCompleto) => new
    {
        usuario = user.Identity?.Name,
        nombre = nombreCompleto,
        rol = user.FindFirstValue(ClaimTypes.Role),
        idEstablecimiento = int.TryParse(user.FindFirstValue(ClaimEstablecimiento), out var e) ? (int?)e : null,
    };
}

record LoginRequest(string? Usuario, string? Clave);
