/* =====================================================================
   VacunApp Tacna - 07. Pruebas funcionales y de rendimiento
   Cada caso corre en su propia transacción y termina en ROLLBACK, así
   que el script se puede ejecutar N veces sin alterar los datos.
   Los resultados se guardan en una variable de tabla, que no se ve
   afectada por el ROLLBACK.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO
SET NOCOUNT ON;

DECLARE @R TABLE (Caso INT IDENTITY, Objeto VARCHAR(40), Prueba VARCHAR(120), Esperado VARCHAR(40), Obtenido VARCHAR(200));

DECLARE @Hoy DATE = CAST(GETDATE() AS DATE);
DECLARE @Nac20m DATE = DATEADD(MONTH, -20, @Hoy);
DECLARE @IdPac INT, @IdAux INT, @IdDosis BIGINT, @IdBrote INT, @n INT, @Err VARCHAR(200);
DECLARE @Est SMALLINT, @Dni CHAR(8), @IdVacunador INT, @IdSPR1 SMALLINT;
DECLARE @LoteSPR VARCHAR(20)        = CONCAT('SPR-', YEAR(@Hoy), '-1');
DECLARE @LoteSPRVencido VARCHAR(20) = CONCAT('SPR-', YEAR(@Hoy) - 11, '-1');

SELECT TOP (1) @Est = es.IdEstablecimiento, @Dni = ps.Dni, @IdVacunador = ps.IdVacunador
FROM vac.EstablecimientoSalud es
JOIN vac.Distrito d       ON d.IdDistrito = es.IdDistrito
JOIN vac.Vacunador ps ON ps.IdEstablecimiento = es.IdEstablecimiento
WHERE d.Ubigeo = '230104';

SELECT @IdSPR1 = e.IdEsquema
FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna
WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 1;

/* =================== Casos negativos: deben fallar =================== */

/* N1. Documento duplicado */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'X', 'Y', NULL, '2024-01-01', 'F', '230101', NULL, NULL, @IdAux OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_RegistrarPaciente', 'Rechaza documento duplicado', '50004', @Err);

/* N2. Ubigeo fuera de Tacna */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000002', 'X', 'Y', NULL, '2024-01-01', 'F', '150101', NULL, NULL, @IdAux OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_RegistrarPaciente', 'Rechaza ubigeo fuera de Tacna', '50001', @Err);

/* N3. Fecha de nacimiento futura */
BEGIN TRANSACTION;
BEGIN TRY
    DECLARE @Manana DATE = DATEADD(DAY, 1, @Hoy);
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000002', 'X', 'Y', NULL, @Manana, 'F', '230101', NULL, NULL, @IdAux OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_RegistrarPaciente', 'Rechaza fecha de nacimiento futura', '50002', @Err);

/* N4. 2.a dosis sin la 1.a (trigger) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 2, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza 2.a dosis sin la 1.a', '50104', @Err);

/* N5. Lote vencido (trigger) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPRVencido, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza lote vencido', '50101', @Err);

/* N6. Lote de otra vacuna, con INSERT directo (sin pasar por el SP) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, FechaAplicacion)
    VALUES (@IdPac, @IdSPR1, (SELECT TOP (1) IdLote FROM vac.LoteVacuna WHERE NumeroLote LIKE 'BCG-%'), @Est, @IdVacunador, @Hoy);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza lote que no es de la vacuna', '50100', @Err);

/* N7. Edad insuficiente: SPR a un recién nacido (trigger) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000003', 'Bebé', 'Prueba', NULL, @Hoy, 'F', '230101', NULL, NULL, @IdAux OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarDosis '89000003', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza SPR en recién nacido', '50103', @Err);

/* N8. Vacunador de otro establecimiento, con INSERT directo (trigger) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, FechaAplicacion)
    VALUES (@IdPac, @IdSPR1, (SELECT IdLote FROM vac.LoteVacuna WHERE NumeroLote = @LoteSPR), @Est,
            (SELECT TOP (1) IdVacunador FROM vac.Vacunador WHERE IdEstablecimiento <> @Est), @Hoy);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza vacunador de otro establecimiento', '50105', @Err);

/* N9. Dosis ya registrada (SP) */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_RegistrarDosis', 'Rechaza dosis ya registrada', '50015', @Err);

/* N10. Segundo brote activo igual (SP) */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_DeclararBrote '230104', 'Sarampión', @Hoy, 1, @IdBrote OUTPUT, @n OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_DeclararBrote', 'Rechaza segundo brote activo igual', '50022', @Err);

