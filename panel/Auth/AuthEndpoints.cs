using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Auth;

static class AuthEndpoints
{
    public const string ClaimEstablecimiento = "est";
    const string MensajeCredenciales = "Usuario o contraseña incorrectos.";

    /// <summary>Intentos fallidos seguidos que bloquean la cuenta, y duración del bloqueo (adición al spec, T1.6).</summary>
    public const int MaxIntentos = 5;
    public const int MinutosBloqueo = 15;
    public const string PoliticaLimiteLogin = "login";

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
                // Es una API: sin sesión se responde 401/403, nunca una redirección HTML (más abajo).
                // Cada solicitud revalida la cuenta: una baja o un cambio de rol/establecimiento
                // surte efecto de inmediato, sin esperar a que venza la cookie.
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var vigente = int.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                        ? await ctx.HttpContext.RequestServices.GetRequiredService<UsuarioRepo>().BuscarPorIdAsync(id)
                        : null;
                    if (vigente is null || !vigente.Activo
                        || vigente.Rol != ctx.Principal!.FindFirstValue(ClaimTypes.Role)
                        || vigente.IdEstablecimiento?.ToString() != ctx.Principal!.FindFirstValue(ClaimEstablecimiento))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            });
        services.AddAuthorization(Politicas.Configurar);

        // Límite de solicitudes de login por IP (complementa el bloqueo por cuenta). El contador vive en
        // memoria: vale para una sola instancia, que es el despliegue previsto (plan gratuito de MonsterASP.NET).
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(PoliticaLimiteLogin, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:LoginPerMinute", 20),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    public static void MapAutenticacion(this IEndpointRouteBuilder app)
    {
        // Hash de una clave descartable: se verifica aunque el usuario no exista, para que
        // "usuario inexistente" y "clave incorrecta" tarden lo mismo y respondan igual.
        var hashFalso = new PasswordHasher<CuentaUsuario>().HashPassword(null!, Guid.NewGuid().ToString("N"));

        app.MapPost("/api/login", async (
            LoginRequest req, HttpContext ctx, UsuarioRepo repo, IPasswordHasher<CuentaUsuario> hasher, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("Auth");
            var usuario = req.Usuario?.Trim();
            if (string.IsNullOrEmpty(usuario) || usuario.Length > 30 ||
                string.IsNullOrEmpty(req.Clave) || req.Clave.Length > PoliticaClave.Maximo)
                return Results.Json(new { error = "Ingrese usuario y contraseña válidos." }, statusCode: StatusCodes.Status400BadRequest);

            var cuenta = await repo.BuscarAsync(usuario);
            var resultado = hasher.VerifyHashedPassword(cuenta!, cuenta?.ClaveHash ?? hashFalso, req.Clave);
            var claveCorrecta = cuenta?.ClaveHash is not null && resultado != PasswordVerificationResult.Failed;

            // Cuenta bloqueada: se rechaza aunque la clave sea correcta, con el mismo mensaje de siempre.
            if (cuenta is { Bloqueada: true })
            {
                log.LogWarning("Intento de acceso a la cuenta bloqueada {Usuario} desde {Ip}", cuenta.NombreUsuario, ctx.Connection.RemoteIpAddress);
                return Results.Json(new { error = MensajeCredenciales }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (cuenta is null || !cuenta.Activo || !claveCorrecta)
            {
                // Solo las cuentas existentes y activas acumulan fallos; un usuario inexistente no deja rastro.
                if (cuenta is { Activo: true, ClaveHash: not null })
                    await repo.RegistrarFalloAsync(cuenta.IdUsuario, MaxIntentos, MinutosBloqueo);
                // Solo se registra el nombre de una cuenta que existe: lo escrito en un usuario inexistente podría ser
                // una contraseña pegada por error y no debe quedar en los registros.
                if (cuenta is null)
                    log.LogWarning("Acceso fallido para un usuario inexistente desde {Ip}", ctx.Connection.RemoteIpAddress);
                else
                    log.LogWarning("Acceso fallido para {Usuario} desde {Ip}", cuenta.NombreUsuario, ctx.Connection.RemoteIpAddress);
                return Results.Json(new { error = MensajeCredenciales }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await repo.RegistrarExitoAsync(cuenta.IdUsuario);
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
        }).AllowAnonymous().RequireRateLimiting(PoliticaLimiteLogin);

        app.MapPost("/api/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

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
