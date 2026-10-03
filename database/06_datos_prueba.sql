/* =====================================================================
   VacunApp Tacna - 06. Datos de prueba SIMULADOS
   Genera @TotalPacientes niños de 0 a 10 años repartidos por distrito
   según un peso aproximado de población, y sus dosis aplicadas según el
   esquema, con coberturas distintas por distrito (más bajas donde luego
   se declara el brote). Todas las dosis pasan por el trigger de
   validación, así que la carga también prueba las reglas de negocio.

   Los valores aleatorios se materializan en tablas temporales antes de
   usarlos: NEWID() se reevalúa en cada referencia de una expresión.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO
SET NOCOUNT ON;

DECLARE @TotalPacientes INT  = 20000;
DECLARE @Hoy            DATE = CAST(GETDATE() AS DATE);

/* ---------------------------------------------------------------------
   Parámetros de simulación por distrito
   Peso: tamaño relativo de la población infantil (aproximado).
   Prob: probabilidad de recibir cada dosis a tiempo, dada la anterior.
   ProbSPR: idem para SPR (menor en los distritos que tendrán brote).
   --------------------------------------------------------------------- */
CREATE TABLE #Dist (IdDistrito SMALLINT PRIMARY KEY, Peso INT, Desde INT, Hasta INT, Prob INT, ProbSPR INT);

INSERT INTO #Dist (IdDistrito, Peso, Prob, ProbSPR)
SELECT d.IdDistrito, x.Peso, x.Prob, x.ProbSPR
FROM (VALUES
    ('230101', 80, 9600, 9500), ('230102', 38, 9100, 8000), ('230103',  3, 9400, 9300),
    ('230104', 36, 9000, 7800), ('230105',  7, 9300, 9100), ('230106',  2, 9200, 9000),
    ('230107',  2, 8900, 8800), ('230108', 21, 9500, 9400), ('230109',  3, 9200, 9000),
    ('230110',110, 9300, 8400), ('230111', 12, 9000, 8800),
    ('230201',  3, 9100, 9000), ('230202',  1, 8700, 8600), ('230203',  1, 8700, 8500),
    ('230204',  1, 8800, 8700), ('230205',  1, 8700, 8600), ('230206',  1, 8600, 8500),
    ('230301',  3, 9300, 9200), ('230302',  7, 9400, 9300), ('230303',  3, 9300, 9200),
    ('230401',  3, 9200, 9100), ('230402',  1, 8700, 8600), ('230403',  1, 8600, 8500),
    ('230404',  1, 8600, 8500), ('230405',  1, 8700, 8600), ('230406',  1, 8600, 8500),
    ('230407',  1, 8800, 8700), ('230408',  1, 8800, 8700)
) AS x (Ubigeo, Peso, Prob, ProbSPR)
JOIN vac.Distrito d ON d.Ubigeo = x.Ubigeo;

WITH acum AS (
    SELECT IdDistrito, SUM(Peso) OVER (ORDER BY IdDistrito ROWS UNBOUNDED PRECEDING) AS Acum, Peso
    FROM #Dist)
UPDATE d SET Desde = a.Acum - a.Peso, Hasta = a.Acum
FROM #Dist d JOIN acum a ON a.IdDistrito = d.IdDistrito;

DECLARE @SumaPesos INT = (SELECT SUM(Peso) FROM #Dist);

/* Listas de nombres frecuentes en Tacna (para datos verosímiles) */
CREATE TABLE #NomM (N INT PRIMARY KEY, Valor VARCHAR(30));
CREATE TABLE #NomF (N INT PRIMARY KEY, Valor VARCHAR(30));
CREATE TABLE #Ape  (N INT PRIMARY KEY, Valor VARCHAR(30));
CREATE TABLE #Calle(N INT PRIMARY KEY, Valor VARCHAR(40));

INSERT INTO #NomM SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v FROM (VALUES
    ('Mateo'),('Santiago'),('Sebastián'),('Thiago'),('Liam'),('Gael'),('Dylan'),('Adrián'),('Joaquín'),('Leonardo'),
    ('Samuel'),('Diego'),('Benjamín'),('Lucas'),('Alejandro'),('Daniel'),('Emiliano'),('Fabián'),('Rodrigo'),('Iker')) t(v);
INSERT INTO #NomF SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v FROM (VALUES
    ('Valentina'),('Camila'),('Sofía'),('Luciana'),('Isabella'),('Mía'),('Ariana'),('Antonella'),('Valeria'),('Emma'),
    ('Alessandra'),('Zoe'),('Fernanda'),('Renata'),('Victoria'),('Mariana'),('Ximena'),('Luana'),('Abigail'),('Kiara')) t(v);
