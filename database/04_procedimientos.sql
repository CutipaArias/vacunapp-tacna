/* =====================================================================
   VacunApp Tacna - 04. Procedimientos almacenados
   Errores de negocio: THROW 500xx con mensaje en español.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   1. usp_RegistrarPaciente
   Registra un paciente validando documento, fecha de nacimiento y
   distrito (por ubigeo). Si vive en zona de brote, el trigger
   trg_Paciente_AlertaZonaBrote genera sus alertas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_RegistrarPaciente
    @TipoDocumento   VARCHAR(3),
    @NumeroDocumento VARCHAR(12),
    @Nombres         VARCHAR(60),
    @ApellidoPaterno VARCHAR(40),
    @ApellidoMaterno VARCHAR(40) = NULL,
    @FechaNacimiento DATE,
    @Sexo            CHAR(1),
    @Ubigeo          CHAR(6),
    @Direccion       VARCHAR(120) = NULL,
    @Telefono        VARCHAR(15)  = NULL,
    @IdPaciente      INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @IdDistrito SMALLINT = (SELECT IdDistrito FROM vac.Distrito WHERE Ubigeo = @Ubigeo);

    IF @IdDistrito IS NULL
        THROW 50001, 'El ubigeo no corresponde a un distrito de Tacna.', 1;
    IF @FechaNacimiento > CAST(GETDATE() AS DATE)
        THROW 50002, 'La fecha de nacimiento no puede ser futura.', 1;
    IF @TipoDocumento = 'DNI' AND (LEN(@NumeroDocumento) <> 8 OR @NumeroDocumento LIKE '%[^0-9]%')
        THROW 50003, 'El DNI debe tener 8 dígitos.', 1;
    IF EXISTS (SELECT 1 FROM vac.Paciente WHERE TipoDocumento = @TipoDocumento AND NumeroDocumento = @NumeroDocumento)
        THROW 50004, 'Ya existe un paciente con ese documento.', 1;

    INSERT INTO vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, ApellidoMaterno,
                              FechaNacimiento, Sexo, IdDistrito, Direccion, Telefono)
    VALUES (@TipoDocumento, @NumeroDocumento, LTRIM(RTRIM(@Nombres)), LTRIM(RTRIM(@ApellidoPaterno)),
            LTRIM(RTRIM(@ApellidoMaterno)), @FechaNacimiento, UPPER(@Sexo), @IdDistrito, @Direccion, @Telefono);

    SET @IdPaciente = SCOPE_IDENTITY();
END
GO

/* ---------------------------------------------------------------------
   2. usp_RegistrarDosis
   Registra una dosis aplicada a partir de datos del mundo real
   (documento, código de vacuna, número de dosis, lote, DNI del vacunador).
   Las reglas clínicas (edad, orden, intervalo, lote vigente) las
   aplica el trigger trg_DosisAplicada_Validar; así también se cumplen
   en inserciones masivas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_RegistrarDosis
    @NumeroDocumento   VARCHAR(12),
    @CodigoVacuna      VARCHAR(10),
    @NumeroDosis       TINYINT,
    @NumeroLote        VARCHAR(20),
    @IdEstablecimiento SMALLINT,
    @DniVacunador       CHAR(8),
    @FechaAplicacion   DATE = NULL,
    @IdCampana         SMALLINT = NULL,
    @IdDosis           BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @FechaAplicacion = ISNULL(@FechaAplicacion, CAST(GETDATE() AS DATE));

    DECLARE @IdPaciente INT, @IdEsquema SMALLINT, @IdLote INT, @IdVacunador INT;

    SELECT @IdPaciente = IdPaciente FROM vac.Paciente WHERE NumeroDocumento = @NumeroDocumento;
    IF @IdPaciente IS NULL
        THROW 50010, 'Paciente no registrado.', 1;

    SELECT @IdEsquema = e.IdEsquema
    FROM vac.EsquemaDosis e
    JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
    WHERE v.Codigo = @CodigoVacuna AND e.NumeroDosis = @NumeroDosis;
    IF @IdEsquema IS NULL
        THROW 50011, 'La vacuna o el número de dosis no existe en el esquema.', 1;

    SELECT @IdLote = l.IdLote
    FROM vac.LoteVacuna l
    JOIN vac.Vacuna v ON v.IdVacuna = l.IdVacuna
    WHERE v.Codigo = @CodigoVacuna AND l.NumeroLote = @NumeroLote;
    IF @IdLote IS NULL
        THROW 50012, 'El lote no existe para esa vacuna.', 1;

    SELECT @IdVacunador = IdVacunador
    FROM vac.Vacunador
    WHERE Dni = @DniVacunador AND IdEstablecimiento = @IdEstablecimiento AND Activo = 1;
    IF @IdVacunador IS NULL
        THROW 50013, 'El vacunador no pertenece al establecimiento o está inactivo.', 1;

    IF @IdCampana IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM vac.Campana
        WHERE IdCampana = @IdCampana AND @FechaAplicacion BETWEEN FechaInicio AND FechaFin)
        THROW 50014, 'La fecha de aplicación está fuera del periodo de la campaña.', 1;

    IF EXISTS (SELECT 1 FROM vac.DosisAplicada WHERE IdPaciente = @IdPaciente AND IdEsquema = @IdEsquema)
        THROW 50015, 'Esta dosis ya fue registrada para el paciente.', 1;

    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, IdCampana, FechaAplicacion)
    VALUES (@IdPaciente, @IdEsquema, @IdLote, @IdEstablecimiento, @IdVacunador, @IdCampana, @FechaAplicacion);

    SET @IdDosis = SCOPE_IDENTITY();
END
GO

/* ---------------------------------------------------------------------
   3. usp_DeclararBrote
   Registra un brote activo en un distrito. El trigger
   trg_Brote_GenerarAlertas crea las alertas ZONA_BROTE.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_DeclararBrote
    @Ubigeo           CHAR(6),
    @Enfermedad       VARCHAR(50),
    @FechaInicio      DATE,
    @CasosConfirmados SMALLINT,
    @IdBrote          INT OUTPUT,
    @AlertasGeneradas INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @IdDistrito SMALLINT = (SELECT IdDistrito FROM vac.Distrito WHERE Ubigeo = @Ubigeo);
    DECLARE @IdEnfermedad TINYINT = (SELECT IdEnfermedad FROM vac.Enfermedad WHERE Nombre = @Enfermedad);

    IF @IdDistrito IS NULL   THROW 50020, 'Ubigeo no válido.', 1;
    IF @IdEnfermedad IS NULL THROW 50021, 'Enfermedad no registrada.', 1;
    IF @CasosConfirmados < 0 THROW 50024, 'Los casos confirmados no pueden ser negativos.', 1;
    IF @FechaInicio > CAST(GETDATE() AS DATE) THROW 50025, 'La fecha de inicio del brote no puede ser futura.', 1;
    IF EXISTS (SELECT 1 FROM vac.Brote WHERE IdDistrito = @IdDistrito AND IdEnfermedad = @IdEnfermedad AND FechaFin IS NULL)
        THROW 50022, 'Ya existe un brote activo de esa enfermedad en el distrito.', 1;

    -- RN-19: si dos declaraciones simultáneas pasan la comprobación anterior, el índice único
    -- UX_Brote_Activo rechaza a la segunda; se informa con el mismo error 50022 y no como fallo interno.
    BEGIN TRY
        BEGIN TRANSACTION;
            INSERT INTO vac.Brote (IdEnfermedad, IdDistrito, FechaInicio, CasosConfirmados)
            VALUES (@IdEnfermedad, @IdDistrito, @FechaInicio, @CasosConfirmados);
            SET @IdBrote = SCOPE_IDENTITY();
        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        IF ERROR_NUMBER() IN (2601, 2627) THROW 50022, 'Ya existe un brote activo de esa enfermedad en el distrito.', 1;
        THROW;
    END CATCH;

    SELECT @AlertasGeneradas = COUNT(*) FROM vac.Alerta WHERE IdBrote = @IdBrote AND Estado = 'PENDIENTE';
END
GO

/* ---------------------------------------------------------------------
   4. usp_CerrarBrote
   Da por controlado un brote y resuelve sus alertas pendientes (RN-20):
   - si otro brote activo sigue cubriendo la dosis, la alerta pasa a ese brote;
   - si la dosis tiene más atraso que la tolerancia de usp_GenerarAlertasAtrasadas (1 mes), la alerta
     vuelve a DOSIS_ATRASADA: el brote la había escalado y el paciente sigue pendiente;
   - el resto (alertas nacidas solo por la zona de brote) se descarta.
   No exige marca de origen en el esquema: el atraso se recalcula con la misma regla del generador.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_CerrarBrote
    @IdBrote            INT,
    @FechaFin           DATE = NULL,
    @AlertasRestauradas INT  = NULL OUTPUT,
    @AlertasDescartadas INT  = NULL OUTPUT,
    @AlertasReasignadas INT  = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @FechaFin = ISNULL(@FechaFin, CAST(GETDATE() AS DATE));

    IF NOT EXISTS (SELECT 1 FROM vac.Brote WHERE IdBrote = @IdBrote AND FechaFin IS NULL)
        THROW 50023, 'El brote no existe o ya está cerrado.', 1;
    IF @FechaFin < (SELECT FechaInicio FROM vac.Brote WHERE IdBrote = @IdBrote)
        THROW 50026, 'La fecha de cierre no puede ser anterior al inicio del brote.', 1;

    DECLARE @Hoy DATE = CAST(GETDATE() AS DATE);

    BEGIN TRANSACTION;
        UPDATE vac.Brote SET FechaFin = @FechaFin WHERE IdBrote = @IdBrote;

        -- Otro brote activo que cubra la misma dosis: la alerta se queda en zona de brote, ligada a ese.
        UPDATE a
        SET IdBrote = z.IdBrote
        FROM vac.Alerta a
        JOIN (SELECT IdPaciente, IdEsquema, MIN(IdBrote) AS IdBrote
              FROM vac.fn_PendientesZonaBrote(@Hoy)
              GROUP BY IdPaciente, IdEsquema) z
          ON z.IdPaciente = a.IdPaciente AND z.IdEsquema = a.IdEsquema
        WHERE a.IdBrote = @IdBrote AND a.Estado = 'PENDIENTE'
        OPTION (LOOP JOIN, HASH JOIN);
        SET @AlertasReasignadas = @@ROWCOUNT;

        -- Dosis atrasada (misma regla que el generador): vuelve a DOSIS_ATRASADA.
        UPDATE a
        SET TipoAlerta = 'DOSIS_ATRASADA', IdBrote = NULL
        FROM vac.Alerta a
        JOIN (SELECT IdPaciente, IdEsquema
              FROM vac.fn_DosisPendientes(@Hoy)
              WHERE MesesAtraso > 1) dp
          ON dp.IdPaciente = a.IdPaciente AND dp.IdEsquema = a.IdEsquema
        WHERE a.IdBrote = @IdBrote AND a.Estado = 'PENDIENTE'
        OPTION (LOOP JOIN, HASH JOIN);
        SET @AlertasRestauradas = @@ROWCOUNT;

        UPDATE vac.Alerta
        SET Estado = 'DESCARTADA', FechaAtencion = SYSDATETIME()
        WHERE IdBrote = @IdBrote AND Estado = 'PENDIENTE';
        SET @AlertasDescartadas = @@ROWCOUNT;
    COMMIT;
END
GO

/* ---------------------------------------------------------------------
   5. usp_ReporteCoberturaDistrito
   Reporte de cobertura por distrito, filtrable por vacuna y provincia.
   Ordena de menor a mayor cobertura para priorizar campañas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ReporteCoberturaDistrito
    @CodigoVacuna VARCHAR(10) = NULL,
    @NumeroDosis  TINYINT     = NULL,
    @Provincia    VARCHAR(40) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Provincia, Ubigeo, Distrito, CodigoVacuna, Vacuna, NumeroDosis, Dosis,
           Elegibles, Vacunados, SinVacunar, PorcentajeCobertura,
           CASE WHEN PorcentajeCobertura >= 95 THEN 'ÓPTIMA'
                WHEN PorcentajeCobertura >= 80 THEN 'ACEPTABLE'
                ELSE 'CRÍTICA' END AS Clasificacion
    FROM vac.vw_CoberturaDistrito
    WHERE (@CodigoVacuna IS NULL OR CodigoVacuna = @CodigoVacuna)
      AND (@NumeroDosis  IS NULL OR NumeroDosis  = @NumeroDosis)
      AND (@Provincia    IS NULL OR Provincia    = @Provincia)
    ORDER BY PorcentajeCobertura, Distrito, CodigoVacuna, NumeroDosis;
END
GO

/* ---------------------------------------------------------------------
   6. usp_ListarPendientes
   Pacientes con dosis pendiente (para salir a vacunar casa por casa).
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_ListarPendientes
    @Ubigeo        CHAR(6)     = NULL,
    @CodigoVacuna  VARCHAR(10) = NULL,
    @SoloZonaBrote BIT         = 0,
    @Top           INT         = 200
AS
BEGIN
    SET NOCOUNT ON;

    /* La zona de brote se materializa una sola vez, con estadísticas exactas. La subconsulta correlacionada anterior
       reevaluaba fn_DosisPendientes por cada fila y, con estadísticas desactualizadas de Brote, el optimizador elegía un
       merge join muchos-a-muchos (49 s). El resultado es el mismo: todas las pendientes de los pacientes con al menos una
       pendiente en zona de brote.
       OPTION (LOOP JOIN, HASH JOIN) prohíbe el merge join: sin él, el antiunión de fn_DosisPendientes (NOT EXISTS) se
       resolvía a veces como merge sobre IdEsquema solo, muchos-a-muchos (30 s en la lista general; ver docs/capacidad.md). */
    IF @SoloZonaBrote = 1
    BEGIN
        CREATE TABLE #zona (IdPaciente INT NOT NULL PRIMARY KEY);
        INSERT #zona (IdPaciente)
        SELECT DISTINCT IdPaciente FROM vac.fn_PendientesZonaBrote(CAST(GETDATE() AS DATE));

        SELECT TOP (@Top)
               dp.NumeroDocumento, dp.Paciente, dp.EdadMeses, dp.Telefono,
               dp.Distrito, dp.CodigoVacuna, dp.NumeroDosis, dp.Dosis, dp.MesesAtraso
        FROM vac.vw_DosisPendientes dp
        WHERE (@Ubigeo       IS NULL OR dp.Ubigeo       = @Ubigeo)
          AND (@CodigoVacuna IS NULL OR dp.CodigoVacuna = @CodigoVacuna)
          AND dp.IdPaciente IN (SELECT IdPaciente FROM #zona)
        ORDER BY dp.MesesAtraso DESC, dp.Distrito, dp.Paciente
        OPTION (LOOP JOIN, HASH JOIN);
        RETURN;
    END

    SELECT TOP (@Top)
           dp.NumeroDocumento, dp.Paciente, dp.EdadMeses, dp.Telefono,
           dp.Distrito, dp.CodigoVacuna, dp.NumeroDosis, dp.Dosis, dp.MesesAtraso
    FROM vac.vw_DosisPendientes dp
    WHERE (@Ubigeo       IS NULL OR dp.Ubigeo       = @Ubigeo)
      AND (@CodigoVacuna IS NULL OR dp.CodigoVacuna = @CodigoVacuna)
    ORDER BY dp.MesesAtraso DESC, dp.Distrito, dp.Paciente
    OPTION (LOOP JOIN, HASH JOIN);
END
GO

/* ---------------------------------------------------------------------
   7. usp_HistorialPaciente
   Carné de vacunación: datos, dosis aplicadas y dosis pendientes.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_HistorialPaciente
    @NumeroDocumento VARCHAR(12)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @IdPaciente INT = (SELECT IdPaciente FROM vac.Paciente WHERE NumeroDocumento = @NumeroDocumento);
    IF @IdPaciente IS NULL
        THROW 50030, 'Paciente no registrado.', 1;

    SELECT p.TipoDocumento, p.NumeroDocumento,
           CONCAT(p.ApellidoPaterno, ' ', p.ApellidoMaterno, ', ', p.Nombres) AS Paciente,
           p.FechaNacimiento,
           vac.fn_EdadMeses(p.FechaNacimiento, CAST(GETDATE() AS DATE)) AS EdadMeses,
           p.Sexo, d.Nombre AS Distrito, p.Direccion, p.Telefono
    FROM vac.Paciente p
    JOIN vac.Distrito d ON d.IdDistrito = p.IdDistrito
    WHERE p.IdPaciente = @IdPaciente;

    SELECT CodigoVacuna, Vacuna, NumeroDosis, Dosis, FechaAplicacion, EdadMesesAlAplicar,
           NumeroLote, Establecimiento, Vacunador, Campana
    FROM vac.vw_HistorialVacunacion
    WHERE IdPaciente = @IdPaciente
    ORDER BY FechaAplicacion, CodigoVacuna, NumeroDosis;

    SELECT CodigoVacuna, Vacuna, NumeroDosis, Dosis, MesesAtraso
    FROM vac.vw_DosisPendientes
    WHERE IdPaciente = @IdPaciente
    ORDER BY MesesAtraso DESC;
END
GO

/* ---------------------------------------------------------------------
   8. usp_GenerarAlertasAtrasadas
   Proceso por lotes (programable con SQL Server Agent): crea alertas
   DOSIS_ATRASADA para dosis con más de @MesesTolerancia meses de atraso.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_GenerarAlertasAtrasadas
    @MesesTolerancia TINYINT = 1,
    @AlertasCreadas  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO vac.Alerta (IdPaciente, IdEsquema, TipoAlerta)
    SELECT dp.IdPaciente, dp.IdEsquema, 'DOSIS_ATRASADA'
    FROM vac.fn_DosisPendientes(CAST(GETDATE() AS DATE)) dp
    WHERE dp.MesesAtraso > @MesesTolerancia
      AND NOT EXISTS (SELECT 1 FROM vac.Alerta a
                      WHERE a.IdPaciente = dp.IdPaciente
                        AND a.IdEsquema  = dp.IdEsquema
                        AND a.Estado     = 'PENDIENTE');

    SET @AlertasCreadas = @@ROWCOUNT;
END
GO

/* ---------------------------------------------------------------------
   9. usp_AtenderAlerta
   Marca una alerta como atendida o descartada (p. ej. paciente migró).
   Las alertas se atienden solas al registrar la dosis (trigger).
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_AtenderAlerta
    @IdAlerta BIGINT,
    @Estado   VARCHAR(10) = 'DESCARTADA'
AS
BEGIN
    SET NOCOUNT ON;

    IF @Estado NOT IN ('ATENDIDA','DESCARTADA')
        THROW 50040, 'Estado no válido: use ATENDIDA o DESCARTADA.', 1;

    UPDATE vac.Alerta
    SET Estado = @Estado, FechaAtencion = SYSDATETIME()
    WHERE IdAlerta = @IdAlerta AND Estado = 'PENDIENTE';

    IF @@ROWCOUNT = 0
        THROW 50041, 'La alerta no existe o ya fue cerrada.', 1;
END
GO

/* ---------------------------------------------------------------------
   10. usp_CrearCampana (RF-14, CU13)
   Crea una campaña con sus metas de dosis por distrito. @Metas es un arreglo JSON:
   [{"ubigeo":"230104","metaDosis":500}, ...]. Las dosis se vinculan a la campaña con
   @IdCampana de usp_RegistrarDosis y solo dentro de su periodo (50014).
   La campaña no guarda la vacuna: el avance por vacuna sale de las dosis vinculadas.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_CrearCampana
    @Nombre      VARCHAR(100),
    @FechaInicio DATE,
    @FechaFin    DATE,
    @Descripcion VARCHAR(300) = NULL,
    @Metas       NVARCHAR(MAX),
    @IdCampana   SMALLINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @Nombre = LTRIM(RTRIM(@Nombre));
    IF @Nombre IS NULL OR @Nombre = '' THROW 50060, 'Indique el nombre de la campaña.', 1;
    IF @FechaInicio IS NULL OR @FechaFin IS NULL OR @FechaFin < @FechaInicio
        THROW 50061, 'Indique las fechas de la campaña; la de fin no puede ser anterior a la de inicio.', 1;
    IF EXISTS (SELECT 1 FROM vac.Campana WHERE Nombre = @Nombre AND FechaInicio = @FechaInicio)
        THROW 50062, 'Ya existe una campaña con ese nombre y esa fecha de inicio.', 1;
    IF @Metas IS NULL OR ISJSON(@Metas) = 0 OR LEFT(LTRIM(@Metas), 1) <> '[' OR NOT EXISTS (SELECT 1 FROM OPENJSON(@Metas))
        THROW 50063, 'Indique al menos un distrito con su meta de dosis.', 1;

    -- La meta se lee como texto y se convierte aquí para dar un error propio (no un fallo de conversión).
    DECLARE @m TABLE (Ubigeo VARCHAR(10), Meta VARCHAR(30));
    INSERT @m (Ubigeo, Meta)
    SELECT ubigeo, metaDosis
    FROM OPENJSON(@Metas) WITH (ubigeo NVARCHAR(10) '$.ubigeo', metaDosis NVARCHAR(30) '$.metaDosis');

    IF EXISTS (SELECT 1 FROM @m m WHERE NOT EXISTS (SELECT 1 FROM vac.Distrito d WHERE d.Ubigeo = m.Ubigeo))
        THROW 50064, 'Algún ubigeo de las metas no es válido.', 1;
    IF EXISTS (SELECT 1 FROM @m WHERE TRY_CONVERT(BIGINT, Meta) IS NULL OR TRY_CONVERT(BIGINT, Meta) NOT BETWEEN 1 AND 1000000)
        THROW 50065, 'La meta de cada distrito debe ser un número entero entre 1 y 1 000 000.', 1;
    IF EXISTS (SELECT 1 FROM @m GROUP BY Ubigeo HAVING COUNT(*) > 1)
        THROW 50066, 'Un distrito aparece más de una vez en las metas.', 1;

    BEGIN TRANSACTION;
        INSERT INTO vac.Campana (Nombre, FechaInicio, FechaFin, Descripcion)
        VALUES (@Nombre, @FechaInicio, @FechaFin, NULLIF(LTRIM(RTRIM(@Descripcion)), ''));
        SET @IdCampana = SCOPE_IDENTITY();

        INSERT INTO vac.CampanaDistrito (IdCampana, IdDistrito, MetaDosis)
        SELECT @IdCampana, d.IdDistrito, CONVERT(INT, m.Meta)
        FROM @m m JOIN vac.Distrito d ON d.Ubigeo = m.Ubigeo;
    COMMIT;
END
GO

/* ---------------------------------------------------------------------
   11. usp_CerrarCampana (RF-14)
   Da por terminada hoy una campaña en curso: su fin pasa a ser la fecha de hoy y desde mañana
   ya no admite dosis (50014). No se cierra una campaña que no empezó (50068) ni una que ya
   terminó o termina hoy (50069). Devuelve la nueva fecha de fin.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_CerrarCampana
    @IdCampana SMALLINT,
    @FechaFin  DATE OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Hoy DATE = CAST(GETDATE() AS DATE), @Inicio DATE, @Fin DATE;

    BEGIN TRANSACTION;
        SELECT @Inicio = FechaInicio, @Fin = FechaFin FROM vac.Campana WITH (UPDLOCK, HOLDLOCK) WHERE IdCampana = @IdCampana;
        IF @Inicio IS NULL THROW 50067, 'La campaña no existe.', 1;
        IF @Inicio > @Hoy  THROW 50068, 'La campaña aún no empezó: no hay nada que cerrar.', 1;
        IF @Fin <= @Hoy    THROW 50069, 'La campaña ya terminó o termina hoy.', 1;

        UPDATE vac.Campana SET FechaFin = @Hoy WHERE IdCampana = @IdCampana;
        SET @FechaFin = @Hoy;
    COMMIT;
END
GO
