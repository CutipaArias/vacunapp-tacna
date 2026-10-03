using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VacunApp.Tests;

// Prueba de humo: la API arranca y consulta la base de Docker (requiere ./desplegar.ps1).
public class ResumenTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly HttpClient _client;

    public ResumenTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Resumen_devuelve_los_totales_de_la_base()
    {
        var resp = await _client.GetAsync("/api/resumen");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.ValueKind == JsonValueKind.Object);
    }
}