INSERT INTO #Ape SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v FROM (VALUES
    ('Mamani'),('Quispe'),('Condori'),('Flores'),('Ticona'),('Choque'),('Cutipa'),('Apaza'),('Chambilla'),('Laura'),
    ('Pari'),('Huanca'),('Coaquira'),('Ramos'),('Vargas'),('Rodríguez'),('Gómez'),('Torres'),('Rojas'),('Castillo'),
    ('Mendoza'),('Chávez'),('Pérez'),('Soto'),('Valdivia'),('Zapata'),('Arias'),('Copa'),('Calizaya'),('Ale'),
    ('Nina'),('Limache'),('Tintaya'),('Ccallo'),('Ayca'),('Challco'),('Paco'),('Vilca'),('Cruz'),('Espinoza')) t(v);
INSERT INTO #Calle SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, v FROM (VALUES
    ('Av. Bolognesi'),('Calle Zela'),('Av. Leguía'),('Jr. Arias Aragüez'),('Av. Jorge Basadre'),('Calle Deústua'),
    ('Av. Pinto'),('Av. Industrial'),('Calle Blondell'),('Av. Collpa'),('Av. Municipal'),('Asoc. Los Olivos'),
    ('Asoc. Villa El Sol'),('Av. Celestino Vargas'),('Calle Hipólito Unanue'),('Av. Tarata')) t(v);

