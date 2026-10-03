/* =====================================================================
   VacunApp Tacna - 03. Funciones y vistas de reporte
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   fn_DosisPendientes: dosis que a una fecha de corte ya corresponden
   por edad (y cuya ventana no ha cerrado) pero no han sido aplicadas.
   Función inline con valores de tabla: el optimizador la expande como
   una vista parametrizada.
   --------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION vac.fn_DosisPendientes (@FechaCorte DATE)
RETURNS TABLE
AS
RETURN
    SELECT p.IdPaciente,
           p.IdDistrito,
           e.IdEsquema,
           e.IdVacuna,
           e.NumeroDosis,
           ed.EdadMeses,
           ed.EdadMeses - e.EdadMinimaMeses AS MesesAtraso
    FROM vac.Paciente p
    CROSS APPLY (SELECT vac.fn_EdadMeses(p.FechaNacimiento, @FechaCorte) AS EdadMeses) ed
    JOIN vac.EsquemaDosis e
      ON ed.EdadMeses >= e.EdadMinimaMeses
     AND (e.EdadMaximaMeses IS NULL OR ed.EdadMeses <= e.EdadMaximaMeses)
    WHERE NOT EXISTS (SELECT 1
                      FROM vac.DosisAplicada d
                      WHERE d.IdPaciente = p.IdPaciente
                        AND d.IdEsquema  = e.IdEsquema);
GO

/* fn_PendientesZonaBrote: dosis pendientes de pacientes que viven en un
   distrito con brote activo, solo de vacunas que previenen esa enfermedad.
   La usan los triggers de alertas. */
CREATE OR ALTER FUNCTION vac.fn_PendientesZonaBrote (@FechaCorte DATE)
RETURNS TABLE
AS
RETURN
    SELECT dp.IdPaciente, dp.IdEsquema, b.IdBrote, dp.IdDistrito
    FROM vac.fn_DosisPendientes(@FechaCorte) dp
    JOIN vac.Brote b
      ON b.IdDistrito = dp.IdDistrito
     AND b.FechaFin IS NULL
    JOIN vac.VacunaEnfermedad ve
      ON ve.IdVacuna     = dp.IdVacuna
     AND ve.IdEnfermedad = b.IdEnfermedad;
GO

/* ---------------------------------------------------------------------
   Vistas
   --------------------------------------------------------------------- */

/* Cobertura por distrito y dosis del esquema.
   Elegibles = pacientes registrados que ya alcanzaron la edad mínima.
   Vacunados = elegibles que tienen esa dosis aplicada. */
CREATE OR ALTER VIEW vac.vw_CoberturaDistrito
AS
    SELECT pr.Nombre                 AS Provincia,
           d.Ubigeo,
           d.Nombre                  AS Distrito,
           v.Codigo                  AS CodigoVacuna,
           v.Nombre                  AS Vacuna,
           e.NumeroDosis,
           e.Descripcion             AS Dosis,
           COUNT(*)                  AS Elegibles,
           COUNT(da.IdDosis)         AS Vacunados,
           COUNT(*) - COUNT(da.IdDosis) AS SinVacunar,
           CAST(100.0 * COUNT(da.IdDosis) / COUNT(*) AS DECIMAL(5,2)) AS PorcentajeCobertura
    FROM vac.Paciente p
    JOIN vac.Distrito d      ON d.IdDistrito   = p.IdDistrito
    JOIN vac.Provincia pr    ON pr.IdProvincia = d.IdProvincia
    CROSS APPLY (SELECT vac.fn_EdadMeses(p.FechaNacimiento, CAST(GETDATE() AS DATE)) AS EdadMeses) ed
    JOIN vac.EsquemaDosis e  ON ed.EdadMeses >= e.EdadMinimaMeses
    JOIN vac.Vacuna v        ON v.IdVacuna = e.IdVacuna
    LEFT JOIN vac.DosisAplicada da
           ON da.IdPaciente = p.IdPaciente
          AND da.IdEsquema  = e.IdEsquema
    GROUP BY pr.Nombre, d.Ubigeo, d.Nombre, v.Codigo, v.Nombre, e.NumeroDosis, e.Descripcion;
