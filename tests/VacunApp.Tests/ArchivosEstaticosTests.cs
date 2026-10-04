using System.Net;

namespace VacunApp.Tests;

/// <summary>
/// La interfaz está en español con tildes: los archivos de texto se sirven declarando UTF-8 en la cabecera, para que ningún
/// navegador adivine otra codificación al ejecutar un script o aplicar una hoja de estilos.
/// </summary>
[Collection("api")]
public class ArchivosEstaticosTests(AppFactory app)
{
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/login.html")]
    [InlineData("/panel.js")]
    [InlineData("/dashboard.js")]
    [InlineData("/login.js")]
    [InlineData("/estilos.css")]
    public async Task Los_archivos_de_texto_declaran_utf8(string ruta)
    {
        var r = await app.CreateClient().GetAsync(ruta);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("utf-8", r.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
    }

    [Fact]
    public async Task La_raiz_sirve_la_pagina_principal_en_utf8()
    {
        var r = await app.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("utf-8", r.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
    }
}
