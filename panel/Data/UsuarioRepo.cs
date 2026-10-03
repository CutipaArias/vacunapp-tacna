using System.Data;
using Microsoft.Data.SqlClient;

namespace VacunApp.Panel.Data;

/// <summary>Cuenta de acceso tal como la guarda vac.Usuario (ClaveHash es null si aún no tiene clave).</summary>
sealed record CuentaUsuario(
    int IdUsuario, string NombreUsuario, string NombreCompleto, string? ClaveHash,
    string Rol, short? IdEstablecimiento, int? IdVacunador, bool Activo, bool Bloqueada = false);

/// <summary>Fila del listado de administración: no tiene campo de contraseña ni de hash.</summary>
sealed record UsuarioFila(
    int IdUsuario, string NombreUsuario, string NombreCompleto, string Rol, short? IdEstablecimiento,
    string? Establecimiento, int? IdVacunador, bool Activo, DateTime FechaCreacion);

/// <summary>Acceso a datos de usuarios. Todas las consultas usan parámetros tipados.</summary>
sealed class UsuarioRepo(Db db)
{
    const string SelectCuenta =
        """
        SELECT u.IdUsuario, u.NombreUsuario, u.NombreCompleto, u.ClaveHash, r.Nombre AS Rol,
               u.IdEstablecimiento, u.IdVacunador, u.Activo,
               CAST(IIF(u.BloqueadoHasta > SYSUTCDATETIME(), 1, 0) AS BIT) AS Bloqueada
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
            (string)f["Rol"]!, (short?)f["IdEstablecimiento"], (int?)f["IdVacunador"], (bool)f["Activo"]!, (bool)f["Bloqueada"]!);
    }

    /// <summary>
    /// Suma un intento fallido; al llegar a <paramref name="maximo"/> bloquea la cuenta
    /// <paramref name="minutos"/> minutos y reinicia el contador. No hace nada si ya está bloqueada,
    /// así quien ataque no puede alargar el bloqueo indefinidamente. Es una sola sentencia atómica.
    /// </summary>
    public Task RegistrarFalloAsync(int idUsuario, int maximo, int minutos) =>
        db.QueryAsync(
            """
            UPDATE vac.Usuario
            SET IntentosFallidos = CASE WHEN IntentosFallidos + 1 >= @Max THEN 0 ELSE IntentosFallidos + 1 END,
                BloqueadoHasta   = CASE WHEN IntentosFallidos + 1 >= @Max THEN DATEADD(MINUTE, @Minutos, SYSUTCDATETIME()) ELSE BloqueadoHasta END
            WHERE IdUsuario = @Id AND (BloqueadoHasta IS NULL OR BloqueadoHasta <= SYSUTCDATETIME())
            """,
            ("@Id", idUsuario), ("@Max", maximo), ("@Minutos", minutos));

    /// <summary>Un acceso correcto borra los fallos y cualquier bloqueo vencido (solo escribe si hace falta).</summary>
    public Task RegistrarExitoAsync(int idUsuario) =>
        db.QueryAsync(
            "UPDATE vac.Usuario SET IntentosFallidos = 0, BloqueadoHasta = NULL WHERE IdUsuario = @Id AND (IntentosFallidos > 0 OR BloqueadoHasta IS NOT NULL)",
            ("@Id", idUsuario));

    public Task ActualizarHashAsync(int idUsuario, string hash) =>
        db.QueryAsync("UPDATE vac.Usuario SET ClaveHash = @Hash WHERE IdUsuario = @Id",
            ("@Hash", hash), ("@Id", idUsuario));

    /// <summary>Listado para administración. Nunca incluye ClaveHash.</summary>
    public async Task<List<UsuarioFila>> ListarAsync() =>
        (await db.QueryAsync(
            """
            SELECT u.IdUsuario, u.NombreUsuario, u.NombreCompleto, r.Nombre AS Rol, u.IdEstablecimiento,
                   e.Nombre AS Establecimiento, u.IdVacunador, u.Activo, u.FechaCreacion
            FROM vac.Usuario u
            JOIN vac.Rol r ON r.IdRol = u.IdRol
            LEFT JOIN vac.EstablecimientoSalud e ON e.IdEstablecimiento = u.IdEstablecimiento
            ORDER BY u.NombreUsuario
            """))[0]
        .Select(f => new UsuarioFila(
            (int)f["IdUsuario"]!, (string)f["NombreUsuario"]!, (string)f["NombreCompleto"]!, (string)f["Rol"]!,
            (short?)f["IdEstablecimiento"], (string?)f["Establecimiento"], (int?)f["IdVacunador"],
            (bool)f["Activo"]!, (DateTime)f["FechaCreacion"]!))
        .ToList();

    /// <summary>Crea el usuario con vac.usp_CrearUsuario; las reglas de asignación las valida la base.</summary>
    public async Task<int> CrearAsync(
        string nombreUsuario, string nombreCompleto, string claveHash, string rol, short? idEstablecimiento, int? idVacunador)
    {
        var id = new SqlParameter("@IdUsuario", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.ExecAsync("vac.usp_CrearUsuario",
            ("@NombreUsuario", nombreUsuario), ("@NombreCompleto", nombreCompleto), ("@ClaveHash", claveHash),
            ("@Rol", rol), ("@IdEstablecimiento", idEstablecimiento), ("@IdVacunador", idVacunador), ("@IdUsuario", id));
        return (int)id.Value;
    }

    public Task ActualizarAsync(
        int idUsuario, string nombreCompleto, string rol, short? idEstablecimiento, int? idVacunador, bool activo) =>
        db.ExecAsync("vac.usp_ActualizarUsuario",
            ("@IdUsuario", idUsuario), ("@NombreCompleto", nombreCompleto), ("@Rol", rol),
            ("@IdEstablecimiento", idEstablecimiento), ("@IdVacunador", idVacunador), ("@Activo", activo));

    /// <summary>Asigna el hash solo si la cuenta sigue sin clave (nunca pisa una clave existente).</summary>
    public Task AsignarHashSiVacioAsync(int idUsuario, string hash) =>
        db.QueryAsync("UPDATE vac.Usuario SET ClaveHash = @Hash WHERE IdUsuario = @Id AND ClaveHash IS NULL",
            ("@Hash", hash), ("@Id", idUsuario));
}