GO

/* Cobertura de SPR (sarampión) por distrito con nivel de riesgo.
   Meta OMS/OPS para interrumpir la transmisión: >= 95 % con ambas dosis. */
CREATE OR ALTER VIEW vac.vw_CoberturaSarampion
AS
    WITH spr AS (
        SELECT p.IdDistrito,
               SUM(CASE WHEN ed.EdadMeses >= 12 THEN 1 ELSE 0 END)                                     AS ElegiblesSPR1,
               SUM(CASE WHEN ed.EdadMeses >= 12 AND d1.IdDosis IS NOT NULL THEN 1 ELSE 0 END)          AS ConSPR1,
               SUM(CASE WHEN ed.EdadMeses >= 18 THEN 1 ELSE 0 END)                                     AS ElegiblesSPR2,
               SUM(CASE WHEN ed.EdadMeses >= 18 AND d2.IdDosis IS NOT NULL THEN 1 ELSE 0 END)          AS ConSPR2
        FROM vac.Paciente p
        CROSS APPLY (SELECT vac.fn_EdadMeses(p.FechaNacimiento, CAST(GETDATE() AS DATE)) AS EdadMeses) ed
        LEFT JOIN vac.DosisAplicada d1
               ON d1.IdPaciente = p.IdPaciente
              AND d1.IdEsquema  = (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1)
        LEFT JOIN vac.DosisAplicada d2
               ON d2.IdPaciente = p.IdPaciente
              AND d2.IdEsquema  = (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 2)
        GROUP BY p.IdDistrito
    ),
    brote AS (
        SELECT b.IdDistrito, SUM(b.CasosConfirmados) AS Casos
        FROM vac.Brote b
        JOIN vac.Enfermedad en ON en.IdEnfermedad = b.IdEnfermedad
        WHERE b.FechaFin IS NULL AND en.Nombre = 'Sarampión'
        GROUP BY b.IdDistrito
    )
    SELECT pr.Nombre AS Provincia,
           d.Ubigeo,
           d.Nombre  AS Distrito,
           ISNULL(s.ElegiblesSPR1, 0) AS ElegiblesSPR1,
           ISNULL(s.ConSPR1, 0)       AS ConSPR1,
           CAST(100.0 * s.ConSPR1 / NULLIF(s.ElegiblesSPR1, 0) AS DECIMAL(5,2)) AS CoberturaSPR1,
           ISNULL(s.ElegiblesSPR2, 0) AS ElegiblesSPR2,
           ISNULL(s.ConSPR2, 0)       AS ConSPR2,
           CAST(100.0 * s.ConSPR2 / NULLIF(s.ElegiblesSPR2, 0) AS DECIMAL(5,2)) AS CoberturaSPR2,
           CAST(CASE WHEN b.IdDistrito IS NULL THEN 0 ELSE 1 END AS BIT)        AS BroteActivo,
           ISNULL(b.Casos, 0)                                                   AS CasosConfirmados,
           CASE
               WHEN b.IdDistrito IS NOT NULL
                 OR 100.0 * s.ConSPR1 / NULLIF(s.ElegiblesSPR1, 0) < 80 THEN 'ALTO'
               WHEN 100.0 * s.ConSPR2 / NULLIF(s.ElegiblesSPR2, 0) < 95 THEN 'MEDIO'
               ELSE 'BAJO'
           END AS NivelRiesgo
    FROM vac.Distrito d
    JOIN vac.Provincia pr ON pr.IdProvincia = d.IdProvincia
    LEFT JOIN spr s       ON s.IdDistrito   = d.IdDistrito
    LEFT JOIN brote b     ON b.IdDistrito   = d.IdDistrito;
GO

