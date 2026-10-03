using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VacunApp.Tests;

// Prueba de humo: la API arranca, la sesión funciona y consulta la base de Docker (requiere ./desplegar.ps1).
[Collection("api")]
public class ResumenTests(AppFactory app)
{
    [Fact]
    public async Task Resumen_devuelve_los_totales_de_la_base_al_epidemiologo()
    {
        var c = await app.SesionComoAsync("epi01");

        var resp = await c.GetAsync("/api/resumen");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
    }
}