/* N11. Dosis fuera del periodo de la campaña (SP): la campaña 2 terminó el 31/08/2026 */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, '2026-09-15', 2, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_RegistrarDosis', 'Rechaza dosis fuera del periodo de campaña', '50014', @Err);

/* =================== Casos positivos: flujo completo =================== */
BEGIN TRANSACTION;

/* P1. Registro en zona de brote genera alertas (SPR1 y SPR2) */
EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', 'Test', @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
SELECT @n = COUNT(*) FROM vac.Alerta WHERE IdPaciente = @IdPac AND TipoAlerta = 'ZONA_BROTE' AND Estado = 'PENDIENTE';
INSERT @R VALUES ('usp_RegistrarPaciente', 'Registra paciente válido', 'OK', IIF(@IdPac IS NOT NULL, 'OK', 'NULL'));
INSERT @R VALUES ('trg_Paciente_AlertaZonaBrote', 'Genera alertas SPR1 y SPR2 en zona de brote', '2', CAST(@n AS VARCHAR));

/* P2. Dosis válida cierra su alerta */
SET @IdDosis = NULL;
EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
SELECT @Err = Estado FROM vac.Alerta WHERE IdPaciente = @IdPac AND IdEsquema = @IdSPR1;
INSERT @R VALUES ('usp_RegistrarDosis', 'Registra SPR1 válida', 'OK', IIF(@IdDosis IS NOT NULL, 'OK', 'NULL'));
INSERT @R VALUES ('trg_DosisAplicada_AtenderAlertas', 'La alerta de SPR1 pasa a ATENDIDA', 'ATENDIDA', ISNULL(@Err, 'sin alerta'));

/* P3. Corrección de la dosis queda auditada */
UPDATE vac.DosisAplicada SET FechaAplicacion = DATEADD(DAY, -1, FechaAplicacion) WHERE IdDosis = @IdDosis;
DELETE FROM vac.DosisAplicada WHERE IdDosis = @IdDosis;
SELECT @n = COUNT(*) FROM vac.AuditoriaDosis WHERE IdDosis = @IdDosis;
INSERT @R VALUES ('trg_DosisAplicada_Auditoria', 'UPDATE y DELETE quedan auditados', '2', CAST(@n AS VARCHAR));

/* P4. El paciente se muda fuera de la zona de brote */
UPDATE vac.Paciente SET IdDistrito = (SELECT IdDistrito FROM vac.Distrito WHERE Ubigeo = '230401') WHERE IdPaciente = @IdPac;
SELECT @n = COUNT(*) FROM vac.Alerta WHERE IdPaciente = @IdPac AND TipoAlerta = 'ZONA_BROTE' AND Estado = 'PENDIENTE';
INSERT @R VALUES ('trg_Paciente_AlertaZonaBrote', 'Al mudarse se descartan sus alertas de brote', '0', CAST(@n AS VARCHAR));

/* P5. Declarar brote de rubéola en Tarata genera alertas */
EXEC vac.usp_DeclararBrote '230401', 'Rubéola', @Hoy, 1, @IdBrote OUTPUT, @n OUTPUT;
INSERT @R VALUES ('trg_Brote_GenerarAlertas', 'Brote nuevo genera alertas', 'OK', IIF(@n > 0, 'OK', CONCAT('alertas=', @n)));

/* P6. Cerrar el brote descarta sus alertas */
EXEC vac.usp_CerrarBrote @IdBrote;
SELECT @n = COUNT(*) FROM vac.Alerta WHERE IdBrote = @IdBrote AND Estado = 'PENDIENTE';
INSERT @R VALUES ('usp_CerrarBrote', 'No quedan alertas pendientes del brote', '0', CAST(@n AS VARCHAR));

/* P7. Atender una alerta manualmente */
DECLARE @IdAlerta BIGINT = (SELECT TOP (1) IdAlerta FROM vac.Alerta WHERE Estado = 'PENDIENTE');
EXEC vac.usp_AtenderAlerta @IdAlerta, 'DESCARTADA';
SELECT @Err = Estado FROM vac.Alerta WHERE IdAlerta = @IdAlerta;
INSERT @R VALUES ('usp_AtenderAlerta', 'Cambia el estado de la alerta', 'DESCARTADA', @Err);

