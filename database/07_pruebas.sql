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

/* N9. Vacunador inactivo, con INSERT directo (RN-09: el trigger también debe cubrirlo) */
BEGIN TRANSACTION;
BEGIN TRY
    DECLARE @PacN9 INT;
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000009', 'Prueba', 'Nueve', NULL, @Nac20m, 'M', '230104', NULL, NULL, @PacN9 OUTPUT;
    UPDATE vac.Vacunador SET Activo = 0 WHERE IdVacunador = @IdVacunador;
    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, FechaAplicacion)
    VALUES (@PacN9, @IdSPR1, (SELECT TOP (1) l.IdLote FROM vac.LoteVacuna l JOIN vac.EsquemaDosis e ON e.IdVacuna = l.IdVacuna
                              WHERE e.IdEsquema = @IdSPR1 AND l.FechaVencimiento >= @Hoy ORDER BY l.FechaVencimiento DESC),
            @Est, @IdVacunador, @Hoy);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_Validar', 'Rechaza vacunador inactivo con INSERT directo', '50106', @Err);

/* A1/A2. Auditoría (RN-23): la vista descompone el JSON y registra al usuario de la aplicación */
DECLARE @PacA INT, @LoteA INT, @IdDosisA BIGINT, @FechaNueva DATE = DATEADD(DAY, -1, @Hoy);
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000010', 'Prueba', 'Auditoria', NULL, @Nac20m, 'M', '230104', NULL, NULL, @PacA OUTPUT;
    SELECT TOP (1) @LoteA = l.IdLote FROM vac.LoteVacuna l JOIN vac.EsquemaDosis e ON e.IdVacuna = l.IdVacuna
    WHERE e.IdEsquema = @IdSPR1 AND l.FechaVencimiento >= @Hoy ORDER BY l.FechaVencimiento DESC;
    INSERT INTO vac.DosisAplicada (IdPaciente, IdEsquema, IdLote, IdEstablecimiento, IdVacunador, FechaAplicacion)
    VALUES (@PacA, @IdSPR1, @LoteA, @Est, @IdVacunador, @Hoy);
    SET @IdDosisA = SCOPE_IDENTITY();
    EXEC sp_set_session_context @key = N'usuario', @value = N'vac01';

    UPDATE vac.DosisAplicada SET FechaAplicacion = @FechaNueva WHERE IdDosis = @IdDosisA;
    SET @Err = (SELECT CONCAT(Operacion, '|', Usuario, '|', FORMAT(FechaAplicacionAnterior, 'yyyy-MM-dd'), '|', FORMAT(FechaAplicacionNueva, 'yyyy-MM-dd'), '|', NumeroDocumento)
                FROM vac.vw_AuditoriaDosis WHERE IdDosis = @IdDosisA AND Operacion = 'U');
    INSERT @R VALUES ('vw_AuditoriaDosis', 'UPDATE: operación, usuario de la app, fechas y paciente', CONCAT('U|vac01|', FORMAT(@Hoy, 'yyyy-MM-dd'), '|', FORMAT(@FechaNueva, 'yyyy-MM-dd'), '|89000010'), @Err);

    DELETE vac.DosisAplicada WHERE IdDosis = @IdDosisA;
    SET @Err = (SELECT CONCAT(Operacion, '|', Usuario, '|', IIF(DatosNuevos IS NULL, 'sin-nuevos', 'con-nuevos'), '|', IIF(FechaAplicacionNueva IS NULL, 'sin-fecha', 'con-fecha'))
                FROM vac.vw_AuditoriaDosis WHERE IdDosis = @IdDosisA AND Operacion = 'D');
    INSERT @R VALUES ('vw_AuditoriaDosis', 'DELETE: operación, usuario y sin datos nuevos', 'D|vac01|sin-nuevos|sin-fecha', @Err);
END TRY BEGIN CATCH
    SET @Err = CAST(ERROR_NUMBER() AS VARCHAR);
    INSERT @R VALUES ('vw_AuditoriaDosis', 'Auditoría de UPDATE y DELETE', 'OK', @Err);
END CATCH;
EXEC sp_set_session_context @key = N'usuario', @value = NULL;
IF @@TRANCOUNT > 0 ROLLBACK;

/* =================== Módulo identidad (08_seguridad.sql) =================== */

