using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VacunApp.Tests;

/// <summary>El texto escrito en el campo de usuario no debe quedar en los registros si no es una cuenta real.</summary>
[Collection("api")]
public class RegistroAccesoFallidoTests(AppFactory app)
{
    sealed class Captura : ILoggerProvider, ILogger
    {
        public List<string> Mensajes { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Mensajes) Mensajes.Add(formatter(state, exception));
        }
        public void Dispose() { }
    }

    HttpClient ClienteConCaptura(out Captura captura)
    {
        var c = new Captura();
        captura = c;
        return app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(c))))
                  .CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
    }

    [Fact]
    public async Task Un_usuario_inexistente_no_deja_el_texto_escrito_en_el_registro()
    {
        const string pegado = "Cl4ve-Secreta-Pegada!";
        var cliente = ClienteConCaptura(out var captura);

        var resp = await cliente.PostAsJsonAsync("/api/login", new { usuario = pegado, clave = "otra" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var todo = string.Join("\n", captura.Mensajes);
        Assert.DoesNotContain(pegado, todo);
        Assert.Contains(captura.Mensajes, m => m.Contains("Acceso fallido"));
    }

    [Fact]
    public async Task Una_cuenta_real_si_se_registra_por_su_nombre()
    {
        var cliente = ClienteConCaptura(out var captura);

        var resp = await cliente.PostAsJsonAsync("/api/login", new { usuario = "vac01", clave = "Incorrecta1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains(captura.Mensajes, m => m.Contains("Acceso fallido") && m.Contains("vac01"));
    }
}
