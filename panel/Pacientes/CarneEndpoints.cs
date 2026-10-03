using System.Security.Claims;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Pacientes;

/// <summary>
/// RF-15 / CU14: carné de vacunación. El personal (vacunador, jefe, epidemiólogo, administrador) consulta cualquier
/// paciente porque necesita ubicarlo para vacunarlo; el ciudadano solo ve a los hijos vinculados a su usuario (RN-17).
/// </summary>
static class CarneEndpoints
{
    public static void MapCarne(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/paciente/{documento}", async (string documento, ClaimsPrincipal user, Db db) =>
        {
            // Un ciudadano recibe el mismo 403 para un documento ajeno y para uno inexistente: no se puede
            // usar este endpoint para averiguar qué DNI están registrados.
            if (user.IsInRole(Roles.Ciudadano) && !await EsHijoAsync(db, user, documento))
                return Results.Forbid();

            var r = await db.ExecAsync("vac.usp_HistorialPaciente", ("@NumeroDocumento", documento));
            return Results.Ok(new { datos = r[0].FirstOrDefault(), aplicadas = r[1], pendientes = r[2] });
        });

        app.MapGet("/api/mis-pacientes", async (ClaimsPrincipal user, Db db) =>
        {
            var idUsuario = IdUsuario(user);
            var r = await db.QueryAsync(
                """
                SELECT p.NumeroDocumento AS numeroDocumento,
                       CONCAT(p.Nombres, ' ', p.ApellidoPaterno, ' ', p.ApellidoMaterno) AS nombre,
                       v.Parentesco AS parentesco
                FROM vac.VinculoFamiliar v JOIN vac.Paciente p ON p.IdPaciente = v.IdPaciente
                WHERE v.IdUsuario = @Id
                ORDER BY p.FechaNacimiento DESC
                """,
                ("@Id", idUsuario));
            return Results.Ok(r[0]);
        }).RequireAuthorization(Politicas.Ciudadano);
    }

    static int IdUsuario(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    static async Task<bool> EsHijoAsync(Db db, ClaimsPrincipal user, string documento)
    {
        var r = await db.QueryAsync(
            """
            SELECT 1 FROM vac.VinculoFamiliar v JOIN vac.Paciente p ON p.IdPaciente = v.IdPaciente
            WHERE v.IdUsuario = @Id AND p.NumeroDocumento = @Doc
            """,
            ("@Id", IdUsuario(user)), ("@Doc", documento));
        return r[0].Count > 0;
    }
}
