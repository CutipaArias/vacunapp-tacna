/* =====================================================================
   VacunApp Tacna - 05. Triggers
   Todos son set-based (procesan N filas de inserted/deleted), por lo que
   funcionan igual con un registro manual o con una carga masiva.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   T1. trg_DosisAplicada_Validar  (AFTER INSERT, UPDATE)
   Reglas clínicas que una CHECK no puede expresar porque cruzan tablas:
     - el lote pertenece a la vacuna de la dosis y no está vencido;
     - la fecha no es futura ni anterior al nacimiento;
     - la edad del paciente está dentro de la ventana del esquema;
     - la dosis anterior existe y se respetó el intervalo mínimo;
     - el vacunador pertenece al establecimiento.
   Si alguna falla, THROW revierte toda la transacción.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_DosisAplicada_Validar
ON vac.DosisAplicada
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.EsquemaDosis e ON e.IdEsquema = i.IdEsquema
               JOIN vac.LoteVacuna l   ON l.IdLote    = i.IdLote
               WHERE l.IdVacuna <> e.IdVacuna)
        THROW 50100, 'El lote no corresponde a la vacuna de la dosis.', 1;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.LoteVacuna l ON l.IdLote = i.IdLote
               WHERE l.FechaVencimiento < i.FechaAplicacion)
        THROW 50101, 'El lote estaba vencido en la fecha de aplicación.', 1;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.Paciente p ON p.IdPaciente = i.IdPaciente
               WHERE i.FechaAplicacion > CAST(GETDATE() AS DATE)
                  OR i.FechaAplicacion < p.FechaNacimiento)
        THROW 50102, 'La fecha de aplicación es futura o anterior al nacimiento.', 1;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.Paciente p     ON p.IdPaciente = i.IdPaciente
               JOIN vac.EsquemaDosis e ON e.IdEsquema  = i.IdEsquema
               CROSS APPLY (SELECT vac.fn_EdadMeses(p.FechaNacimiento, i.FechaAplicacion) AS EdadMeses) ed
               WHERE ed.EdadMeses < e.EdadMinimaMeses
                  OR (e.EdadMaximaMeses IS NOT NULL AND ed.EdadMeses > e.EdadMaximaMeses))
        THROW 50103, 'La edad del paciente está fuera del rango permitido para esta dosis.', 1;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.EsquemaDosis e ON e.IdEsquema = i.IdEsquema
               WHERE e.NumeroDosis > 1
                 AND NOT EXISTS (SELECT 1
                                 FROM vac.DosisAplicada prev
                                 JOIN vac.EsquemaDosis ep ON ep.IdEsquema = prev.IdEsquema
                                 WHERE prev.IdPaciente = i.IdPaciente
                                   AND ep.IdVacuna     = e.IdVacuna
                                   AND ep.NumeroDosis  = e.NumeroDosis - 1
                                   AND DATEDIFF(DAY, prev.FechaAplicacion, i.FechaAplicacion) >= e.IntervaloMinDias))
        THROW 50104, 'Falta la dosis anterior o no se respetó el intervalo mínimo entre dosis.', 1;

    IF EXISTS (SELECT 1
               FROM inserted i
               JOIN vac.Vacunador ps ON ps.IdVacunador = i.IdVacunador
               WHERE ps.IdEstablecimiento <> i.IdEstablecimiento)
        THROW 50105, 'El vacunador no pertenece al establecimiento indicado.', 1;
END
GO
EXEC sp_settriggerorder @triggername = N'vac.trg_DosisAplicada_Validar', @order = N'First', @stmttype = N'INSERT';
EXEC sp_settriggerorder @triggername = N'vac.trg_DosisAplicada_Validar', @order = N'First', @stmttype = N'UPDATE';
GO

/* ---------------------------------------------------------------------
   T2. trg_DosisAplicada_AtenderAlertas  (AFTER INSERT)
   Al registrar una dosis, cierra automáticamente sus alertas pendientes.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_DosisAplicada_AtenderAlertas
ON vac.DosisAplicada
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE a
    SET Estado = 'ATENDIDA', FechaAtencion = SYSDATETIME()
    FROM vac.Alerta a
    JOIN inserted i
      ON i.IdPaciente = a.IdPaciente
     AND i.IdEsquema  = a.IdEsquema
    WHERE a.Estado = 'PENDIENTE';
END
GO

/* ---------------------------------------------------------------------
   T3. trg_Brote_GenerarAlertas  (AFTER INSERT)
   Al declarar un brote, genera una alerta ZONA_BROTE por cada paciente
   del distrito con una dosis pendiente de una vacuna que previene la
   enfermedad. Si ya tenía una alerta DOSIS_ATRASADA, la escala.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_Brote_GenerarAlertas
ON vac.Brote
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Hoy DATE = CAST(GETDATE() AS DATE);

    SELECT z.IdPaciente, z.IdEsquema, MIN(z.IdBrote) AS IdBrote
    INTO #Pendientes
    FROM vac.fn_PendientesZonaBrote(@Hoy) z
    JOIN inserted i ON i.IdBrote = z.IdBrote
    GROUP BY z.IdPaciente, z.IdEsquema;

    UPDATE a
    SET TipoAlerta = 'ZONA_BROTE', IdBrote = x.IdBrote
    FROM vac.Alerta a
    JOIN #Pendientes x ON x.IdPaciente = a.IdPaciente AND x.IdEsquema = a.IdEsquema
    WHERE a.Estado = 'PENDIENTE' AND a.TipoAlerta = 'DOSIS_ATRASADA';

    INSERT INTO vac.Alerta (IdPaciente, IdEsquema, IdBrote, TipoAlerta)
    SELECT x.IdPaciente, x.IdEsquema, x.IdBrote, 'ZONA_BROTE'
    FROM #Pendientes x
    WHERE NOT EXISTS (SELECT 1 FROM vac.Alerta a
                      WHERE a.IdPaciente = x.IdPaciente
                        AND a.IdEsquema  = x.IdEsquema
                        AND a.Estado     = 'PENDIENTE');
END
GO

/* ---------------------------------------------------------------------
   T4. trg_Paciente_AlertaZonaBrote  (AFTER INSERT, UPDATE)
   Alerta automática cuando un paciente registrado (o que se muda) a un
   distrito con brote activo tiene dosis pendientes. Si se muda fuera
   de la zona, descarta las alertas del brote anterior.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_Paciente_AlertaZonaBrote
ON vac.Paciente
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT (UPDATE(IdDistrito) OR UPDATE(FechaNacimiento)) RETURN;
    IF NOT EXISTS (SELECT 1 FROM vac.Brote WHERE FechaFin IS NULL) RETURN;

    DECLARE @Hoy DATE = CAST(GETDATE() AS DATE);

    UPDATE a
    SET Estado = 'DESCARTADA', FechaAtencion = SYSDATETIME()
    FROM vac.Alerta a
    JOIN inserted i ON i.IdPaciente = a.IdPaciente
    JOIN vac.Brote b ON b.IdBrote = a.IdBrote
    WHERE a.Estado = 'PENDIENTE'
      AND a.TipoAlerta = 'ZONA_BROTE'
      AND b.IdDistrito <> i.IdDistrito;

    INSERT INTO vac.Alerta (IdPaciente, IdEsquema, IdBrote, TipoAlerta)
    SELECT z.IdPaciente, z.IdEsquema, MIN(z.IdBrote), 'ZONA_BROTE'
    FROM vac.fn_PendientesZonaBrote(@Hoy) z
    JOIN inserted i ON i.IdPaciente = z.IdPaciente
    WHERE NOT EXISTS (SELECT 1 FROM vac.Alerta a
                      WHERE a.IdPaciente = z.IdPaciente
                        AND a.IdEsquema  = z.IdEsquema
                        AND a.Estado     = 'PENDIENTE')
    GROUP BY z.IdPaciente, z.IdEsquema;
END
GO

/* ---------------------------------------------------------------------
   T5. trg_DosisAplicada_Auditoria  (AFTER UPDATE, DELETE)
   Guarda en JSON el estado anterior y nuevo de toda dosis corregida o
   eliminada: el registro de vacunación es un documento clínico.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_DosisAplicada_Auditoria
ON vac.DosisAplicada
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO vac.AuditoriaDosis (IdDosis, Operacion, DatosAnteriores, DatosNuevos)
    SELECT d.IdDosis,
           CASE WHEN i.IdDosis IS NULL THEN 'D' ELSE 'U' END,
           (SELECT d.IdPaciente, d.IdEsquema, d.IdLote, d.IdEstablecimiento, d.IdVacunador,
                   d.IdCampana, d.FechaAplicacion
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
           CASE WHEN i.IdDosis IS NOT NULL THEN
               (SELECT i.IdPaciente, i.IdEsquema, i.IdLote, i.IdEstablecimiento, i.IdVacunador,
                       i.IdCampana, i.FechaAplicacion
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
           END
    FROM deleted d
    LEFT JOIN inserted i ON i.IdDosis = d.IdDosis;
END
GO