/* S1. Los cinco roles existen */
BEGIN TRY
    SELECT @n = COUNT(*) FROM vac.Rol WHERE Nombre IN ('ADMINISTRADOR','EPIDEMIOLOGO','JEFE_ESTABLECIMIENTO','VACUNADOR','CIUDADANO');
    SET @Err = CAST(@n AS VARCHAR);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
INSERT @R VALUES ('Rol', 'Existen los 5 roles del sistema', '5', @Err);

/* S2. Usuarios semilla: uno por rol, ninguno con contraseña en claro */
BEGIN TRY
    SELECT @n = COUNT(*) FROM vac.Usuario
    WHERE NombreUsuario IN ('admin','epi01','jefe01','vac01','ciud01') AND (ClaveHash IS NULL OR LEN(ClaveHash) >= 60);
    SET @Err = CAST(@n AS VARCHAR);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
INSERT @R VALUES ('Usuario', 'Usuarios semilla creados y sin clave en claro', '5', @Err);

/* S3. Un texto corto (contraseña en claro) no se acepta como hash */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.Usuario (NombreUsuario, NombreCompleto, ClaveHash, IdRol)
    VALUES ('t_claro', 'Prueba', 'secreto123', (SELECT IdRol FROM vac.Rol WHERE Nombre = 'EPIDEMIOLOGO'));
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('Usuario', 'Rechaza contraseña almacenada en claro (CHECK)', '547', @Err);

/* S4. Nombre de usuario duplicado (sin distinguir mayúsculas) */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol)
    VALUES ('ADMIN', 'Duplicado', (SELECT IdRol FROM vac.Rol WHERE Nombre = 'ADMINISTRADOR'));
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('Usuario', 'Rechaza nombre de usuario duplicado', '2627', @Err);

/* S5. Vacunador o jefe sin establecimiento (RN-22) */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol)
    VALUES ('t_sinest', 'Sin establecimiento', (SELECT IdRol FROM vac.Rol WHERE Nombre = 'JEFE_ESTABLECIMIENTO'));
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('Usuario', 'Jefe/vacunador exige establecimiento', '547', @Err);

/* S6. Rol inexistente */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol) VALUES ('t_norol', 'Sin rol', 99);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('Usuario', 'Rechaza rol inexistente (FK)', '547', @Err);

/* S7. Solo un ciudadano puede vincularse a pacientes (RN-17) */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
    SELECT (SELECT IdUsuario FROM vac.Usuario WHERE NombreUsuario = 'epi01'), MIN(IdPaciente), 'TUTOR' FROM vac.Paciente;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_Vinculo_SoloCiudadano', 'Rechaza vínculo de un usuario que no es ciudadano', '50201', @Err);

/* S8. El ciudadano semilla ve a sus dos hijos vinculados */
BEGIN TRY
    SELECT @n = COUNT(*) FROM vac.VinculoFamiliar v JOIN vac.Usuario u ON u.IdUsuario = v.IdUsuario WHERE u.NombreUsuario = 'ciud01';
    SET @Err = CAST(@n AS VARCHAR);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
INSERT @R VALUES ('VinculoFamiliar', 'ciud01 tiene 2 pacientes vinculados', '2', @Err);

/* S9. Vínculo duplicado */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
    SELECT TOP (1) IdUsuario, IdPaciente, Parentesco FROM vac.VinculoFamiliar;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('VinculoFamiliar', 'Rechaza vínculo duplicado', '2627', @Err);

/* --- Gestión de usuarios (usp_CrearUsuario / usp_ActualizarUsuario) --- */
DECLARE @HashPrueba VARCHAR(255) = REPLICATE('x', 64), @IdUsu INT, @EstOtro SMALLINT;
SELECT @EstOtro = MIN(IdEstablecimiento) FROM vac.EstablecimientoSalud WHERE IdEstablecimiento <> @Est;

/* S10. Alta válida de un vacunador */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 't_vac', 'Vacunador de prueba', @HashPrueba, 'VACUNADOR', @Est, @IdVacunador, @IdUsu OUTPUT;
    SET @Err = IIF(EXISTS (SELECT 1 FROM vac.Usuario WHERE IdUsuario = @IdUsu AND IdEstablecimiento = @Est), 'OK', 'no se creó');
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Crea un vacunador con su establecimiento', 'OK', @Err);

