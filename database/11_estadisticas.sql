/* =====================================================================
   VacunApp Tacna - 11. Estadísticas del optimizador
   Se ejecuta al final de la carga masiva (antes de 07_pruebas.sql). Tras
   cargar 20 000 pacientes y 300 000 dosis, SQL Server aún conserva
   estadísticas de las tablas pequeñas tomadas cuando estaban casi vacías
   (Brote: 1 fila frente a 3 reales) y solo las renueva tras 500 cambios.
   Con ellas, una consulta puede pasar de 0,4 s a casi 50 s. Vea
   docs/capacidad.md.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO
EXEC sp_updatestats;
GO
