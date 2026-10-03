namespace VacunApp.Panel.Auth;

/// <summary>Política mínima de contraseñas para cuentas nuevas y claves de semilla.</summary>
static class PoliticaClave
{
    public const int Minimo = 10;
    public const int Maximo = 128;   // tope para no regalar trabajo de PBKDF2 a quien envíe claves enormes

    public static bool EsValida(string? clave) =>
        clave is { Length: >= Minimo and <= Maximo }
        && clave.Any(char.IsUpper) && clave.Any(char.IsLower)
        && clave.Any(char.IsDigit) && clave.Any(c => !char.IsLetterOrDigit(c));

    public const string Descripcion =
        "La contraseña debe tener entre 10 y 128 caracteres, con mayúscula, minúscula, número y símbolo.";
}