/* S11. Nombre de usuario repetido (sin distinguir mayúsculas) */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 'ADMIN', 'Repetido', @HashPrueba, 'EPIDEMIOLOGO', NULL, NULL, @IdUsu OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Rechaza nombre de usuario repetido', '50211', @Err);

/* S12. Rol inexistente */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 't_rol', 'Rol malo', @HashPrueba, 'SUPERUSUARIO', NULL, NULL, @IdUsu OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Rechaza rol inexistente', '50212', @Err);

/* S13. Jefe sin establecimiento */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 't_jefe', 'Jefe sin centro', @HashPrueba, 'JEFE_ESTABLECIMIENTO', NULL, NULL, @IdUsu OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Jefe sin establecimiento', '50213', @Err);

/* S14. Vacunador asignado a un establecimiento que no es el suyo */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 't_vac2', 'Vacunador ajeno', @HashPrueba, 'VACUNADOR', @EstOtro, @IdVacunador, @IdUsu OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Vacunador de otro establecimiento', '50215', @Err);

/* S15. Un valor corto (contraseña en claro) no se acepta como hash */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearUsuario 't_claro2', 'Clave en claro', 'secreto123', 'EPIDEMIOLOGO', NULL, NULL, @IdUsu OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearUsuario', 'Rechaza una clave que no es un hash', '50216', @Err);

/* S16. No se puede desactivar al único administrador activo */
BEGIN TRANSACTION;
BEGIN TRY
    SELECT @IdUsu = IdUsuario FROM vac.Usuario WHERE NombreUsuario = 'admin';
    EXEC vac.usp_ActualizarUsuario @IdUsu, 'Administrador del sistema', 'ADMINISTRADOR', NULL, NULL, 0;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ActualizarUsuario', 'No deja el sistema sin administrador activo', '50218', @Err);

/* S17. Cambio de establecimiento de un jefe */
BEGIN TRANSACTION;
BEGIN TRY
    SELECT @IdUsu = IdUsuario FROM vac.Usuario WHERE NombreUsuario = 'jefe01';
    EXEC vac.usp_ActualizarUsuario @IdUsu, 'Jefe (prueba)', 'JEFE_ESTABLECIMIENTO', @EstOtro, NULL, 1;
    SET @Err = IIF((SELECT IdEstablecimiento FROM vac.Usuario WHERE IdUsuario = @IdUsu) = @EstOtro, 'OK', 'no cambió');
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ActualizarUsuario', 'Reasigna un jefe a otro establecimiento', 'OK', @Err);

/* =================== Stock (módulo stock: RN-10…RN-13) =================== */

/* K1. Una existencia no puede ser negativa */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET Cantidad = -1 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('StockLote', 'CHECK: la cantidad no puede ser negativa', '547', @Err);

/* K2. El umbral mínimo no puede ser negativo */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET UmbralMinimo = -5 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('StockLote', 'CHECK: el umbral no puede ser negativo', '547', @Err);

/* K3. Un lote tiene una sola fila por establecimiento */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo)
    SELECT s.IdLote, s.IdEstablecimiento, 10, 5
    FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('StockLote', 'UNIQUE: lote y establecimiento no se repiten', '2627', @Err);

/* K4. Un movimiento de ENTRADA no puede restar */
BEGIN TRANSACTION;
BEGIN TRY
    INSERT vac.MovimientoStock (IdStock, Tipo, Cantidad, Motivo)
    SELECT TOP (1) IdStock, 'ENTRADA', -3, 'prueba' FROM vac.StockLote;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('MovimientoStock', 'CHECK: el signo debe corresponder al tipo', '547', @Err);

/* D1. RN-10: cada dosis descuenta una unidad y deja un movimiento SALIDA */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET Cantidad = 100, UmbralMinimo = 10 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = CONCAT(
        (SELECT s.Cantidad FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est),
        '/',
        (SELECT COUNT(*) FROM vac.MovimientoStock WHERE IdDosis = @IdDosis AND Tipo = 'SALIDA' AND Cantidad = -1));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_DescontarStock', 'RN-10: descuenta 1 y registra el movimiento', '99/1', @Err);