/* P8. La generación por lotes es idempotente */
EXEC vac.usp_GenerarAlertasAtrasadas 2, @n OUTPUT;
EXEC vac.usp_GenerarAlertasAtrasadas 2, @n OUTPUT;
INSERT @R VALUES ('usp_GenerarAlertasAtrasadas', 'Segunda ejecución no crea duplicados', '0', CAST(@n AS VARCHAR));

IF @@TRANCOUNT > 0 ROLLBACK;

/* P9-P11. Consultas (sin modificar datos) */
DECLARE @Doc VARCHAR(12) = (SELECT TOP (1) NumeroDocumento FROM vac.Paciente ORDER BY IdPaciente);
BEGIN TRY
    EXEC vac.usp_HistorialPaciente @Doc;
    SET @Err = 'OK';
END TRY BEGIN CATCH SET @Err = ERROR_MESSAGE(); END CATCH;
INSERT @R VALUES ('usp_HistorialPaciente', 'Devuelve el carné del paciente', 'OK', @Err);

BEGIN TRY
    EXEC vac.usp_ReporteCoberturaDistrito @CodigoVacuna = 'SPR', @NumeroDosis = 1;
    SET @Err = 'OK';
END TRY BEGIN CATCH SET @Err = ERROR_MESSAGE(); END CATCH;
INSERT @R VALUES ('usp_ReporteCoberturaDistrito', 'Reporte SPR1 por distrito', 'OK', @Err);

BEGIN TRY
    EXEC vac.usp_ListarPendientes @Ubigeo = '230104', @SoloZonaBrote = 1, @Top = 20;
    SET @Err = 'OK';
END TRY BEGIN CATCH SET @Err = ERROR_MESSAGE(); END CATCH;
INSERT @R VALUES ('usp_ListarPendientes', 'Pendientes en zona de brote', 'OK', @Err);

SELECT Caso, IIF(Esperado = Obtenido, 'OK', 'FALLA') AS Resultado, Objeto, Prueba, Esperado, Obtenido
FROM @R ORDER BY Caso;
SELECT SUM(IIF(Esperado = Obtenido, 1, 0)) AS Correctas, COUNT(*) AS Total FROM @R;
GO

/* ---------------------------------------------------------------------
   Rendimiento: tiempo de cada reporte (objetivo < 2 000 ms)
   --------------------------------------------------------------------- */
SET NOCOUNT ON;
DECLARE @T TABLE (Reporte VARCHAR(60), Filas INT, Milisegundos INT);
DECLARE @i DATETIME2(7), @f INT;

SET @i = SYSDATETIME();
SELECT * INTO #t1 FROM vac.vw_CoberturaDistrito; SET @f = @@ROWCOUNT;
INSERT @T VALUES ('vw_CoberturaDistrito (todas las dosis)', @f, DATEDIFF(MILLISECOND, @i, SYSDATETIME()));

SET @i = SYSDATETIME();
SELECT * INTO #t2 FROM vac.vw_CoberturaSarampion; SET @f = @@ROWCOUNT;
INSERT @T VALUES ('vw_CoberturaSarampion', @f, DATEDIFF(MILLISECOND, @i, SYSDATETIME()));

SET @i = SYSDATETIME();
SELECT * INTO #t3 FROM vac.vw_DosisPendientes; SET @f = @@ROWCOUNT;
INSERT @T VALUES ('vw_DosisPendientes', @f, DATEDIFF(MILLISECOND, @i, SYSDATETIME()));

SET @i = SYSDATETIME();
SELECT * INTO #t4 FROM vac.vw_AlertasPendientes; SET @f = @@ROWCOUNT;
INSERT @T VALUES ('vw_AlertasPendientes', @f, DATEDIFF(MILLISECOND, @i, SYSDATETIME()));

SET @i = SYSDATETIME();
SELECT * INTO #t5 FROM vac.vw_ResumenGeneral; SET @f = @@ROWCOUNT;
INSERT @T VALUES ('vw_ResumenGeneral', @f, DATEDIFF(MILLISECOND, @i, SYSDATETIME()));

SELECT Reporte, Filas, Milisegundos, IIF(Milisegundos < 2000, 'CUMPLE', 'NO CUMPLE') AS Objetivo FROM @T;
SELECT (SELECT COUNT(*) FROM vac.Paciente) AS Pacientes, (SELECT COUNT(*) FROM vac.DosisAplicada) AS DosisAplicadas;
GO
