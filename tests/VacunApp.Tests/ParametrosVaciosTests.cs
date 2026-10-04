using System.Net;

namespace VacunApp.Tests;

/// <summary>
/// El formulario envía los filtros que el usuario dejó en blanco como parámetros vacíos (dosis=). Un vacío
/// significa "sin filtro" en todos los parámetros opcionales; uno mal formado es un 400, nunca un 500.
/// </summary>
[Collection("api")]
public class ParametrosVaciosTests(AppFactory app)
{
    [Theory]
    [InlineData("admin", "/api/cobertura?vacuna=&dosis=&provincia=")]
    [InlineData("admin", "/api/cobertura?dosis=&provincia=1")]
    [InlineData("epi01", "/api/pendientes?ubigeo=&vacuna=&soloBrote=&top=")]
    [InlineData("epi01", "/api/alertas?tipo=&ubigeo=&documento=&top=")]
    [InlineData("admin", "/api/auditoria?top=&operacion=&documento=")]
    [InlineData("jefe01", "/api/stock?idEstablecimiento=")]
    [InlineData("vac01", "/api/agenda/franjas?vacuna=SPR&idEstablecimiento=&desde=&hasta=")]
    public async Task Un_parametro_vacio_se_trata_como_sin_filtro(string usuario, string url)
    {
        var c = await app.SesionComoAsync(usuario);

        var resp = await c.GetAsync(url);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{usuario} {url} -> {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("/api/cobertura?dosis=abc")]
    [InlineData("/api/cobertura?dosis=999")]
    public async Task Un_valor_numerico_mal_formado_da_400_no_500(string url)
    {
        var c = await app.SesionComoAsync("admin");

        var resp = await c.GetAsync(url);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"{url} -> {(int)resp.StatusCode}");
    }
}