/* D2. RN-11: con stock cero no se aplica la dosis */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET Cantidad = 0 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_DescontarStock', 'RN-11: rechaza la dosis con stock cero', '50107', @Err);

/* D3. RN-11: un lote sin existencias registradas en el establecimiento tampoco se aplica */
BEGIN TRANSACTION;
BEGIN TRY
    DELETE m FROM vac.MovimientoStock m JOIN vac.StockLote s ON s.IdStock = m.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    DELETE s FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_DosisAplicada_DescontarStock', 'RN-11: rechaza si el establecimiento no tiene el lote', '50107', @Err);

/* D4. RN-12: al llegar al umbral se genera una sola alerta de stock bajo */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET Cantidad = 11, UmbralMinimo = 10 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SELECT @n = COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est AND a.Estado = 'PENDIENTE';
    EXEC vac.usp_RegistrarPaciente 'DNI', '89000001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
    EXEC vac.usp_RegistrarDosis '89000001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
    UPDATE s SET Cantidad = 9 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = CONCAT(@n, '/', (SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
                                WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est AND a.Estado = 'PENDIENTE' AND a.TipoAlerta = 'STOCK_BAJO'));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_StockLote_Alerta', 'RN-12: una alerta al llegar al umbral, sin duplicados', '0/1', @Err);

/* D5. La reposición por encima del umbral cierra la alerta de stock bajo */
BEGIN TRANSACTION;
BEGIN TRY
    UPDATE s SET Cantidad = 5, UmbralMinimo = 10 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    UPDATE s SET Cantidad = 500 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = CONCAT(
        (SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
         WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est AND a.Estado = 'PENDIENTE'), '/',
        (SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
         WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est AND a.Estado = 'ATENDIDA'));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_StockLote_Alerta', 'Reponer sobre el umbral cierra la alerta', '0/1', @Err);

/* D6. RN-13: un lote que vence en <= 30 días con existencias genera una alerta, una sola vez */
BEGIN TRANSACTION;
BEGIN TRY
    DECLARE @IdLoteVenc INT, @Cre INT;
    INSERT vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
    SELECT IdVacuna, 'PRB-VENCE-10', 'Prueba', DATEADD(DAY, 10, @Hoy) FROM vac.Vacuna WHERE Codigo = 'SPR';
    SET @IdLoteVenc = SCOPE_IDENTITY();
    INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo) VALUES (@IdLoteVenc, @Est, 40, 5);
    EXEC vac.usp_GenerarAlertasStock @AlertasCreadas = @Cre OUTPUT;
    EXEC vac.usp_GenerarAlertasStock @AlertasCreadas = @Cre OUTPUT;
    SET @Err = CONCAT((SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock
                       WHERE s.IdLote = @IdLoteVenc AND a.Estado = 'PENDIENTE' AND a.TipoAlerta = 'LOTE_POR_VENCER'), '/', @Cre);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_GenerarAlertasStock', 'RN-13: alerta de lote por vencer, sin duplicar', '1/0', @Err);

/* D7. RN-13: un lote que vence en más de 30 días no genera alerta; uno sin existencias tampoco */
BEGIN TRANSACTION;
BEGIN TRY
    DECLARE @IdLoteLejos INT, @IdLoteVacio INT;
    INSERT vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
    SELECT IdVacuna, 'PRB-VENCE-60', 'Prueba', DATEADD(DAY, 60, @Hoy) FROM vac.Vacuna WHERE Codigo = 'SPR';
    SET @IdLoteLejos = SCOPE_IDENTITY();
    INSERT vac.LoteVacuna (IdVacuna, NumeroLote, Laboratorio, FechaVencimiento)
    SELECT IdVacuna, 'PRB-VACIO-10', 'Prueba', DATEADD(DAY, 10, @Hoy) FROM vac.Vacuna WHERE Codigo = 'SPR';
    SET @IdLoteVacio = SCOPE_IDENTITY();
    INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo) VALUES (@IdLoteLejos, @Est, 40, 5), (@IdLoteVacio, @Est, 0, 0);
    EXEC vac.usp_GenerarAlertasStock @AlertasCreadas = @Cre OUTPUT;
    SET @Err = CAST((SELECT COUNT(*) FROM vac.Alerta a JOIN vac.StockLote s ON s.IdStock = a.IdStock
                     WHERE s.IdLote IN (@IdLoteLejos, @IdLoteVacio) AND a.TipoAlerta = 'LOTE_POR_VENCER') AS VARCHAR);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_GenerarAlertasStock', 'RN-13: no alerta lotes lejanos ni sin existencias', '0', @Err);

