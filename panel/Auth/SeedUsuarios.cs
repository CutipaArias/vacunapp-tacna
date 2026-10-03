using Microsoft.AspNetCore.Identity;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Auth;

/// <summary>
/// Asigna clave a los usuarios semilla de 08_seguridad.sql al arrancar. Las claves vienen
/// solo de la configuración (Seed:AdminPassword y Seed:Password, p. ej. variables de entorno):
/// nunca del código ni del repositorio. Sin configuración, las cuentas siguen sin clave.
/// </summary>
static class SeedUsuarios
{
    static readonly string[] Cuentas = ["admin", "epi01", "jefe01", "vac01", "ciud01"];

    public static async Task EjecutarAsync(
        UsuarioRepo repo, IPasswordHasher<CuentaUsuario> hasher, IConfiguration config, ILogger log)
    {
        foreach (var nombre in Cuentas)
        {
            var cuenta = await repo.BuscarAsync(nombre);
            if (cuenta is null || cuenta.ClaveHash is not null) continue;

            var clave = nombre == "admin" ? config["Seed:AdminPassword"] : config["Seed:Password"];
            if (string.IsNullOrEmpty(clave))
            {
                log.LogWarning("Usuario {Usuario} sin clave: defina {Clave} para habilitarlo.",
                    nombre, nombre == "admin" ? "Seed:AdminPassword" : "Seed:Password");
                continue;
            }
            if (!PoliticaClave.EsValida(clave))
            {
                log.LogWarning("La clave configurada para {Usuario} no cumple la política; la cuenta sigue deshabilitada.", nombre);
                continue;
            }
            await repo.AsignarHashSiVacioAsync(cuenta.IdUsuario, hasher.HashPassword(cuenta, clave));
        }
    }
}
