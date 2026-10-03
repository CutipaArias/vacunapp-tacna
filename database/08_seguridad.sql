/* =====================================================================
   VacunApp Tacna - 08. Seguridad: roles, usuarios y vínculos familiares
   Módulo identidad (RF-01, RF-02). Es idempotente: se puede ejecutar N
   veces, también sobre una base que ya tiene datos (no borra nada).

   Las contraseñas NUNCA se guardan aquí: ClaveHash recibe el hash
   PBKDF2 que genera PasswordHasher de ASP.NET Core. Los usuarios semilla
   quedan con ClaveHash = NULL (no pueden iniciar sesión) hasta que la
   aplicación, al arrancar, les asigne la clave configurada en el entorno.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   Rol: catálogo fijo. Los Id se usan en CHECK y en las políticas de la API.
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.Rol', N'U') IS NULL
CREATE TABLE vac.Rol (
    IdRol       TINYINT      NOT NULL,
    Nombre      VARCHAR(30)  NOT NULL,
    Descripcion VARCHAR(120) NOT NULL,
    CONSTRAINT PK_Rol PRIMARY KEY (IdRol),
    CONSTRAINT UQ_Rol_Nombre UNIQUE (Nombre)
);
GO

INSERT vac.Rol (IdRol, Nombre, Descripcion)
SELECT v.IdRol, v.Nombre, v.Descripcion
FROM (VALUES
    (1, 'ADMINISTRADOR',        'Gestiona usuarios y revisa la auditoría (alcance regional)'),
    (2, 'EPIDEMIOLOGO',         'Declara brotes, gestiona campañas y consulta la cobertura (alcance regional)'),
    (3, 'JEFE_ESTABLECIMIENTO', 'Gestiona horarios, stock y supervisa su establecimiento'),
    (4, 'VACUNADOR',            'Registra pacientes y dosis, atiende citas de su establecimiento'),
    (5, 'CIUDADANO',            'Reserva citas y consulta el carné de sus hijos vinculados')
) AS v (IdRol, Nombre, Descripcion)
WHERE NOT EXISTS (SELECT 1 FROM vac.Rol r WHERE r.IdRol = v.IdRol);
GO

/* ---------------------------------------------------------------------
   Usuario: cuenta de acceso. ClaveHash admite NULL (cuenta sin clave
   asignada) pero, si hay valor, debe parecer un hash (>= 60 caracteres):
   así una contraseña en claro nunca entra por error.
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.Usuario', N'U') IS NULL
CREATE TABLE vac.Usuario (
    IdUsuario         INT           NOT NULL IDENTITY(1,1),
    NombreUsuario     VARCHAR(30)   NOT NULL,
    NombreCompleto    VARCHAR(100)  NOT NULL,
    ClaveHash         VARCHAR(255)  NULL,
    IdRol             TINYINT       NOT NULL,
    IdEstablecimiento SMALLINT      NULL,
    IdVacunador       INT           NULL,
    Activo            BIT           NOT NULL CONSTRAINT DF_Usuario_Activo DEFAULT (1),
    FechaCreacion     DATETIME2(0)  NOT NULL CONSTRAINT DF_Usuario_Fecha DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Usuario PRIMARY KEY (IdUsuario),
    CONSTRAINT UQ_Usuario_Nombre UNIQUE (NombreUsuario),
    CONSTRAINT FK_Usuario_Rol FOREIGN KEY (IdRol) REFERENCES vac.Rol (IdRol),
    CONSTRAINT FK_Usuario_Establecimiento FOREIGN KEY (IdEstablecimiento) REFERENCES vac.EstablecimientoSalud (IdEstablecimiento),
    CONSTRAINT FK_Usuario_Vacunador FOREIGN KEY (IdVacunador) REFERENCES vac.Vacunador (IdVacunador),
    CONSTRAINT CK_Usuario_Nombre CHECK (LEN(NombreUsuario) >= 3 AND NombreUsuario NOT LIKE '%[^A-Za-z0-9._]%'),
    CONSTRAINT CK_Usuario_ClaveHash CHECK (ClaveHash IS NULL OR LEN(ClaveHash) >= 60),
    /* RN-22: vacunador (4) y jefe (3) trabajan en un establecimiento; el vacunador, además, es personal registrado */
    CONSTRAINT CK_Usuario_Establecimiento CHECK (IdRol NOT IN (3, 4) OR IdEstablecimiento IS NOT NULL),
    CONSTRAINT CK_Usuario_Vacunador CHECK (IdRol <> 4 OR IdVacunador IS NOT NULL)
);
GO

