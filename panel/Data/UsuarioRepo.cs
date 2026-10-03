namespace VacunApp.Panel.Data;

/// <summary>Cuenta de acceso tal como la guarda vac.Usuario (ClaveHash es null si aún no tiene clave).</summary>
sealed record CuentaUsuario(
    int IdUsuario, string NombreUsuario, string NombreCompleto, string? ClaveHash,
    string Rol, short? IdEstablecimiento, int? IdVacunador, bool Activo);

/// <summary>Acceso a datos de usuarios. Todas las consultas usan parámetros tipados.</summary>
sealed class UsuarioRepo(Db db)
{
    const string SelectCuenta =
        """
        SELECT u.IdUsuario, u.NombreUsuario, u.NombreCompleto, u.ClaveHash, r.Nombre AS Rol,
               u.IdEstablecimiento, u.IdVacunador, u.Activo
        FROM vac.Usuario u JOIN vac.Rol r ON r.IdRol = u.IdRol
        """;

    public Task<CuentaUsuario?> BuscarAsync(string nombreUsuario) =>
        BuscarUnoAsync(SelectCuenta + " WHERE u.NombreUsuario = @Valor", ("@Valor", nombreUsuario));

    public Task<CuentaUsuario?> BuscarPorIdAsync(int idUsuario) =>
        BuscarUnoAsync(SelectCuenta + " WHERE u.IdUsuario = @Valor", ("@Valor", idUsuario));

    async Task<CuentaUsuario?> BuscarUnoAsync(string sql, (string, object?) parametro)
    {
        var r = await db.QueryAsync(sql, parametro);
        var f = r[0].FirstOrDefault();
        return f is null ? null : new CuentaUsuario(
            (int)f["IdUsuario"]!, (string)f["NombreUsuario"]!, (string)f["NombreCompleto"]!, (string?)f["ClaveHash"],
            (string)f["Rol"]!, (short?)f["IdEstablecimiento"], (int?)f["IdVacunador"], (bool)f["Activo"]!);
    }

    public Task ActualizarHashAsync(int idUsuario, string hash) =>
        db.QueryAsync("UPDATE vac.Usuario SET ClaveHash = @Hash WHERE IdUsuario = @Id",
            ("@Hash", hash), ("@Id", idUsuario));

    /// <summary>Asigna el hash solo si la cuenta sigue sin clave (nunca pisa una clave existente).</summary>
    public Task AsignarHashSiVacioAsync(int idUsuario, string hash) =>
        db.QueryAsync("UPDATE vac.Usuario SET ClaveHash = @Hash WHERE IdUsuario = @Id AND ClaveHash IS NULL",
            ("@Hash", hash), ("@Id", idUsuario));
}
