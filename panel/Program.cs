using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;
using VacunApp.Panel.Data;
using VacunApp.Panel.Pacientes;
using VacunApp.Panel.Agenda;
using VacunApp.Panel.Stock;

// Interfaz mínima de consulta de VacunApp Tacna.
// Toda la lógica vive en la base de datos (vistas, procedimientos y triggers);
// esta API solo los expone como JSON para la página de wwwroot.

var builder = WebApplication.CreateBuilder(args);

// La cadena de conexión se lee al resolver Db (no al construir el builder) para que cualquier
// fuente de configuración —archivo, variable de entorno, pruebas— se aplique por igual.
builder.Services.AddSingleton(sp => new Db(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("VacunApp")
    ?? throw new InvalidOperationException(
        "Falta ConnectionStrings:VacunApp: use appsettings.Development.json (vea el .example) o la variable ConnectionStrings__VacunApp.")));
builder.Services.AddAutenticacion(builder.Environment);

var app = builder.Build();
var db = app.Services.GetRequiredService<Db>();

// Fuera de desarrollo, un error inesperado nunca muestra trazas ni detalles internos.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(salida => salida.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { error = "Ocurrió un error interno. Intente nuevamente." });
    }));
    app.UseHsts();
}

// Cabeceras de seguridad en todas las respuestas. 'unsafe-inline' solo se admite en estilos
// (las barras de cobertura usan style=""); los scripts deben venir de archivos propios.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
        if (ctx.Request.Path.StartsWithSegments("/api"))
            h["Cache-Control"] = "no-store";   // datos de salud y sesión: nunca en caché
        return Task.CompletedTask;
    });
    await next();
});

// Los errores de negocio (THROW 50000+) llegan al usuario con su mensaje en español.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (SqlException ex) when (ex.Number >= 50000)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, codigo = ex.Number });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAutenticacion();
app.MapUsuarios();
app.MapPacientes();
app.MapDosis();
app.MapCarne();
app.MapAuditoria();
app.MapStock();
app.MapHorarios();

// RN-22: las consultas regionales son solo del epidemiólogo y el administrador.
var regional = app.MapGroup("/api").RequireAuthorization(Politicas.Regional);
await SeedUsuarios.EjecutarAsync(
    app.Services.GetRequiredService<UsuarioRepo>(),
    app.Services.GetRequiredService<IPasswordHasher<CuentaUsuario>>(),
    app.Configuration,
    app.Logger);

regional.MapGet("/resumen", async () =>
    Results.Ok((await db.QueryAsync("SELECT * FROM vac.vw_ResumenGeneral"))[0].FirstOrDefault()));

regional.MapGet("/sarampion", async () =>
    Results.Ok((await db.QueryAsync(
        "SELECT * FROM vac.vw_CoberturaSarampion ORDER BY BroteActivo DESC, CoberturaSPR1"))[0]));

regional.MapGet("/cobertura", async (string? vacuna, byte? dosis, string? provincia) =>
    Results.Ok((await db.ExecAsync("vac.usp_ReporteCoberturaDistrito",
        ("@CodigoVacuna", vacuna), ("@NumeroDosis", dosis), ("@Provincia", provincia)))[0]));

regional.MapGet("/alertas", async (string? ubigeo, int? top) =>
    Results.Ok((await db.QueryAsync(
        """
        SELECT TOP (@Top) * FROM vac.vw_AlertasPendientes
        WHERE (@Ubigeo IS NULL OR Ubigeo = @Ubigeo)
        ORDER BY CASE TipoAlerta WHEN 'ZONA_BROTE' THEN 0 ELSE 1 END, DiasAbierta DESC, Paciente
        """,
        ("@Top", top ?? 100), ("@Ubigeo", ubigeo)))[0]));

regional.MapGet("/pendientes", async (string? ubigeo, string? vacuna, bool? soloBrote, int? top) =>
    Results.Ok((await db.ExecAsync("vac.usp_ListarPendientes",
        ("@Ubigeo", ubigeo), ("@CodigoVacuna", vacuna),
        ("@SoloZonaBrote", soloBrote ?? false), ("@Top", top ?? 100)))[0]));

regional.MapGet("/campanas", async () =>
    Results.Ok((await db.QueryAsync("SELECT * FROM vac.vw_AvanceCampana ORDER BY IdCampana, PorcentajeAvance"))[0]));

app.MapGet("/api/catalogos", async (ClaimsPrincipal user) =>
{
    var r = await db.QueryAsync(
        """
        SELECT Ubigeo, Nombre FROM vac.Distrito ORDER BY Nombre;
        SELECT v.Codigo, e.NumeroDosis, e.Descripcion FROM vac.EsquemaDosis e
          JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna ORDER BY v.Codigo, e.NumeroDosis;
        SELECT es.IdEstablecimiento, es.Nombre, d.Nombre AS Distrito FROM vac.EstablecimientoSalud es
          JOIN vac.Distrito d ON d.IdDistrito = es.IdDistrito ORDER BY d.Nombre, es.Nombre;
        SELECT IdVacunador, Dni, CONCAT(Nombres, ' ', Apellidos) AS Nombre, IdEstablecimiento FROM vac.Vacunador WHERE Activo = 1 AND (@Regional = 1 OR IdEstablecimiento = @Est);
        SELECT v.Codigo, l.NumeroLote, l.FechaVencimiento, s.Cantidad AS Existencias FROM vac.LoteVacuna l
          JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
          LEFT JOIN vac.StockLote s ON s.IdLote = l.IdLote AND s.IdEstablecimiento = @Est
          WHERE l.FechaVencimiento >= CAST(GETDATE() AS DATE) AND (@Regional = 1 OR s.Cantidad > 0)
          ORDER BY v.Codigo, l.NumeroLote;
        """,
        ("@Regional", Alcance.EsRegional(user)), ("@Est", Alcance.Establecimiento(user)));
    return Results.Ok(new { distritos = r[0], esquema = r[1], establecimientos = r[2], personal = r[3], lotes = r[4] });
});


app.Run();


sealed class Db(string connectionString)
{
    public Task<List<List<Dictionary<string, object?>>>> QueryAsync(string sql, params (string, object?)[] args)
        => RunAsync(sql, CommandType.Text, args);

    public Task<List<List<Dictionary<string, object?>>>> ExecAsync(string procedure, params (string, object?)[] args)
        => RunAsync(procedure, CommandType.StoredProcedure, args);

    // Devuelve todos los result sets como listas de filas (columna -> valor).
    async Task<List<List<Dictionary<string, object?>>>> RunAsync(string text, CommandType type, (string Name, object? Value)[] args)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand(text, cn) { CommandType = type };
        foreach (var (name, value) in args)
            cmd.Parameters.Add(value as SqlParameter ?? new SqlParameter(name, ToDb(value)));

        var sets = new List<List<Dictionary<string, object?>>>();
        await using var rd = await cmd.ExecuteReaderAsync();
        do
        {
            var rows = new List<Dictionary<string, object?>>();
            while (await rd.ReadAsync())
            {
                var row = new Dictionary<string, object?>(rd.FieldCount);
                for (var i = 0; i < rd.FieldCount; i++)
                    row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                rows.Add(row);
            }
            sets.Add(rows);
        } while (await rd.NextResultAsync());
        return sets;
    }

    static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        string s when string.IsNullOrWhiteSpace(s) => DBNull.Value,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => value
    };
}
