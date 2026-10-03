using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Auth;

/// <summary>RF-02 / CU02: el administrador crea usuarios, les asigna rol y establecimiento y los da de baja.</summary>
static class UsuariosEndpoints
{
    public static void MapUsuarios(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/usuarios").RequireAuthorization(Politicas.Administrador);

        g.MapGet("/", async (UsuarioRepo repo) => Results.Ok(await repo.ListarAsync()));

        g.MapPost("/", async (CrearUsuarioRequest req, UsuarioRepo repo, IPasswordHasher<CuentaUsuario> hasher) =>
        {
            if (!PoliticaClave.EsValida(req.Clave))
                return Error(PoliticaClave.Descripcion);

            // El hash se calcula aquí (PBKDF2 de PasswordHasher); a la base solo llega el hash.
            var hash = hasher.HashPassword(new CuentaUsuario(0, req.Usuario ?? "", req.Nombre ?? "", null, req.Rol ?? "", null, null, true), req.Clave!);
            var id = await repo.CrearAsync(req.Usuario!, req.Nombre!, hash, req.Rol!, req.IdEstablecimiento, req.IdVacunador);
            return Results.Created($"/api/usuarios/{id}", new { idUsuario = id });
        });

        g.MapPut("/{id:int}", async (int id, ActualizarUsuarioRequest req, ClaimsPrincipal yo, UsuarioRepo repo) =>
        {
            // Evita que el administrador se quite el acceso o los permisos a sí mismo por error.
            if (id.ToString() == yo.FindFirstValue(ClaimTypes.NameIdentifier)
                && (!req.Activo || req.Rol != yo.FindFirstValue(ClaimTypes.Role)))
                return Error("No puede desactivar su propia cuenta ni cambiar su propio rol.");

            await repo.ActualizarAsync(id, req.Nombre!, req.Rol!, req.IdEstablecimiento, req.IdVacunador, req.Activo);
            return Results.NoContent();
        });
    }

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

record CrearUsuarioRequest(string? Usuario, string? Nombre, string? Clave, string? Rol, short? IdEstablecimiento, int? IdVacunador);
record ActualizarUsuarioRequest(string? Nombre, string? Rol, short? IdEstablecimiento, int? IdVacunador, bool Activo);