DECLARE @nM INT = (SELECT COUNT(*) FROM #NomM), @nF INT = (SELECT COUNT(*) FROM #NomF),
        @nA INT = (SELECT COUNT(*) FROM #Ape),  @nC INT = (SELECT COUNT(*) FROM #Calle);

/* ---------------------------------------------------------------------
   Lotes: dos por vacuna y año, vencen el 31/12 del año siguiente.
   --------------------------------------------------------------------- */
WITH anios AS (
    SELECT YEAR(@Hoy) - 11 AS Anio
    UNION ALL SELECT Anio + 1 FROM anios WHERE Anio < YEAR(@Hoy) + 1)
INSERT INTO vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
SELECT v.IdVacuna,
       CONCAT(v.Codigo, '-', a.Anio, '-', k.K),
       CASE (v.IdVacuna + k.K) % 4 WHEN 0 THEN 'Serum Institute of India' WHEN 1 THEN 'Sanofi Pasteur'
                                   WHEN 2 THEN 'GSK' ELSE 'Merck Sharp & Dohme' END,
       DATEFROMPARTS(a.Anio + 1, 12, 31)
FROM vac.Vacuna v
CROSS JOIN anios a
CROSS JOIN (VALUES (1), (2)) k (K);

/* ---------------------------------------------------------------------
   Pacientes
   --------------------------------------------------------------------- */
SELECT TOP (@TotalPacientes)
       ROW_NUMBER() OVER (ORDER BY (SELECT NULL))  AS N,
       ABS(CHECKSUM(NEWID())) % @SumaPesos         AS RDist,
       ABS(CHECKSUM(NEWID())) % 3653               AS RDias,
       ABS(CHECKSUM(NEWID())) % 2                  AS RSexo,
       ABS(CHECKSUM(NEWID())) % 1000               AS RNom,
       ABS(CHECKSUM(NEWID())) % 1000               AS RAp1,
       ABS(CHECKSUM(NEWID())) % 1000               AS RAp2,
       ABS(CHECKSUM(NEWID())) % 1000               AS RCalle,
       ABS(CHECKSUM(NEWID())) % 100000000          AS RTel
INTO #P
FROM sys.all_objects a CROSS JOIN sys.all_objects b;

INSERT INTO vac.Paciente (TipoDocumento, NumeroDocumento, Nombres, ApellidoPaterno, ApellidoMaterno,
                          FechaNacimiento, Sexo, IdDistrito, Direccion, Telefono)
SELECT 'DNI',
       CAST(70000000 + p.N AS CHAR(8)),
       CASE p.RSexo WHEN 0 THEN nf.Valor ELSE nm.Valor END,
       a1.Valor, a2.Valor,
       DATEADD(DAY, -p.RDias, @Hoy),
       CASE p.RSexo WHEN 0 THEN 'F' ELSE 'M' END,
       d.IdDistrito,
       CONCAT(c.Valor, ' ', 100 + p.RCalle % 900),
       CONCAT('9', RIGHT('00000000' + CAST(p.RTel AS VARCHAR(8)), 8))
FROM #P p
JOIN #Dist d  ON p.RDist >= d.Desde AND p.RDist < d.Hasta
JOIN #NomM nm ON nm.N = p.RNom % @nM
JOIN #NomF nf ON nf.N = p.RNom % @nF
JOIN #Ape a1  ON a1.N = p.RAp1 % @nA
JOIN #Ape a2  ON a2.N = p.RAp2 % @nA
JOIN #Calle c ON c.N  = p.RCalle % @nC;

/* ---------------------------------------------------------------------
   Auxiliares para asignar establecimiento, vacunador y campaña
   --------------------------------------------------------------------- */
SELECT IdEstablecimiento, IdDistrito,
       ROW_NUMBER() OVER (PARTITION BY IdDistrito ORDER BY IdEstablecimiento) AS Rn,
       COUNT(*)     OVER (PARTITION BY IdDistrito) AS Cnt
INTO #Est FROM vac.EstablecimientoSalud;

SELECT IdVacunador, IdEstablecimiento,
       ROW_NUMBER() OVER (PARTITION BY IdEstablecimiento ORDER BY IdVacunador) AS Rn
INTO #Per FROM vac.Vacunador;

/* Campaña 1: todos los distritos. Campaña 2 (barrido SPR): provincia Tacna. */
SELECT c.IdCampana, c.FechaInicio, c.FechaFin, d.IdDistrito
INTO #CampDist
FROM vac.Campana c
JOIN vac.Distrito d ON c.IdCampana = 1
                    OR (c.IdCampana = 2 AND d.IdProvincia = (SELECT IdProvincia FROM vac.Provincia WHERE Nombre = 'Tacna'));

/* ---------------------------------------------------------------------
   Dosis del esquema regular, nivel por nivel (1.a, 2.a, 3.a dosis):
   la dosis k solo se genera si existe la k-1 y respetando el intervalo.
   --------------------------------------------------------------------- */
DECLARE @k TINYINT = 1, @MaxDosis TINYINT = (SELECT MAX(NumeroDosis) FROM vac.EsquemaDosis);

WHILE @k <= @MaxDosis
BEGIN
    IF OBJECT_ID('tempdb..#Cand') IS NOT NULL DROP TABLE #Cand;

    SELECT p.IdPaciente, p.IdDistrito, p.FechaNacimiento, e.IdEsquema, e.IdVacuna, v.Codigo,
           e.EdadMinimaMeses, e.EdadMaximaMeses, prev.FechaAplicacion AS FechaPrevia, e.IntervaloMinDias,
           3 + ABS(CHECKSUM(NEWID())) % 46  AS RDiasEdad,
           ABS(CHECKSUM(NEWID())) % 21      AS RDiasIntervalo,
           ABS(CHECKSUM(NEWID())) % 10000   AS RProb,
           ABS(CHECKSUM(NEWID())) % 1000    AS RSel,
           CAST(NULL AS DATE)               AS Fecha
    INTO #Cand
    FROM vac.Paciente p
    JOIN vac.EsquemaDosis e ON e.NumeroDosis = @k
    JOIN vac.Vacuna v       ON v.IdVacuna    = e.IdVacuna
    OUTER APPLY (SELECT da.FechaAplicacion
                 FROM vac.DosisAplicada da
                 JOIN vac.EsquemaDosis ep ON ep.IdEsquema = da.IdEsquema
                 WHERE da.IdPaciente = p.IdPaciente
                   AND ep.IdVacuna   = e.IdVacuna
                   AND ep.NumeroDosis = @k - 1) prev
    WHERE @k = 1 OR prev.FechaAplicacion IS NOT NULL;

    UPDATE #Cand
    SET Fecha = CASE
                    WHEN FechaPrevia IS NULL
                      OR DATEADD(DAY, RDiasEdad, DATEADD(MONTH, EdadMinimaMeses, FechaNacimiento))
                         >= DATEADD(DAY, IntervaloMinDias + RDiasIntervalo, FechaPrevia)
                    THEN DATEADD(DAY, RDiasEdad, DATEADD(MONTH, EdadMinimaMeses, FechaNacimiento))
                    ELSE DATEADD(DAY, IntervaloMinDias + RDiasIntervalo, FechaPrevia)
                END;

    DELETE c
    FROM #Cand c
    JOIN #Dist d ON d.IdDistrito = c.IdDistrito
    WHERE c.Fecha > @Hoy
       OR vac.fn_EdadMeses(c.FechaNacimiento, c.Fecha) < c.EdadMinimaMeses
       OR (c.EdadMaximaMeses IS NOT NULL AND vac.fn_EdadMeses(c.FechaNacimiento, c.Fecha) > c.EdadMaximaMeses)
       OR c.RProb >= CASE WHEN c.Codigo = 'SPR' THEN d.ProbSPR ELSE d.Prob END;

    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, IdCampana, FechaAplicacion)
    SELECT c.IdPaciente, c.IdEsquema, l.IdLote, es.IdEstablecimiento, pe.IdVacunador, camp.IdCampana, c.Fecha
    FROM #Cand c
    JOIN #Est es ON es.IdDistrito = c.IdDistrito AND es.Rn = c.RSel % es.Cnt + 1
    JOIN #Per pe ON pe.IdEstablecimiento = es.IdEstablecimiento AND pe.Rn = c.RSel % 2 + 1
    JOIN vac.LoteVacuna l ON l.IdVacuna = c.IdVacuna
                         AND l.NumeroLote = CONCAT(c.Codigo, '-', YEAR(c.Fecha), '-', 1 + c.RSel % 2)
    OUTER APPLY (SELECT TOP (1) cd.IdCampana FROM #CampDist cd
                 WHERE cd.IdDistrito = c.IdDistrito AND c.Fecha BETWEEN cd.FechaInicio AND cd.FechaFin) camp;

    PRINT CONCAT('Dosis nivel ', @k, ': ', @@ROWCOUNT, ' registros');
    SET @k += 1;
END

/* ---------------------------------------------------------------------
   Barrido SPR (campaña 2): el 45 % de los niños sin SPR1 de la provincia
   Tacna se vacuna casa por casa entre julio y agosto de 2026.
   --------------------------------------------------------------------- */
DECLARE @IdSPR1 SMALLINT = (SELECT e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
                            WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1);
DECLARE @IniBarrido DATE, @FinBarrido DATE;
SELECT @IniBarrido = FechaInicio, @FinBarrido = FechaFin FROM vac.Campana WHERE IdCampana = 2;

IF OBJECT_ID('tempdb..#Barrido') IS NOT NULL DROP TABLE #Barrido;
SELECT p.IdPaciente, p.IdDistrito, p.FechaNacimiento,
       DATEADD(DAY, ABS(CHECKSUM(NEWID())) % (DATEDIFF(DAY, @IniBarrido, @FinBarrido) + 1), @IniBarrido) AS Fecha,
       ABS(CHECKSUM(NEWID())) % 10000 AS RProb,
       ABS(CHECKSUM(NEWID())) % 1000  AS RSel
INTO #Barrido
FROM vac.Paciente p
JOIN #CampDist cd ON cd.IdCampana = 2 AND cd.IdDistrito = p.IdDistrito
WHERE NOT EXISTS (SELECT 1 FROM vac.DosisAplicada da WHERE da.IdPaciente = p.IdPaciente AND da.IdEsquema = @IdSPR1);

DELETE FROM #Barrido
WHERE RProb >= 4500 OR Fecha > @Hoy OR vac.fn_EdadMeses(FechaNacimiento, Fecha) < 12;

INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, IdCampana, FechaAplicacion)
SELECT c.IdPaciente, @IdSPR1, l.IdLote, es.IdEstablecimiento, pe.IdVacunador, 2, c.Fecha
FROM #Barrido c
JOIN #Est es ON es.IdDistrito = c.IdDistrito AND es.Rn = c.RSel % es.Cnt + 1
JOIN #Per pe ON pe.IdEstablecimiento = es.IdEstablecimiento AND pe.Rn = c.RSel % 2 + 1
JOIN vac.LoteVacuna l ON l.NumeroLote = CONCAT('SPR-', YEAR(c.Fecha), '-', 1 + c.RSel % 2);
PRINT CONCAT('Barrido SPR: ', @@ROWCOUNT, ' dosis');