/* ---------------------------------------------------------------------
   VinculoFamiliar: qué pacientes puede ver y reservar un ciudadano (RN-17)
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.VinculoFamiliar', N'U') IS NULL
CREATE TABLE vac.VinculoFamiliar (
    IdUsuario  INT         NOT NULL,
    IdPaciente INT         NOT NULL,
    Parentesco VARCHAR(10) NOT NULL,
    CONSTRAINT PK_VinculoFamiliar PRIMARY KEY (IdUsuario, IdPaciente),
    CONSTRAINT FK_Vinculo_Usuario FOREIGN KEY (IdUsuario) REFERENCES vac.Usuario (IdUsuario),
    CONSTRAINT FK_Vinculo_Paciente FOREIGN KEY (IdPaciente) REFERENCES vac.Paciente (IdPaciente),
    CONSTRAINT CK_Vinculo_Parentesco CHECK (Parentesco IN ('MADRE','PADRE','TUTOR','OTRO'))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vinculo_Paciente' AND object_id = OBJECT_ID(N'vac.VinculoFamiliar'))
    CREATE INDEX IX_Vinculo_Paciente ON vac.VinculoFamiliar (IdPaciente);
GO

CREATE OR ALTER TRIGGER vac.trg_Vinculo_SoloCiudadano
ON vac.VinculoFamiliar
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN vac.Usuario u ON u.IdUsuario = i.IdUsuario WHERE u.IdRol <> 5)
        THROW 50201, 'Solo un usuario con rol CIUDADANO puede vincularse a pacientes.', 1;
END;
GO

/* ---------------------------------------------------------------------
   Usuarios semilla (SIMULADOS): uno por rol. Sin clave hasta que la
   aplicación la asigne desde la configuración (nunca desde este script).
   --------------------------------------------------------------------- */
DECLARE @Est SMALLINT = (SELECT MIN(IdEstablecimiento) FROM vac.EstablecimientoSalud);
DECLARE @Vac INT      = (SELECT MIN(IdVacunador) FROM vac.Vacunador WHERE IdEstablecimiento = @Est AND Activo = 1);

INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol, IdEstablecimiento, IdVacunador)
SELECT s.NombreUsuario, s.NombreCompleto, s.IdRol, s.IdEstablecimiento, s.IdVacunador
FROM (VALUES
    ('admin',  'Administrador del sistema',        1, CAST(NULL AS SMALLINT), CAST(NULL AS INT)),
    ('epi01',  'Epidemiólogo DIRESA Tacna',        2, NULL, NULL),
    ('jefe01', 'Jefe de establecimiento (prueba)', 3, @Est, NULL),
    ('vac01',  'Vacunador (prueba)',               4, @Est, @Vac),
    ('ciud01', 'Ciudadano (prueba)',               5, NULL, NULL)
) AS s (NombreUsuario, NombreCompleto, IdRol, IdEstablecimiento, IdVacunador)
WHERE NOT EXISTS (SELECT 1 FROM vac.Usuario u WHERE u.NombreUsuario = s.NombreUsuario);

/* El ciudadano de prueba queda vinculado a sus dos primeros "hijos" simulados */
INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
SELECT u.IdUsuario, p.IdPaciente, 'MADRE'
FROM vac.Usuario u
CROSS APPLY (SELECT TOP (2) IdPaciente FROM vac.Paciente ORDER BY IdPaciente) p
WHERE u.NombreUsuario = 'ciud01'
  AND NOT EXISTS (SELECT 1 FROM vac.VinculoFamiliar v WHERE v.IdUsuario = u.IdUsuario AND v.IdPaciente = p.IdPaciente);
GO