/* D8. Las alertas de stock no se mezclan con las de pacientes en el resumen general */
BEGIN TRANSACTION;
BEGIN TRY
    SET @n = (SELECT AlertasPendientes FROM vac.vw_ResumenGeneral);
    UPDATE s SET Cantidad = 1, UmbralMinimo = 10 FROM vac.StockLote s JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE l.NumeroLote = @LoteSPR AND s.IdEstablecimiento = @Est;
    SET @Err = IIF((SELECT AlertasPendientes FROM vac.vw_ResumenGeneral) = @n, 'OK', 'cambió');
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('vw_ResumenGeneral', 'Las alertas de stock no cuentan como alertas de pacientes', 'OK', @Err);

/* =================== Gestión de stock: ingreso de lotes y ajustes (RF-09) =================== */

/* G1. El ingreso de un lote nuevo crea el lote, la existencia, el umbral y el movimiento ENTRADA */
BEGIN TRANSACTION;
BEGIN TRY
    DECLARE @IdSt INT;
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Laboratorio de prueba', '2099-12-31', 100, 20, @IdSt OUTPUT;
    SET @Err = CONCAT((SELECT Cantidad FROM vac.StockLote WHERE IdStock = @IdSt), '/',
                      (SELECT UmbralMinimo FROM vac.StockLote WHERE IdStock = @IdSt), '/',
                      (SELECT COUNT(*) FROM vac.MovimientoStock WHERE IdStock = @IdSt AND Tipo = 'ENTRADA' AND Cantidad = 100));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Crea lote, existencia, umbral y movimiento', '100/20/1', @Err);

/* G2. Un segundo ingreso del mismo lote suma a la existencia */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Laboratorio de prueba', '2099-12-31', 100, 20, @IdSt OUTPUT;
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Laboratorio de prueba', '2099-12-31', 50, NULL, @IdSt OUTPUT;
    SET @Err = CONCAT((SELECT Cantidad FROM vac.StockLote WHERE IdStock = @IdSt), '/',
                      (SELECT UmbralMinimo FROM vac.StockLote WHERE IdStock = @IdSt), '/',
                      (SELECT COUNT(*) FROM vac.MovimientoStock WHERE IdStock = @IdSt AND Tipo = 'ENTRADA'));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Un segundo ingreso suma y conserva el umbral', '150/20/2', @Err);

/* G3. Cantidad cero o negativa */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-2', 'Lab', '2099-12-31', 0, NULL, @IdSt OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Rechaza cantidad cero', '50108', @Err);

/* G4. Vacuna inexistente */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'XXX', 'PRB-NUEVO-2', 'Lab', '2099-12-31', 10, NULL, @IdSt OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Rechaza vacuna inexistente', '50109', @Err);

/* G5. El mismo número de lote con otro vencimiento */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2099-12-31', 10, NULL, @IdSt OUTPUT;
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2098-12-31', 10, NULL, @IdSt OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Rechaza el mismo lote con otro vencimiento', '50110', @Err);

/* G6. No se ingresa un lote ya vencido */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-3', 'Lab', '2020-01-01', 10, NULL, @IdSt OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Rechaza un lote vencido', '50111', @Err);

/* G7. Umbral negativo */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-2', 'Lab', '2099-12-31', 10, -1, @IdSt OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_IngresarLote', 'Rechaza umbral negativo', '50112', @Err);

/* G8. Un ajuste fija la cantidad contada y deja el movimiento AJUSTE con la diferencia */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2099-12-31', 100, 20, @IdSt OUTPUT;
    EXEC vac.usp_AjustarStock @IdSt, 90, 'Conteo físico';
    SET @Err = CONCAT((SELECT Cantidad FROM vac.StockLote WHERE IdStock = @IdSt), '/',
                      (SELECT Cantidad FROM vac.MovimientoStock WHERE IdStock = @IdSt AND Tipo = 'AJUSTE'));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_AjustarStock', 'Fija la cantidad y registra la diferencia', '90/-10', @Err);

