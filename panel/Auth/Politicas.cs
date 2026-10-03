using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace VacunApp.Panel.Auth;

/// <summary>Nombres de rol tal como están en vac.Rol.</summary>
static class Roles
{
    public const string Administrador = "ADMINISTRADOR";
    public const string Epidemiologo = "EPIDEMIOLOGO";
    public const string Jefe = "JEFE_ESTABLECIMIENTO";
    public const string Vacunador = "VACUNADOR";
    public const string Ciudadano = "CIUDADANO";
}

static class Politicas
{
    /// <summary>Alcance regional (toda la región de Tacna): administrador y epidemiólogo.</summary>
    public const string Regional = nameof(Regional);
    /// <summary>Quien aplica o supervisa la vacunación en un establecimiento: vacunador, jefe y los roles regionales.</summary>
    public const string ConsultaClinica = nameof(ConsultaClinica);
    public const string Vacunador = nameof(Vacunador);
    public const string Administrador = nameof(Administrador);

    public static void Configurar(AuthorizationOptions o)
    {
        // Negar por defecto: todo endpoint sin política explícita exige, al menos, una sesión iniciada.
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        o.AddPolicy(Regional, p => p.RequireRole(Roles.Administrador, Roles.Epidemiologo));
        o.AddPolicy(ConsultaClinica, p => p.RequireRole(Roles.Administrador, Roles.Epidemiologo, Roles.Jefe, Roles.Vacunador));
        o.AddPolicy(Vacunador, p => p.RequireRole(Roles.Vacunador));
        o.AddPolicy(Administrador, p => p.RequireRole(Roles.Administrador));
    }
}

/// <summary>RN-22: vacunador y jefe solo ven y modifican su establecimiento; administrador y epidemiólogo, toda la región.</summary>
static class Alcance
{
    public static bool EsRegional(ClaimsPrincipal u) =>
        u.IsInRole(Roles.Administrador) || u.IsInRole(Roles.Epidemiologo);

    public static short? Establecimiento(ClaimsPrincipal u) =>
        short.TryParse(u.FindFirstValue(AuthEndpoints.ClaimEstablecimiento), out var e) ? e : null;

    /// <summary>¿Puede el usuario operar sobre este establecimiento?</summary>
    public static bool PuedeEstablecimiento(ClaimsPrincipal u, short idEstablecimiento) =>
        EsRegional(u) || (Establecimiento(u) is { } mio && mio == idEstablecimiento);
}
