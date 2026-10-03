/* =====================================================================
   VacunApp Tacna - 10. Agenda: franjas con cupo y citas
   RF-05 reservar cita, RF-08 gestionar horarios y cupos.
   RN-14 una cita ocupa un cupo y no puede superar el máximo.
   RN-15 la cita es para una dosis elegible no aplicada; una sola cita
         activa por dosis. RN-17 el ciudadano solo reserva para sus vinculados.
   Idempotente: se puede ejecutar varias veces sin perder datos.
   Códigos de error de este módulo: 50120 a 50138.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   HorarioAtencion: franja (establecimiento + vacuna + fecha y hora) con
   un cupo máximo. El índice único impide crear dos veces la misma franja.
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.HorarioAtencion', N'U') IS NULL
CREATE TABLE vac.HorarioAtencion (
    IdHorario         INT          NOT NULL IDENTITY(1,1),
    IdEstablecimiento SMALLINT     NOT NULL,
    IdVacuna          TINYINT      NOT NULL,
    FechaHora         DATETIME2(0) NOT NULL,
    CupoMaximo        SMALLINT     NOT NULL,
    Activo            BIT          NOT NULL CONSTRAINT DF_Horario_Activo DEFAULT (1),
    CONSTRAINT PK_HorarioAtencion PRIMARY KEY (IdHorario),
    CONSTRAINT UQ_Horario_Franja UNIQUE (IdEstablecimiento, IdVacuna, FechaHora),
    CONSTRAINT FK_Horario_Establecimiento FOREIGN KEY (IdEstablecimiento) REFERENCES vac.EstablecimientoSalud (IdEstablecimiento),
    CONSTRAINT FK_Horario_Vacuna FOREIGN KEY (IdVacuna) REFERENCES vac.Vacuna (IdVacuna),
    CONSTRAINT CK_Horario_Cupo CHECK (CupoMaximo BETWEEN 1 AND 500)
);
GO

/* ---------------------------------------------------------------------
   Cita: reserva de un paciente en una franja para una dosis del esquema.
   Ocupa cupo mientras no esté CANCELADA. IdUsuario = quién la reservó.
   El índice único filtrado garantiza una sola cita PROGRAMADA por dosis,
   incluso si dos sesiones reservan a la vez en franjas distintas (RN-15).
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.Cita', N'U') IS NULL
CREATE TABLE vac.Cita (
    IdCita       INT          NOT NULL IDENTITY(1,1),
    IdHorario    INT          NOT NULL,
    IdPaciente   INT          NOT NULL,
    IdEsquema    SMALLINT     NOT NULL,
    Estado       VARCHAR(12)  NOT NULL CONSTRAINT DF_Cita_Estado DEFAULT ('PROGRAMADA'),
    IdUsuario    INT          NULL,
    FechaReserva DATETIME2(0) NOT NULL CONSTRAINT DF_Cita_FechaReserva DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Cita PRIMARY KEY (IdCita),
    CONSTRAINT FK_Cita_Horario FOREIGN KEY (IdHorario) REFERENCES vac.HorarioAtencion (IdHorario),
    CONSTRAINT FK_Cita_Paciente FOREIGN KEY (IdPaciente) REFERENCES vac.Paciente (IdPaciente),
    CONSTRAINT FK_Cita_Esquema FOREIGN KEY (IdEsquema) REFERENCES vac.EsquemaDosis (IdEsquema),
    CONSTRAINT FK_Cita_Usuario FOREIGN KEY (IdUsuario) REFERENCES vac.Usuario (IdUsuario),
    CONSTRAINT CK_Cita_Estado CHECK (Estado IN ('PROGRAMADA', 'CANCELADA', 'ATENDIDA', 'NO_ASISTIO'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Cita_DosisActiva' AND object_id = OBJECT_ID(N'vac.Cita'))
    CREATE UNIQUE INDEX UX_Cita_DosisActiva ON vac.Cita (IdPaciente, IdEsquema) WHERE Estado = 'PROGRAMADA';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Cita_Horario' AND object_id = OBJECT_ID(N'vac.Cita'))
    CREATE INDEX IX_Cita_Horario ON vac.Cita (IdHorario, Estado);
GO

/* ---------------------------------------------------------------------
   fn_CitaElegible: devuelve una fila si, en @Fecha, el paciente puede
   recibir esa dosis: edad dentro de la ventana del esquema, dosis no
   aplicada y, desde la 2.a, dosis anterior aplicada respetando el
   intervalo mínimo. Es la misma regla que valida el registro de dosis.
   --------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION vac.fn_CitaElegible (@IdPaciente INT, @IdEsquema SMALLINT, @Fecha DATE)
RETURNS TABLE
AS
RETURN
    SELECT 1 AS Elegible
    FROM vac.Paciente p
    JOIN vac.EsquemaDosis e ON e.IdEsquema = @IdEsquema
    CROSS APPLY (SELECT vac.fn_EdadMeses(p.FechaNacimiento, @Fecha) AS EdadMeses) ed
    WHERE p.IdPaciente = @IdPaciente
      AND ed.EdadMeses >= e.EdadMinimaMeses
      AND (e.EdadMaximaMeses IS NULL OR ed.EdadMeses <= e.EdadMaximaMeses)
      AND NOT EXISTS (SELECT 1 FROM vac.DosisAplicada d WHERE d.IdPaciente = p.IdPaciente AND d.IdEsquema = e.IdEsquema)
      AND (e.NumeroDosis = 1
           OR EXISTS (SELECT 1
                      FROM vac.DosisAplicada prev
                      JOIN vac.EsquemaDosis ep ON ep.IdEsquema = prev.IdEsquema
                      WHERE prev.IdPaciente = p.IdPaciente
                        AND ep.IdVacuna     = e.IdVacuna
                        AND ep.NumeroDosis  = e.NumeroDosis - 1
                        AND DATEDIFF(DAY, prev.FechaAplicacion, @Fecha) >= e.IntervaloMinDias));
GO

/* ---------------------------------------------------------------------
   trg_Cita_Validar (AFTER INSERT, UPDATE): segunda barrera de RN-14 y
   RN-15, también para quien inserte sin pasar por el procedimiento.
   Solo revisa las citas que pasan a PROGRAMADA o cambian de franja.
   Bloquea la franja con UPDLOCK/HOLDLOCK, igual que el procedimiento,
   para que el conteo de cupo sea serializado.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_Cita_Validar
ON vac.Cita
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Nuevas TABLE (IdCita INT PRIMARY KEY, IdHorario INT NOT NULL, IdPaciente INT NOT NULL, IdEsquema SMALLINT NOT NULL);
    INSERT @Nuevas
    SELECT i.IdCita, i.IdHorario, i.IdPaciente, i.IdEsquema
    FROM inserted i
    LEFT JOIN deleted d ON d.IdCita = i.IdCita
    WHERE i.Estado = 'PROGRAMADA'
      AND (d.IdCita IS NULL OR d.Estado <> 'PROGRAMADA' OR d.IdHorario <> i.IdHorario);
    IF NOT EXISTS (SELECT 1 FROM @Nuevas) RETURN;

    -- Una búsqueda puntual por franja, en orden ascendente (el mismo orden que los procedimientos). Un solo
    -- "WHERE IdHorario IN (...)" con HOLDLOCK se resuelve con un recorrido de rango que bloquea franjas ajenas
    -- y provoca interbloqueos entre reprogramaciones simultáneas.
    DECLARE @bloqueo INT, @h INT = (SELECT MIN(IdHorario) FROM @Nuevas);
    WHILE @h IS NOT NULL
    BEGIN
        SELECT @bloqueo = IdHorario FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK) WHERE IdHorario = @h;
        SET @h = (SELECT MIN(IdHorario) FROM @Nuevas WHERE IdHorario > @h);
    END

    IF EXISTS (SELECT 1
               FROM @Nuevas n
               JOIN vac.HorarioAtencion h ON h.IdHorario = n.IdHorario
               JOIN vac.EsquemaDosis e    ON e.IdEsquema = n.IdEsquema
               WHERE e.IdVacuna <> h.IdVacuna)
        THROW 50122, 'La dosis no corresponde a la vacuna de la franja.', 1;

    IF EXISTS (SELECT 1
               FROM @Nuevas n
               JOIN vac.HorarioAtencion h ON h.IdHorario = n.IdHorario
               WHERE NOT EXISTS (SELECT 1 FROM vac.fn_CitaElegible(n.IdPaciente, n.IdEsquema, CAST(h.FechaHora AS DATE))))
        THROW 50124, 'La dosis no es elegible para el paciente en esa fecha (edad, dosis anterior, intervalo o ya aplicada).', 1;

    IF EXISTS (SELECT 1
               FROM vac.HorarioAtencion h
               WHERE h.IdHorario IN (SELECT IdHorario FROM @Nuevas)
                 AND (SELECT COUNT(*) FROM vac.Cita c WHERE c.IdHorario = h.IdHorario AND c.Estado <> 'CANCELADA') > h.CupoMaximo)
        THROW 50126, 'La franja no tiene cupos disponibles.', 1;
END
GO

/* ---------------------------------------------------------------------
   usp_CrearHorario: el jefe define una franja con su cupo (RF-08).
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_CrearHorario
    @IdEstablecimiento SMALLINT,
    @CodigoVacuna      VARCHAR(10),
    @FechaHora         DATETIME2(0),
    @CupoMaximo        INT,
    @IdHorario         INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @IdHorario = NULL;

    IF @CupoMaximo IS NULL OR @CupoMaximo NOT BETWEEN 1 AND 500
        THROW 50132, 'El cupo debe estar entre 1 y 500.', 1;
    IF @FechaHora IS NULL OR @FechaHora <= SYSDATETIME()
        THROW 50131, 'La franja debe ser posterior al momento actual.', 1;
    IF NOT EXISTS (SELECT 1 FROM vac.EstablecimientoSalud WHERE IdEstablecimiento = @IdEstablecimiento)
        THROW 50134, 'El establecimiento no existe.', 1;

    DECLARE @IdVacuna TINYINT = (SELECT IdVacuna FROM vac.Vacuna WHERE Codigo = @CodigoVacuna);
    IF @IdVacuna IS NULL THROW 50109, 'La vacuna no existe.', 1;

    BEGIN TRY
        INSERT vac.HorarioAtencion (IdEstablecimiento, IdVacuna, FechaHora, CupoMaximo)
        VALUES (@IdEstablecimiento, @IdVacuna, @FechaHora, @CupoMaximo);
        SET @IdHorario = SCOPE_IDENTITY();
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() IN (2601, 2627) THROW 50130, 'Ya existe una franja para ese establecimiento, vacuna y hora.', 1;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   usp_ActualizarHorario: cambia el cupo o activa/desactiva una franja.
   El cupo no puede quedar por debajo de las citas ya reservadas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ActualizarHorario
    @IdHorario  INT,
    @CupoMaximo INT,
    @Activo     BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @CupoMaximo IS NULL OR @CupoMaximo NOT BETWEEN 1 AND 500
        THROW 50132, 'El cupo debe estar entre 1 y 500.', 1;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        -- Bloquea la franja: ninguna reserva cuenta cupos mientras se cambia.
        IF NOT EXISTS (SELECT 1 FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK) WHERE IdHorario = @IdHorario)
            THROW 50120, 'La franja no existe.', 1;

        IF (SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @IdHorario AND Estado <> 'CANCELADA') > @CupoMaximo
            THROW 50128, 'El cupo no puede ser menor que las citas ya reservadas en la franja.', 1;

        UPDATE vac.HorarioAtencion SET CupoMaximo = @CupoMaximo, Activo = @Activo WHERE IdHorario = @IdHorario;

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   usp_ReservarCita (RF-05, CU05)
   Control de concurrencia: la fila de la franja se lee con UPDLOCK y
   HOLDLOCK dentro de la transacción, de modo que dos reservas de la
   misma franja se ejecutan una detrás de otra; la segunda ve el cupo
   ya ocupado. trg_Cita_Validar vuelve a comprobarlo (defensa en
   profundidad) y UX_Cita_DosisActiva cubre la misma dosis en franjas
   distintas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ReservarCita
    @IdPaciente INT,
    @IdHorario  INT,
    @IdEsquema  SMALLINT,
    @IdUsuario  INT = NULL,
    @IdCita     INT = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @IdCita = NULL;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    DECLARE @IdVacunaFranja TINYINT, @FechaHora DATETIME2(0), @Cupo SMALLINT, @Activo BIT, @IdVacunaDosis TINYINT;

    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        -- Orden fijo de bloqueos (paciente+dosis, luego franja): evita interbloqueos cuando la misma
        -- dosis se reserva a la vez en franjas distintas (doble clic, dos dispositivos).
        DECLARE @recurso NVARCHAR(100) = CONCAT(N'cita:', @IdPaciente, N':', @IdEsquema), @rc INT;
        EXEC @rc = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @rc < 0 THROW 50135, 'El sistema está ocupado; intente de nuevo en unos segundos.', 1;

        SELECT @IdVacunaFranja = IdVacuna, @FechaHora = FechaHora, @Cupo = CupoMaximo, @Activo = Activo
        FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK)
        WHERE IdHorario = @IdHorario;
        IF @@ROWCOUNT = 0 THROW 50120, 'La franja no existe.', 1;

        IF @Activo = 0 OR @FechaHora <= SYSDATETIME()
            THROW 50121, 'La franja no está disponible (inactiva o ya pasó).', 1;

        SELECT @IdVacunaDosis = IdVacuna FROM vac.EsquemaDosis WHERE IdEsquema = @IdEsquema;
        IF @IdVacunaDosis IS NULL OR NOT EXISTS (SELECT 1 FROM vac.Paciente WHERE IdPaciente = @IdPaciente)
            THROW 50123, 'El paciente o la dosis no existe.', 1;

        IF @IdVacunaDosis <> @IdVacunaFranja
            THROW 50122, 'La dosis no corresponde a la vacuna de la franja.', 1;

        -- RN-17: el ciudadano solo reserva para pacientes vinculados a su usuario.
        IF @IdUsuario IS NOT NULL
           AND EXISTS (SELECT 1 FROM vac.Usuario WHERE IdUsuario = @IdUsuario AND IdRol = 5)
           AND NOT EXISTS (SELECT 1 FROM vac.VinculoFamiliar WHERE IdUsuario = @IdUsuario AND IdPaciente = @IdPaciente)
            THROW 50127, 'El paciente no está vinculado a su usuario.', 1;

        -- RN-15: una sola cita activa por dosis.
        IF EXISTS (SELECT 1 FROM vac.Cita WHERE IdPaciente = @IdPaciente AND IdEsquema = @IdEsquema AND Estado = 'PROGRAMADA')
            THROW 50125, 'El paciente ya tiene una cita activa para esta dosis.', 1;

        IF NOT EXISTS (SELECT 1 FROM vac.fn_CitaElegible(@IdPaciente, @IdEsquema, CAST(@FechaHora AS DATE)))
            THROW 50124, 'La dosis no es elegible para el paciente en esa fecha (edad, dosis anterior, intervalo o ya aplicada).', 1;

        -- RN-14: la franja no supera su cupo (el conteo es seguro: la fila de la franja está bloqueada).
        IF (SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @IdHorario AND Estado <> 'CANCELADA') >= @Cupo
            THROW 50126, 'La franja no tiene cupos disponibles.', 1;

        INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema, IdUsuario)
        VALUES (@IdHorario, @IdPaciente, @IdEsquema, @IdUsuario);
        SET @IdCita = SCOPE_IDENTITY();

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        SET @IdCita = NULL;
        -- Dos reservas de la misma dosis en franjas distintas: gana una, la otra choca con el índice único.
        IF ERROR_NUMBER() IN (2601, 2627) THROW 50125, 'El paciente ya tiene una cita activa para esta dosis.', 1;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   usp_CancelarCita (RF-06, CU06, RN-16): una cita PROGRAMADA se cancela
   hasta 24 horas antes de la franja; al cancelarse deja de ocupar cupo.
   Orden de bloqueos común a todo el módulo: 1.º paciente+dosis
   (sp_getapplock), 2.º franjas en orden ascendente de IdHorario.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_CancelarCita
    @IdCita    INT,
    @IdUsuario INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    DECLARE @IdPaciente INT, @IdEsquema SMALLINT, @IdHorario INT, @Estado VARCHAR(12), @FechaHora DATETIME2(0);

    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        SELECT @IdPaciente = IdPaciente, @IdEsquema = IdEsquema FROM vac.Cita WHERE IdCita = @IdCita;
        IF @IdPaciente IS NULL THROW 50136, 'La cita no existe.', 1;

        DECLARE @recurso NVARCHAR(100) = CONCAT(N'cita:', @IdPaciente, N':', @IdEsquema), @rc INT;
        EXEC @rc = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @rc < 0 THROW 50135, 'El sistema está ocupado; intente de nuevo en unos segundos.', 1;

        -- Con el bloqueo del paciente+dosis, la franja y el estado de esta cita ya no pueden cambiar bajo nuestros pies.
        SELECT @IdHorario = c.IdHorario, @Estado = c.Estado, @FechaHora = h.FechaHora
        FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
        WHERE c.IdCita = @IdCita;

        IF @IdUsuario IS NOT NULL
           AND EXISTS (SELECT 1 FROM vac.Usuario WHERE IdUsuario = @IdUsuario AND IdRol = 5)
           AND NOT EXISTS (SELECT 1 FROM vac.VinculoFamiliar WHERE IdUsuario = @IdUsuario AND IdPaciente = @IdPaciente)
            THROW 50127, 'El paciente no está vinculado a su usuario.', 1;

        IF @Estado <> 'PROGRAMADA' THROW 50137, 'Solo se puede cancelar o reprogramar una cita programada.', 1;
        IF @FechaHora < DATEADD(HOUR, 24, SYSDATETIME())
            THROW 50138, 'Solo se puede cancelar o reprogramar hasta 24 horas antes de la cita.', 1;

        UPDATE vac.Cita SET Estado = 'CANCELADA' WHERE IdCita = @IdCita;

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   usp_ReprogramarCita (RF-06, CU06, RN-16): mueve una cita PROGRAMADA a
   otra franja de la misma vacuna, de forma atómica: si la nueva franja
   está llena o no es elegible, la cita original queda intacta. La regla
   de las 24 horas se aplica a la cita original.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ReprogramarCita
    @IdCita         INT,
    @IdHorarioNuevo INT,
    @IdUsuario      INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    DECLARE @IdPaciente INT, @IdEsquema SMALLINT, @IdHorario INT, @Estado VARCHAR(12), @FechaActual DATETIME2(0);
    DECLARE @IdVacunaDosis TINYINT, @IdVacunaNueva TINYINT, @FechaNueva DATETIME2(0), @CupoNuevo SMALLINT, @ActivoNuevo BIT;
    DECLARE @menor INT, @mayor INT, @bloqueo INT;

    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        SELECT @IdPaciente = IdPaciente, @IdEsquema = IdEsquema FROM vac.Cita WHERE IdCita = @IdCita;
        IF @IdPaciente IS NULL THROW 50136, 'La cita no existe.', 1;

        -- 1.º paciente+dosis, 2.º franjas en orden ascendente de IdHorario: el mismo orden en todo el módulo.
        DECLARE @recurso NVARCHAR(100) = CONCAT(N'cita:', @IdPaciente, N':', @IdEsquema), @rc INT;
        EXEC @rc = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @rc < 0 THROW 50135, 'El sistema está ocupado; intente de nuevo en unos segundos.', 1;

        SELECT @IdHorario = c.IdHorario, @Estado = c.Estado, @FechaActual = h.FechaHora
        FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
        WHERE c.IdCita = @IdCita;

        IF @IdUsuario IS NOT NULL
           AND EXISTS (SELECT 1 FROM vac.Usuario WHERE IdUsuario = @IdUsuario AND IdRol = 5)
           AND NOT EXISTS (SELECT 1 FROM vac.VinculoFamiliar WHERE IdUsuario = @IdUsuario AND IdPaciente = @IdPaciente)
            THROW 50127, 'El paciente no está vinculado a su usuario.', 1;

        IF @Estado <> 'PROGRAMADA' THROW 50137, 'Solo se puede cancelar o reprogramar una cita programada.', 1;
        IF @IdHorarioNuevo = @IdHorario THROW 50129, 'La nueva franja es la misma de la cita.', 1;
        IF @FechaActual < DATEADD(HOUR, 24, SYSDATETIME())
            THROW 50138, 'Solo se puede cancelar o reprogramar hasta 24 horas antes de la cita.', 1;

        SET @menor = IIF(@IdHorario < @IdHorarioNuevo, @IdHorario, @IdHorarioNuevo);
        SET @mayor = IIF(@IdHorario < @IdHorarioNuevo, @IdHorarioNuevo, @IdHorario);
        SELECT @bloqueo = IdHorario FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK) WHERE IdHorario = @menor;
        SELECT @bloqueo = IdHorario FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK) WHERE IdHorario = @mayor;

        SELECT @IdVacunaNueva = IdVacuna, @FechaNueva = FechaHora, @CupoNuevo = CupoMaximo, @ActivoNuevo = Activo
        FROM vac.HorarioAtencion WHERE IdHorario = @IdHorarioNuevo;
        IF @IdVacunaNueva IS NULL THROW 50120, 'La franja no existe.', 1;
        IF @ActivoNuevo = 0 OR @FechaNueva <= SYSDATETIME()
            THROW 50121, 'La franja no está disponible (inactiva o ya pasó).', 1;

        SELECT @IdVacunaDosis = IdVacuna FROM vac.EsquemaDosis WHERE IdEsquema = @IdEsquema;
        IF @IdVacunaDosis <> @IdVacunaNueva THROW 50122, 'La dosis no corresponde a la vacuna de la franja.', 1;

        IF NOT EXISTS (SELECT 1 FROM vac.fn_CitaElegible(@IdPaciente, @IdEsquema, CAST(@FechaNueva AS DATE)))
            THROW 50124, 'La dosis no es elegible para el paciente en esa fecha (edad, dosis anterior, intervalo o ya aplicada).', 1;

        IF (SELECT COUNT(*) FROM vac.Cita WHERE IdHorario = @IdHorarioNuevo AND Estado <> 'CANCELADA') >= @CupoNuevo
            THROW 50126, 'La franja no tiene cupos disponibles.', 1;

        UPDATE vac.Cita SET IdHorario = @IdHorarioNuevo WHERE IdCita = @IdCita;

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   Alerta INASISTENCIA (RN-18): seguimiento del paciente que faltó a su
   cita. Es una alerta de paciente y dosis, igual que DOSIS_ATRASADA.
   --------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Alerta_Tipo' AND definition LIKE N'%INASISTENCIA%')
BEGIN
    ALTER TABLE vac.Alerta DROP CONSTRAINT CK_Alerta_Origen;
    ALTER TABLE vac.Alerta DROP CONSTRAINT CK_Alerta_Tipo;
    ALTER TABLE vac.Alerta ADD CONSTRAINT CK_Alerta_Tipo
        CHECK (TipoAlerta IN ('ZONA_BROTE', 'DOSIS_ATRASADA', 'STOCK_BAJO', 'LOTE_POR_VENCER', 'INASISTENCIA'));
    ALTER TABLE vac.Alerta ADD CONSTRAINT CK_Alerta_Origen CHECK (
        (TipoAlerta IN ('ZONA_BROTE', 'DOSIS_ATRASADA', 'INASISTENCIA') AND IdPaciente IS NOT NULL AND IdEsquema IS NOT NULL AND IdStock IS NULL)
     OR (TipoAlerta IN ('STOCK_BAJO', 'LOTE_POR_VENCER') AND IdStock IS NOT NULL AND IdPaciente IS NULL AND IdEsquema IS NULL));
END
GO

/* ---------------------------------------------------------------------
   usp_AtenderCita (RF-07, CU07): el vacunador atiende una cita PROGRAMADA
   el día de la franja. Registra la dosis con vac.usp_RegistrarDosis (las
   mismas validaciones y el mismo descuento de stock que el registro
   directo; no se duplica lógica) y marca la cita ATENDIDA, todo en una
   transacción: si la dosis falla (p. ej. sin stock) la cita no cambia.
   Mismo orden de bloqueos que el resto del módulo: paciente+dosis primero.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_AtenderCita
    @IdCita        INT,
    @NumeroLote    VARCHAR(20),
    @DniVacunador  CHAR(8),
    @IdDosis       BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    DECLARE @IdPaciente INT, @IdEsquema SMALLINT, @Estado VARCHAR(12), @FechaHora DATETIME2(0), @IdEst SMALLINT;
    DECLARE @Documento VARCHAR(12), @Vacuna VARCHAR(10), @NumeroDosis TINYINT;

    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        SELECT @IdPaciente = IdPaciente, @IdEsquema = IdEsquema FROM vac.Cita WHERE IdCita = @IdCita;
        IF @IdPaciente IS NULL THROW 50136, 'La cita no existe.', 1;

        DECLARE @recurso NVARCHAR(100) = CONCAT(N'cita:', @IdPaciente, N':', @IdEsquema), @rc INT;
        EXEC @rc = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @rc < 0 THROW 50135, 'El sistema está ocupado; intente de nuevo en unos segundos.', 1;

        SELECT @Estado = c.Estado, @FechaHora = h.FechaHora, @IdEst = h.IdEstablecimiento
        FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
        WHERE c.IdCita = @IdCita;

        IF @Estado <> 'PROGRAMADA' THROW 50139, 'Solo se puede atender o marcar inasistencia en una cita programada.', 1;
        IF CAST(@FechaHora AS DATE) <> CAST(SYSDATETIME() AS DATE)
            THROW 50140, 'Solo se puede atender la cita el día de la franja.', 1;

        SELECT @Documento = p.NumeroDocumento FROM vac.Paciente p WHERE p.IdPaciente = @IdPaciente;
        SELECT @Vacuna = v.Codigo, @NumeroDosis = e.NumeroDosis
        FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE e.IdEsquema = @IdEsquema;

        -- Mismas validaciones y descuento de stock que el registro directo de una dosis.
        EXEC vac.usp_RegistrarDosis @Documento, @Vacuna, @NumeroDosis, @NumeroLote, @IdEst, @DniVacunador, NULL, NULL, @IdDosis OUTPUT;

        UPDATE vac.Cita SET Estado = 'ATENDIDA' WHERE IdCita = @IdCita;

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END
GO

/* ---------------------------------------------------------------------
   usp_RegistrarInasistencia (RF-07, RN-18): una cita PROGRAMADA cuya hora
   ya pasó se marca NO_ASISTIO y se deja una alerta INASISTENCIA para el
   seguimiento (si no hay ya una alerta pendiente de esa dosis). No toca
   el stock. La dosis queda libre para reservar de nuevo.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_RegistrarInasistencia
    @IdCita          INT,
    @AlertaCreada    BIT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @propia BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    DECLARE @IdPaciente INT, @IdEsquema SMALLINT, @Estado VARCHAR(12), @FechaHora DATETIME2(0);
    SET @AlertaCreada = 0;

    BEGIN TRY
        IF @propia = 1 BEGIN TRANSACTION;

        SELECT @IdPaciente = IdPaciente, @IdEsquema = IdEsquema FROM vac.Cita WHERE IdCita = @IdCita;
        IF @IdPaciente IS NULL THROW 50136, 'La cita no existe.', 1;

        DECLARE @recurso NVARCHAR(100) = CONCAT(N'cita:', @IdPaciente, N':', @IdEsquema), @rc INT;
        EXEC @rc = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @rc < 0 THROW 50135, 'El sistema está ocupado; intente de nuevo en unos segundos.', 1;

        SELECT @Estado = c.Estado, @FechaHora = h.FechaHora
        FROM vac.Cita c JOIN vac.HorarioAtencion h ON h.IdHorario = c.IdHorario
        WHERE c.IdCita = @IdCita;

        IF @Estado <> 'PROGRAMADA' THROW 50139, 'Solo se puede atender o marcar inasistencia en una cita programada.', 1;
        IF @FechaHora > SYSDATETIME()
            THROW 50141, 'Solo se puede marcar la inasistencia cuando la hora de la cita ya pasó.', 1;

        UPDATE vac.Cita SET Estado = 'NO_ASISTIO' WHERE IdCita = @IdCita;

        IF NOT EXISTS (SELECT 1 FROM vac.Alerta WHERE IdPaciente = @IdPaciente AND IdEsquema = @IdEsquema AND Estado = 'PENDIENTE')
        BEGIN
            INSERT vac.Alerta (IdPaciente, IdEsquema, TipoAlerta) VALUES (@IdPaciente, @IdEsquema, 'INASISTENCIA');
            SET @AlertaCreada = 1;
        END

        IF @propia = 1 COMMIT;
    END TRY
    BEGIN CATCH
        IF @propia = 1 AND XACT_STATE() <> 0 ROLLBACK;
        THROW;
    END CATCH;
END
GO