/* G9. Ajuste a una cantidad negativa */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2099-12-31', 100, 20, @IdSt OUTPUT;
    EXEC vac.usp_AjustarStock @IdSt, -5, 'Conteo físico';
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_AjustarStock', 'Rechaza una cantidad negativa', '50113', @Err);

/* G10. Ajuste sin motivo */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2099-12-31', 100, 20, @IdSt OUTPUT;
    EXEC vac.usp_AjustarStock @IdSt, 90, '   ';
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_AjustarStock', 'Exige el motivo', '50114', @Err);

/* G11. Ajuste de una existencia inexistente */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_AjustarStock -1, 10, 'Conteo físico';
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_AjustarStock', 'Rechaza una existencia inexistente', '50115', @Err);

/* G12. Ajuste a la misma cantidad */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_IngresarLote @Est, 'SPR', 'PRB-NUEVO-1', 'Lab', '2099-12-31', 100, 20, @IdSt OUTPUT;
    EXEC vac.usp_AjustarStock @IdSt, 100, 'Conteo físico';
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_AjustarStock', 'Rechaza un ajuste sin diferencia', '50116', @Err);

/* =================== Agenda: franjas y citas (RF-05, RF-08; RN-14, RN-15, RN-17) =================== */
DECLARE @IdHor INT, @IdHor2 INT, @IdCita INT, @IdPacB INT, @IdSPR2 SMALLINT, @IdUsuCiud INT, @IdUsuCiud2 INT;
DECLARE @Franja DATETIME2(0) = DATEADD(HOUR, 9, CAST(DATEADD(DAY, 3, @Hoy) AS DATETIME2(0)));
DECLARE @Franja2 DATETIME2(0) = DATEADD(HOUR, 1, @Franja);
SELECT @IdSPR2 = e.IdEsquema FROM vac.EsquemaDosis e JOIN vac.Vacuna v ON v.IdVacuna = e.IdVacuna WHERE v.Codigo = 'SPR' AND e.NumeroDosis = 2;
SELECT @IdUsuCiud = IdUsuario FROM vac.Usuario WHERE NombreUsuario = 'ciud01';

/* H1. Crear una franja */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
    SET @Err = CONCAT((SELECT COUNT(*) FROM vac.HorarioAtencion WHERE IdHorario = @IdHor), '/',
                      (SELECT CupoMaximo FROM vac.HorarioAtencion WHERE IdHorario = @IdHor));
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearHorario', 'Crea una franja con su cupo', '1/5', @Err);

/* H2. Franja duplicada (mismo establecimiento, vacuna y hora) */
BEGIN TRANSACTION;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 3, @IdHor2 OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearHorario', 'Rechaza una franja duplicada', '50130', @Err);

/* H3. Franja en el pasado */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearHorario @Est, 'SPR', '2020-01-01 09:00', 5, @IdHor OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearHorario', 'Rechaza una franja en el pasado', '50131', @Err);

/* H4. Cupo fuera de rango */
BEGIN TRANSACTION;
BEGIN TRY
    EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 0, @IdHor OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_CrearHorario', 'Rechaza un cupo menor que 1', '50132', @Err);

/* H5. Reserva válida: queda PROGRAMADA */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Cita', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = (SELECT Estado FROM vac.Cita WHERE IdCita = @IdCita);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Reserva una dosis elegible', 'PROGRAMADA', @Err);

/* H6. RN-14: sin cupo en la franja */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050002', 'Prueba', 'Dos', NULL, @Nac20m, 'F', '230104', NULL, NULL, @IdPacB OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 1, @IdHor OUTPUT;
EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPacB, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'RN-14: rechaza la reserva sobre el cupo', '50126', @Err);

/* H7. RN-15: dos citas activas para la misma dosis */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja2, 5, @IdHor2 OUTPUT;
EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor2, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'RN-15: rechaza una segunda cita activa para la dosis', '50125', @Err);

/* H8. RN-15: la dosis ya fue aplicada */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarDosis '89050001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'RN-15: rechaza una dosis ya aplicada', '50124', @Err);

/* H9. La dosis es de otra vacuna que la franja */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'BCG', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza una dosis de otra vacuna', '50122', @Err);

