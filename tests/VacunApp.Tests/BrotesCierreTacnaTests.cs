using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VacunApp.Tests;

/// <summary>
/// Cierre del brote de sarampión de Tacna de la semilla (cientos de alertas escaladas desde DOSIS_ATRASADA): las
/// escaladas vuelven a DOSIS_ATRASADA y el resto se descarta. Par propio de esta clase: sarampión en Tacna (230104),
/// el brote de la semilla, que se reabre y se restaura al terminar para no alterar los datos de las demás pruebas.
/// </summary>
[Collection("api")]
public class BrotesCierreTacnaTests(AppFactory app) : IAsyncLifetime
{
    const string Ubigeo = "230104";
    int idBrote;
    readonly List<(long Id, string Tipo, int? IdBrote, string Estado, DateTime? FechaAtencion)> copia = [];

    public async Task InitializeAsync()
    {
        idBrote = (int)(await Escalar("""
            SELECT b.IdBrote FROM vac.Brote b JOIN vac.Distrito d ON d.IdDistrito = b.IdDistrito
            JOIN vac.Enfermedad e ON e.IdEnfermedad = b.IdEnfermedad
            WHERE d.Ubigeo = @u AND e.Nombre = N'Sarampión' AND b.FechaFin IS NULL
            """))!;
        await using var cn = await Abrir();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT IdAlerta, TipoAlerta, IdBrote, Estado, FechaAtencion FROM vac.Alerta WHERE IdBrote = @b";
        cmd.Parameters.AddWithValue("@b", idBrote);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            copia.Add((rd.GetInt64(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetDateTime(4)));
    }

    public async Task DisposeAsync()
    {
        await using var cn = await Abrir();
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();
        foreach (var a in copia)
        {
            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE vac.Alerta SET TipoAlerta = @t, IdBrote = @b, Estado = @e, FechaAtencion = @f WHERE IdAlerta = @i";
            cmd.Parameters.AddWithValue("@t", a.Tipo);
            cmd.Parameters.AddWithValue("@b", (object?)a.IdBrote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@e", a.Estado);
            cmd.Parameters.AddWithValue("@f", (object?)a.FechaAtencion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@i", a.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var reabrir = cn.CreateCommand();
        reabrir.Transaction = tx;
        reabrir.CommandText = "UPDATE vac.Brote SET FechaFin = NULL WHERE IdBrote = @b";
        reabrir.Parameters.AddWithValue("@b", idBrote);
        await reabrir.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    async Task<SqlConnection> Abrir()
    {
        var cn = new SqlConnection(app.CadenaConexion);
        await cn.OpenAsync();
        return cn;
    }

    async Task<object?> Escalar(string sql, params (string, object)[] args)
    {
        await using var cn = await Abrir();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", Ubigeo);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull ? null : r;
    }

    [Fact]
    public async Task Cerrar_el_brote_de_Tacna_restaura_las_escaladas_y_descarta_el_resto()
    {
        var pendientesAntes = copia.Count(a => a.Estado == "PENDIENTE");
        Assert.True(pendientesAntes > 0, "la semilla debe traer alertas de zona de brote pendientes en Tacna");
        var c = await app.SesionComoAsync("admin");

        var resp = await c.PostAsJsonAsync($"/api/brotes/{idBrote}/cerrar", new { });

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var restauradas = r.GetProperty("alertasRestauradas").GetInt32();
        var descartadas = r.GetProperty("alertasDescartadas").GetInt32();
        Assert.True(restauradas + descartadas == pendientesAntes, $"pendientes antes={pendientesAntes}; respuesta={r}");
        Assert.True(restauradas > 0, $"las alertas escaladas deben volver a DOSIS_ATRASADA; respuesta={r}");

        var ids = string.Join(',', copia.Where(a => a.Estado == "PENDIENTE").Select(a => a.Id));
        var vueltas = (int)(await Escalar($"SELECT COUNT(*) FROM vac.Alerta WHERE IdAlerta IN ({ids}) AND TipoAlerta = 'DOSIS_ATRASADA' AND Estado = 'PENDIENTE' AND IdBrote IS NULL"))!;
        var descartadasBd = (int)(await Escalar($"SELECT COUNT(*) FROM vac.Alerta WHERE IdAlerta IN ({ids}) AND Estado = 'DESCARTADA'"))!;
        var colgadas = (int)(await Escalar("SELECT COUNT(*) FROM vac.Alerta WHERE IdBrote = @b AND Estado = 'PENDIENTE'", ("@b", idBrote)))!;
        Assert.Equal(restauradas, vueltas);
        Assert.Equal(descartadas, descartadasBd);
        Assert.Equal(0, colgadas);
    }

    [Fact]
    public async Task Los_pacientes_de_las_alertas_restauradas_siguen_en_la_lista_de_pendientes()
    {
        var c = await app.SesionComoAsync("admin");
        var cierre = await c.PostAsJsonAsync($"/api/brotes/{idBrote}/cerrar", new { });
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, await cierre.Content.ReadAsStringAsync());

        var ids = string.Join(',', copia.Where(a => a.Estado == "PENDIENTE").Select(a => a.Id));
        var docs = new List<string>();
        await using (var cn = await Abrir())
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = $"""
                SELECT TOP (20) p.NumeroDocumento FROM vac.Alerta a JOIN vac.Paciente p ON p.IdPaciente = a.IdPaciente
                WHERE a.IdAlerta IN ({ids}) AND a.TipoAlerta = 'DOSIS_ATRASADA' AND a.Estado = 'PENDIENTE' AND a.IdBrote IS NULL
                ORDER BY a.IdAlerta
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) docs.Add(rd.GetString(0));
        }
        Assert.NotEmpty(docs);

        // La lista de pendientes sale de las dosis faltantes, no de las alertas: cerrar el brote no las toca.
        var epi = await app.SesionComoAsync("epi01");
        foreach (var d in docs.Take(5))
        {
            var p = await epi.GetFromJsonAsync<JsonElement>($"/api/paciente/{d}");
            Assert.True(p.GetProperty("pendientes").GetArrayLength() > 0, $"{d} dejó de tener dosis pendientes");
        }
    }
}