/* ---------------------------------------------------------------------
   Gestión de usuarios (RF-02). La API genera el hash con PasswordHasher y
   lo pasa a estos procedimientos; la base valida la coherencia rol /
   establecimiento / vacunador y devuelve mensajes en español.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ValidarAsignacionUsuario
    @Rol               VARCHAR(30),
    @IdEstablecimiento SMALLINT,
    @IdVacunador       INT,
    @IdRol             TINYINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @IdRol = IdRol FROM vac.Rol WHERE Nombre = @Rol;
    IF @IdRol IS NULL
        THROW 50212, 'El rol indicado no existe.', 1;
    IF @IdRol IN (3, 4) AND @IdEstablecimiento IS NULL
        THROW 50213, 'El jefe de establecimiento y el vacunador deben tener un establecimiento.', 1;
    IF @IdRol NOT IN (3, 4) AND @IdEstablecimiento IS NOT NULL
        THROW 50220, 'Los roles regionales y el ciudadano no llevan establecimiento.', 1;
    IF @IdEstablecimiento IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM vac.EstablecimientoSalud WHERE IdEstablecimiento = @IdEstablecimiento)
        THROW 50214, 'El establecimiento indicado no existe.', 1;
    IF @IdRol = 4
       AND NOT EXISTS (SELECT 1 FROM vac.Vacunador
                       WHERE IdVacunador = @IdVacunador AND IdEstablecimiento = @IdEstablecimiento AND Activo = 1)
        THROW 50215, 'El vacunador debe estar activo y pertenecer al establecimiento indicado.', 1;
    IF @IdRol <> 4 AND @IdVacunador IS NOT NULL
        THROW 50219, 'Solo el rol VACUNADOR se asocia a personal vacunador.', 1;
END
GO

CREATE OR ALTER PROCEDURE vac.usp_CrearUsuario
    @NombreUsuario     VARCHAR(30),
    @NombreCompleto    VARCHAR(100),
    @ClaveHash         VARCHAR(255),
    @Rol               VARCHAR(30),
    @IdEstablecimiento SMALLINT = NULL,
    @IdVacunador       INT      = NULL,
    @IdUsuario         INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @NombreUsuario IS NULL OR LEN(@NombreUsuario) < 3 OR @NombreUsuario LIKE '%[^A-Za-z0-9._]%'
        THROW 50210, 'El nombre de usuario debe tener entre 3 y 30 caracteres: letras, números, punto o guion bajo.', 1;
    IF NULLIF(LTRIM(RTRIM(@NombreCompleto)), '') IS NULL
        THROW 50221, 'Ingrese el nombre completo del usuario.', 1;
    IF @ClaveHash IS NULL OR LEN(@ClaveHash) < 60
        THROW 50216, 'La contraseña debe recibirse como hash; no se guarda texto plano.', 1;

    DECLARE @IdRol TINYINT;
    EXEC vac.usp_ValidarAsignacionUsuario @Rol, @IdEstablecimiento, @IdVacunador, @IdRol OUTPUT;

    IF EXISTS (SELECT 1 FROM vac.Usuario WHERE NombreUsuario = @NombreUsuario)
        THROW 50211, 'Ya existe un usuario con ese nombre.', 1;

    INSERT vac.Usuario (NombreUsuario, NombreCompleto, ClaveHash, IdRol, IdEstablecimiento, IdVacunador)
    VALUES (@NombreUsuario, LTRIM(RTRIM(@NombreCompleto)), @ClaveHash, @IdRol, @IdEstablecimiento, @IdVacunador);
    SET @IdUsuario = SCOPE_IDENTITY();
END
GO

CREATE OR ALTER PROCEDURE vac.usp_ActualizarUsuario
    @IdUsuario         INT,
    @NombreCompleto    VARCHAR(100),
    @Rol               VARCHAR(30),
    @IdEstablecimiento SMALLINT = NULL,
    @IdVacunador       INT      = NULL,
    @Activo            BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NULLIF(LTRIM(RTRIM(@NombreCompleto)), '') IS NULL
        THROW 50221, 'Ingrese el nombre completo del usuario.', 1;

    DECLARE @IdRol TINYINT;
    EXEC vac.usp_ValidarAsignacionUsuario @Rol, @IdEstablecimiento, @IdVacunador, @IdRol OUTPUT;

    BEGIN TRANSACTION;
    /* Se bloquean los administradores activos para que dos bajas simultáneas no dejen el sistema sin ninguno */
    DECLARE @EraAdminActivo BIT = 0, @OtrosAdmins INT;
    SELECT @EraAdminActivo = 1 FROM vac.Usuario WITH (UPDLOCK, HOLDLOCK) WHERE IdUsuario = @IdUsuario AND IdRol = 1 AND Activo = 1;
    IF NOT EXISTS (SELECT 1 FROM vac.Usuario WHERE IdUsuario = @IdUsuario)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50222, 'El usuario no existe.', 1;
    END
    SELECT @OtrosAdmins = COUNT(*) FROM vac.Usuario WITH (UPDLOCK, HOLDLOCK)
    WHERE IdRol = 1 AND Activo = 1 AND IdUsuario <> @IdUsuario;
    IF @EraAdminActivo = 1 AND @OtrosAdmins = 0 AND (@IdRol <> 1 OR @Activo = 0)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50218, 'No se puede dejar el sistema sin un administrador activo.', 1;
    END

    UPDATE vac.Usuario
    SET NombreCompleto = LTRIM(RTRIM(@NombreCompleto)), IdRol = @IdRol,
        IdEstablecimiento = @IdEstablecimiento, IdVacunador = @IdVacunador, Activo = @Activo
    WHERE IdUsuario = @IdUsuario;
    COMMIT TRANSACTION;
END
GO

/* ---------------------------------------------------------------------
   Bloqueo temporal por intentos fallidos (adición al spec, 03/10/2026):
   la API cuenta los fallos consecutivos y, al llegar al máximo, fija
   BloqueadoHasta. Se agregan con ALTER para que el script siga siendo
   idempotente también sobre una base que ya tenga la tabla Usuario.
   --------------------------------------------------------------------- */
IF COL_LENGTH(N'vac.Usuario', N'IntentosFallidos') IS NULL
    ALTER TABLE vac.Usuario ADD IntentosFallidos TINYINT NOT NULL CONSTRAINT DF_Usuario_Intentos DEFAULT (0);
GO
IF COL_LENGTH(N'vac.Usuario', N'BloqueadoHasta') IS NULL
    ALTER TABLE vac.Usuario ADD BloqueadoHasta DATETIME2(0) NULL;
GO