/* H10. 2.ª dosis sin la 1.ª */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR2, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza la 2.a dosis sin la 1.a', '50124', @Err);

/* H11. Paciente menor de la edad mínima */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Bebe', NULL, @Hoy, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza al paciente menor de la edad mínima', '50124', @Err);

/* H12. Franja inactiva */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
EXEC vac.usp_ActualizarHorario @IdHor, 5, 0;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza una franja inactiva', '50121', @Err);

/* H13. Franja inexistente */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, -1, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza una franja inexistente', '50120', @Err);

/* H14. Paciente inexistente */
BEGIN TRANSACTION;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita -1, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Rechaza un paciente inexistente', '50123', @Err);

/* H15. RN-17: el ciudadano no puede reservar para un paciente no vinculado */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, @IdUsuCiud, @IdCita OUTPUT;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'RN-17: rechaza a un ciudadano sin vínculo', '50127', @Err);

/* H16. RN-17: el ciudadano reserva para un paciente vinculado */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco) VALUES (@IdUsuCiud, @IdPac, 'MADRE');
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, @IdUsuCiud, @IdCita OUTPUT;
    SET @Err = (SELECT Estado FROM vac.Cita WHERE IdCita = @IdCita);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'RN-17: acepta a un ciudadano vinculado', 'PROGRAMADA', @Err);

/* H17. Una cita cancelada libera el cupo */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050002', 'Prueba', 'Dos', NULL, @Nac20m, 'F', '230104', NULL, NULL, @IdPacB OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 1, @IdHor OUTPUT;
EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
UPDATE vac.Cita SET Estado = 'CANCELADA' WHERE IdCita = @IdCita;
BEGIN TRY
    EXEC vac.usp_ReservarCita @IdPacB, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
    SET @Err = (SELECT Estado FROM vac.Cita WHERE IdCita = @IdCita);
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ReservarCita', 'Una cita cancelada libera el cupo', 'PROGRAMADA', @Err);

/* H18. RN-14: el trigger revalida el cupo aunque se inserte sin el procedimiento */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050002', 'Prueba', 'Dos', NULL, @Nac20m, 'F', '230104', NULL, NULL, @IdPacB OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 1, @IdHor OUTPUT;
INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema) VALUES (@IdHor, @IdPac, @IdSPR1);
BEGIN TRY
    INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema) VALUES (@IdHor, @IdPacB, @IdSPR1);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_Cita_Validar', 'RN-14: revalida el cupo en inserción directa', '50126', @Err);

/* H19. RN-15: el trigger rechaza la dosis ya aplicada en inserción directa */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarDosis '89050001', 'SPR', 1, @LoteSPR, @Est, @Dni, NULL, NULL, @IdDosis OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 5, @IdHor OUTPUT;
BEGIN TRY
    INSERT vac.Cita (IdHorario, IdPaciente, IdEsquema) VALUES (@IdHor, @IdPac, @IdSPR1);
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('trg_Cita_Validar', 'RN-15: rechaza la dosis aplicada en inserción directa', '50124', @Err);

/* H20. No se baja el cupo de una franja por debajo de las citas que ya tiene */
BEGIN TRANSACTION;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050001', 'Prueba', 'Uno', NULL, @Nac20m, 'M', '230104', NULL, NULL, @IdPac OUTPUT;
EXEC vac.usp_RegistrarPaciente 'DNI', '89050002', 'Prueba', 'Dos', NULL, @Nac20m, 'F', '230104', NULL, NULL, @IdPacB OUTPUT;
EXEC vac.usp_CrearHorario @Est, 'SPR', @Franja, 3, @IdHor OUTPUT;
EXEC vac.usp_ReservarCita @IdPac, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
EXEC vac.usp_ReservarCita @IdPacB, @IdHor, @IdSPR1, NULL, @IdCita OUTPUT;
BEGIN TRY
    EXEC vac.usp_ActualizarHorario @IdHor, 1, 1;
    SET @Err = 'sin error';
END TRY BEGIN CATCH SET @Err = CAST(ERROR_NUMBER() AS VARCHAR); END CATCH;
IF @@TRANCOUNT > 0 ROLLBACK;
INSERT @R VALUES ('usp_ActualizarHorario', 'Rechaza un cupo menor que las citas existentes', '50128', @Err);

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
