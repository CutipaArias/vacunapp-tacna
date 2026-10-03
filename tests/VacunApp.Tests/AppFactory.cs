using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace VacunApp.Tests;

/// <summary>
/// Levanta la API contra el SQL Server de Docker (requiere ./desplegar.ps1).
/// Las contraseñas de los usuarios semilla se generan al azar en cada ejecución y se
/// pasan por configuración: no hay ninguna contraseña escrita en el código de pruebas.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public static readonly string[] UsuariosSemilla = ["admin", "epi01", "jefe01", "vac01", "ciud01"];

    public string ClaveAdmin { get; } = NuevaClave();
    public string ClaveGeneral { get; } = NuevaClave();
    public string CadenaConexion { get; } = LeerCadenaConexion();

    public AppFactory()
    {
        // Deja a los usuarios semilla sin clave para que el arranque de la API les asigne las de esta ejecución.
        using var cn = new SqlConnection(CadenaConexion);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE vac.Usuario SET ClaveHash = NULL WHERE NombreUsuario IN ('admin','epi01','jefe01','vac01','ciud01')";
        cmd.ExecuteNonQuery();
    }

    public string ClaveDe(string usuario) => usuario == "admin" ? ClaveAdmin : ClaveGeneral;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:AdminPassword"] = ClaveAdmin,
            ["Seed:Password"] = ClaveGeneral,
            ["ConnectionStrings:VacunApp"] = CadenaConexion,
        }));
    }

    // Cumple la política de claves de la API (largo, mayúscula, minúscula, dígito y símbolo).
    static string NuevaClave() => "Aa1!" + Guid.NewGuid().ToString("N");

    static string LeerCadenaConexion()
    {
        var env = Environment.GetEnvironmentVariable("VACUNAPP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // Misma fuente que usa la API en desarrollo: panel/appsettings.Development.json (ignorado por git).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var archivo = Path.Combine(dir.FullName, "panel", "appsettings.Development.json");
            if (!File.Exists(archivo)) continue;
            var cadena = new ConfigurationBuilder().AddJsonFile(archivo).Build().GetConnectionString("VacunApp");
            if (!string.IsNullOrWhiteSpace(cadena)) return cadena;
        }
        throw new InvalidOperationException(
            "No hay cadena de conexión: defina VACUNAPP_TEST_CONNECTION o cree panel/appsettings.Development.json (vea el .example).");
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<AppFactory>;