/* Metas por campaña y distrito: la meta planificada se fija entre el 95 %
   y el 133 % de lo logrado, para simular distritos que no llegaron. */
INSERT INTO vac.CampanaDistrito (IdCampana, IdDistrito, MetaDosis)
SELECT cd.IdCampana, cd.IdDistrito,
       CASE WHEN x.Aplicadas = 0 THEN 10
            ELSE CEILING(x.Aplicadas / ((75 + ABS(CHECKSUM(NEWID())) % 31) / 100.0)) END
FROM (SELECT DISTINCT IdCampana, IdDistrito FROM #CampDist) cd
OUTER APPLY (SELECT COUNT(*) AS Aplicadas
             FROM vac.DosisAplicada da
             JOIN vac.EstablecimientoSalud es ON es.IdEstablecimiento = da.IdEstablecimiento
             WHERE da.IdCampana = cd.IdCampana AND es.IdDistrito = cd.IdDistrito) x;

/* ---------------------------------------------------------------------
   Brotes de sarampión (SIMULADOS: 9 casos, como reportó la DIRESA; la
   distribución por distrito es supuesta) y alertas de dosis atrasadas.
   --------------------------------------------------------------------- */
DECLARE @IdBrote INT, @Alertas INT;
EXEC vac.usp_DeclararBrote '230110', 'Sarampión', '2026-06-15', 4, @IdBrote OUTPUT, @Alertas OUTPUT;
PRINT CONCAT('Brote C. G. Albarracín: ', @Alertas, ' alertas');
EXEC vac.usp_DeclararBrote '230104', 'Sarampión', '2026-06-22', 3, @IdBrote OUTPUT, @Alertas OUTPUT;
PRINT CONCAT('Brote Ciudad Nueva: ', @Alertas, ' alertas');
EXEC vac.usp_DeclararBrote '230102', 'Sarampión', '2026-07-03', 2, @IdBrote OUTPUT, @Alertas OUTPUT;
PRINT CONCAT('Brote Alto de la Alianza: ', @Alertas, ' alertas');

EXEC vac.usp_GenerarAlertasAtrasadas @MesesTolerancia = 2, @AlertasCreadas = @Alertas OUTPUT;
PRINT CONCAT('Alertas de dosis atrasadas: ', @Alertas);

UPDATE STATISTICS vac.Paciente WITH FULLSCAN;
UPDATE STATISTICS vac.DosisAplicada WITH FULLSCAN;
UPDATE STATISTICS vac.Alerta WITH FULLSCAN;

SELECT (SELECT COUNT(*) FROM vac.Paciente)      AS Pacientes,
       (SELECT COUNT(*) FROM vac.DosisAplicada) AS Dosis,
       (SELECT COUNT(*) FROM vac.Alerta)        AS Alertas,
       (SELECT COUNT(*) FROM vac.LoteVacuna)    AS Lotes;
GO
