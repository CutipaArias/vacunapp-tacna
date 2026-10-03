using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using VacunApp.Panel.Auth;

namespace VacunApp.Panel.Pacientes;

/// <summary>RF-03 / CU03: registro de pacientes por el vacunador. Las reglas RN-01 y RN-02 las aplica la base.</summary>
static partial class PacientesEndpoints
{
    static readonly string[] TiposDocumento = ["DNI", "CNV", "CE", "PAS"];

    public static void MapPacientes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pacientes", async (NuevoPaciente p, Db db) =>
        {
            if (Validar(p) is { } error) return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

            var id = new SqlParameter("@IdPaciente", SqlDbType.Int) { Direction = ParameterDirection.Output };
            await db.ExecAsync("vac.usp_RegistrarPaciente",
                ("@TipoDocumento", (p.TipoDocumento ?? "DNI").Trim().ToUpperInvariant()),
                ("@NumeroDocumento", p.NumeroDocumento!.Trim()), ("@Nombres", p.Nombres!.Trim()),
                ("@ApellidoPaterno", p.ApellidoPaterno!.Trim()), ("@ApellidoMaterno", p.ApellidoMaterno?.Trim()),
                ("@FechaNacimiento", Fecha(p.FechaNacimiento)), ("@Sexo", p.Sexo!.Trim().ToUpperInvariant()),
                ("@Ubigeo", p.Ubigeo!.Trim()), ("@Direccion", p.Direccion?.Trim()), ("@Telefono", p.Telefono?.Trim()),
                ("@IdPaciente", id));
            return Results.Created($"/api/paciente/{p.NumeroDocumento!.Trim()}", new { idPaciente = (int)id.Value });
        }).RequireAuthorization(Politicas.Vacunador);
    }

    static DateOnly? Fecha(string? texto) =>
        DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    /// <summary>Forma de los datos (longitudes, formatos, valores permitidos); las reglas de negocio las valida la base.</summary>
    static string? Validar(NuevoPaciente p)
    {
        var tipo = (p.TipoDocumento ?? "DNI").Trim().ToUpperInvariant();
        if (!TiposDocumento.Contains(tipo)) return "El tipo de documento debe ser DNI, CNV, CE o PAS.";
        if (string.IsNullOrWhiteSpace(p.NumeroDocumento) || p.NumeroDocumento.Trim().Length > 12
            || !Alfanumerico().IsMatch(p.NumeroDocumento.Trim()))
            return "Ingrese un número de documento válido (letras y números, hasta 12 caracteres).";
        if (string.IsNullOrWhiteSpace(p.Nombres) || p.Nombres.Trim().Length > 60) return "Ingrese los nombres (hasta 60 caracteres).";
        if (string.IsNullOrWhiteSpace(p.ApellidoPaterno) || p.ApellidoPaterno.Trim().Length > 40) return "Ingrese el apellido paterno (hasta 40 caracteres).";
        if (p.ApellidoMaterno is { Length: > 40 }) return "El apellido materno admite hasta 40 caracteres.";
        if (Fecha(p.FechaNacimiento) is not { } nac) return "Ingrese la fecha de nacimiento con el formato AAAA-MM-DD.";
        if (nac < new DateOnly(1900, 1, 1)) return "La fecha de nacimiento no puede ser anterior a 1900.";
        if (p.Sexo?.Trim().ToUpperInvariant() is not ("F" or "M")) return "El sexo debe ser F o M.";
        if (string.IsNullOrWhiteSpace(p.Ubigeo) || !Ubigeo().IsMatch(p.Ubigeo.Trim())) return "El ubigeo debe tener 6 dígitos.";
        if (p.Direccion is { Length: > 120 }) return "La dirección admite hasta 120 caracteres.";
        if (p.Telefono is { } t && (t.Length > 15 || !Telefono().IsMatch(t))) return "El teléfono admite hasta 15 caracteres: dígitos, espacios, +, ( ) y guion.";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9]+$")] private static partial Regex Alfanumerico();
    [GeneratedRegex(@"^[0-9]{6}$")] private static partial Regex Ubigeo();
    [GeneratedRegex(@"^[0-9+()\- ]*$")] private static partial Regex Telefono();
}

record NuevoPaciente(
    string? TipoDocumento, string? NumeroDocumento, string? Nombres, string? ApellidoPaterno, string? ApellidoMaterno,
    string? FechaNacimiento, string? Sexo, string? Ubigeo, string? Direccion, string? Telefono);