/* Dosis pendientes a la fecha, con datos legibles. */
CREATE OR ALTER VIEW vac.vw_DosisPendientes
AS
    SELECT dp.IdPaciente,
           p.TipoDocumento,
           p.NumeroDocumento,
           CONCAT(p.ApellidoPaterno, ' ', p.ApellidoMaterno, ', ', p.Nombres) AS Paciente,
           p.FechaNacimiento,
           dp.EdadMeses,
           p.Telefono,
           d.Ubigeo,
           d.Nombre           AS Distrito,
           v.Codigo           AS CodigoVacuna,
           v.Nombre           AS Vacuna,
           e.NumeroDosis,
           e.Descripcion      AS Dosis,
           dp.MesesAtraso
    FROM vac.fn_DosisPendientes(CAST(GETDATE() AS DATE)) dp
    JOIN vac.Paciente p     ON p.IdPaciente = dp.IdPaciente
    JOIN vac.Distrito d     ON d.IdDistrito = dp.IdDistrito
    JOIN vac.EsquemaDosis e ON e.IdEsquema  = dp.IdEsquema
    JOIN vac.Vacuna v       ON v.IdVacuna   = dp.IdVacuna;
GO

/* Alertas pendientes con el detalle necesario para salir a vacunar. */
CREATE OR ALTER VIEW vac.vw_AlertasPendientes
AS
    SELECT a.IdAlerta,
           a.TipoAlerta,
           a.FechaGeneracion,
           DATEDIFF(DAY, a.FechaGeneracion, SYSDATETIME()) AS DiasAbierta,
           p.NumeroDocumento,
           CONCAT(p.ApellidoPaterno, ' ', p.ApellidoMaterno, ', ', p.Nombres) AS Paciente,
           vac.fn_EdadMeses(p.FechaNacimiento, CAST(GETDATE() AS DATE)) AS EdadMeses,
           p.Telefono,
           p.Direccion,
           d.Ubigeo,
           d.Nombre      AS Distrito,
           v.Codigo      AS CodigoVacuna,
           e.NumeroDosis,
           e.Descripcion AS Dosis,
           en.Nombre     AS EnfermedadBrote
    FROM vac.Alerta a
    JOIN vac.Paciente p     ON p.IdPaciente = a.IdPaciente
    JOIN vac.Distrito d     ON d.IdDistrito = p.IdDistrito
    JOIN vac.EsquemaDosis e ON e.IdEsquema  = a.IdEsquema
    JOIN vac.Vacuna v       ON v.IdVacuna   = e.IdVacuna
    LEFT JOIN vac.Brote b   ON b.IdBrote    = a.IdBrote
    LEFT JOIN vac.Enfermedad en ON en.IdEnfermedad = b.IdEnfermedad
    WHERE a.Estado = 'PENDIENTE';
GO

/* Avance de cada campaña por distrito frente a su meta. */
CREATE OR ALTER VIEW vac.vw_AvanceCampana
AS
    SELECT c.IdCampana,
           c.Nombre AS Campana,
           c.FechaInicio,
           c.FechaFin,
           d.Nombre AS Distrito,
           cd.MetaDosis,
           ISNULL(x.DosisAplicadas, 0) AS DosisAplicadas,
           CAST(100.0 * ISNULL(x.DosisAplicadas, 0) / cd.MetaDosis AS DECIMAL(6,2)) AS PorcentajeAvance
    FROM vac.CampanaDistrito cd
    JOIN vac.Campana c  ON c.IdCampana  = cd.IdCampana
    JOIN vac.Distrito d ON d.IdDistrito = cd.IdDistrito
    OUTER APPLY (SELECT COUNT(*) AS DosisAplicadas
                 FROM vac.DosisAplicada da
                 JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = da.IdEstablecimiento
                 WHERE da.IdCampana = cd.IdCampana
                   AND es.IdDistrito = cd.IdDistrito) x;
GO

