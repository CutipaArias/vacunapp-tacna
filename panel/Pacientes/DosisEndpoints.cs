using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;
using VacunApp.Panel.Data;

namespace VacunApp.Panel.Pacientes;

/// <summary>
/// RF-04 / CU04: el vacunador registra dosis aplicadas. Las reglas clínicas (RN-03 a RN-09) las aplica la
/// base de datos —procedimiento y trigger de validación— y sus mensajes llegan al usuario en español.
/// </summary>
static class DosisEndpoints
{
    public static void MapDosis(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dosis", async (NuevaDosis d, ClaimsPrincipal user, Db db, UsuarioRepo usuarios) =>
        {
            if (Validar(d) is { } error) return Error(error);
            // RN-22: solo en su propio establecimiento.
            if (!Alcance.PuedeEstablecimiento(user, d.IdEstablecimiento)) return Results.Forbid();

            // Quien aplica la dosis es SIEMPRE el vacunador de la sesión: el DNI que envíe el cliente se ignora,
            // para que nadie registre dosis a nombre de otra persona.
            var dni = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var idUsuario)
                ? await usuarios.DniVacunadorAsync(idUsuario) : null;
            if (dni is null) return Results.Forbid();

            var idDosis = new SqlParameter("@IdDosis", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_RegistrarDosis",
                ("@NumeroDocumento", d.Documento!.Trim()), ("@CodigoVacuna", d.Vacuna!.Trim().ToUpperInvariant()),
                ("@NumeroDosis", (byte)d.Dosis), ("@NumeroLote", d.Lote!.Trim()),
                ("@IdEstablecimiento", d.IdEstablecimiento), ("@DniVacunador", dni),
                ("@FechaAplicacion", Fecha(d.Fecha)), ("@IdCampana", null), ("@IdDosis", idDosis));
            return Results.Ok(new { idDosis = (long)idDosis.Value });
        }).RequireAuthorization(Politicas.Vacunador);
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    static string? Validar(NuevaDosis d)
    {
        if (string.IsNullOrWhiteSpace(d.Documento) || d.Documento.Trim().Length > 12) return "Ingrese el documento del paciente.";
        if (string.IsNullOrWhiteSpace(d.Vacuna) || d.Vacuna.Trim().Length > 10) return "Indique la vacuna.";
        if (d.Dosis is < 1 or > 10) return "Indique un número de dosis entre 1 y 10.";
        if (string.IsNullOrWhiteSpace(d.Lote) || d.Lote.Trim().Length > 20) return "Indique el número de lote.";
        if (!string.IsNullOrWhiteSpace(d.Fecha) && Fecha(d.Fecha) is null) return "La fecha debe tener el formato AAAA-MM-DD.";
        return null;
    }

    static IResult Error(string mensaje) => Results.Json(new { error = mensaje }, statusCode: StatusCodes.Status400BadRequest);
}

/// <param name="DniVacunador">Obsoleto: se acepta por compatibilidad pero se ignora.</param>
record NuevaDosis(string? Documento, string? Vacuna, int Dosis, string? Lote, short IdEstablecimiento, string? DniVacunador, string? Fecha);