/* Historial de vacunación (carné) de cada paciente. */
CREATE OR ALTER VIEW vac.vw_HistorialVacunacion
AS
    SELECT p.IdPaciente,
           p.NumeroDocumento,
           CONCAT(p.ApellidoPaterno, ' ', p.ApellidoMaterno, ', ', p.Nombres) AS Paciente,
           v.Codigo      AS CodigoVacuna,
           v.Nombre      AS Vacuna,
           e.NumeroDosis,
           e.Descripcion AS Dosis,
           da.FechaAplicacion,
           vac.fn_EdadMeses(p.FechaNacimiento, da.FechaAplicacion) AS EdadMesesAlAplicar,
           l.NumeroLote,
           es.Nombre     AS Establecimiento,
           CONCAT(ps.Nombres, ' ', ps.Apellidos) AS Vacunador,
           c.Nombre      AS Campana
    FROM vac.DosisAplicada da
    JOIN vac.Paciente p              ON p.IdPaciente         = da.IdPaciente
    JOIN vac.EsquemaDosis e          ON e.IdEsquema          = da.IdEsquema
    JOIN vac.Vacuna v                ON v.IdVacuna           = e.IdVacuna
    JOIN vac.LoteVacuna l            ON l.IdLote             = da.IdLote
    JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = da.IdEstablecimiento
    JOIN vac.Vacunador ps        ON ps.IdVacunador        = da.IdVacunador
    LEFT JOIN vac.Campana c          ON c.IdCampana          = da.IdCampana;
GO

/* Indicadores generales para el tablero. */
CREATE OR ALTER VIEW vac.vw_ResumenGeneral
AS
    SELECT (SELECT COUNT(*) FROM vac.Paciente)                               AS TotalPacientes,
           (SELECT COUNT(*) FROM vac.DosisAplicada)                          AS TotalDosis,
           (SELECT COUNT(*) FROM vac.Alerta WHERE Estado = 'PENDIENTE')      AS AlertasPendientes,
           (SELECT COUNT(*) FROM vac.Brote WHERE FechaFin IS NULL)           AS BrotesActivos,
           (SELECT ISNULL(SUM(CasosConfirmados), 0) FROM vac.Brote WHERE FechaFin IS NULL) AS CasosActivos,
           (SELECT CAST(100.0 * SUM(ConSPR1) / NULLIF(SUM(ElegiblesSPR1), 0) AS DECIMAL(5,2)) FROM vac.vw_CoberturaSarampion) AS CoberturaRegionalSPR1,
           (SELECT CAST(100.0 * SUM(ConSPR2) / NULLIF(SUM(ElegiblesSPR2), 0) AS DECIMAL(5,2)) FROM vac.vw_CoberturaSarampion) AS CoberturaRegionalSPR2;
GO

/* Auditoría de dosis (RN-23): descompone el JSON que guarda trg_DosisAplicada_Auditoria
   para que se pueda consultar sin leer JSON a mano. */
CREATE OR ALTER VIEW vac.vw_AuditoriaDosis
AS
    SELECT a.IdAuditoria, a.IdDosis, a.Operacion, a.Fecha, a.Usuario,
           p.NumeroDocumento,
           CONCAT(p.ApellidoPaterno, ' ', p.ApellidoMaterno, ', ', p.Nombres) AS Paciente,
           v.Codigo AS CodigoVacuna, e.NumeroDosis,
           TRY_CAST(JSON_VALUE(a.DatosAnteriores, '$.FechaAplicacion') AS DATE) AS FechaAplicacionAnterior,
           TRY_CAST(JSON_VALUE(a.DatosNuevos,     '$.FechaAplicacion') AS DATE) AS FechaAplicacionNueva,
           la.NumeroLote AS LoteAnterior, ln.NumeroLote AS LoteNuevo,
           a.DatosAnteriores, a.DatosNuevos
    FROM vac.AuditoriaDosis a
    LEFT JOIN vac.Paciente p     ON p.IdPaciente = TRY_CAST(JSON_VALUE(a.DatosAnteriores, '$.IdPaciente') AS INT)
    LEFT JOIN vac.EsquemaDosis e ON e.IdEsquema  = TRY_CAST(JSON_VALUE(a.DatosAnteriores, '$.IdEsquema') AS SMALLINT)
    LEFT JOIN vac.Vacuna v       ON v.IdVacuna   = e.IdVacuna
    LEFT JOIN vac.LoteVacuna la  ON la.IdLote    = TRY_CAST(JSON_VALUE(a.DatosAnteriores, '$.IdLote') AS INT)
    LEFT JOIN vac.LoteVacuna ln  ON ln.IdLote    = TRY_CAST(JSON_VALUE(a.DatosNuevos,     '$.IdLote') AS INT);
GO
