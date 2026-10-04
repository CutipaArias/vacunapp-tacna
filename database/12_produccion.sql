/* =====================================================================
   VacunApp Tacna - 12. Ajustes de produccion
   Solo para scripts/desplegar-remoto.ps1 -Produccion. Esa carga NO ejecuta
   06_datos_prueba.sql ni 07_pruebas.sql, pero 02_catalogos.sql y
   08_seguridad.sql traen material de demostracion que no debe quedar en una
   base real; este script lo retira y deja:

     - los catalogos (provincias, distritos, vacunas, esquema, establecimientos
       y vacunadores);
     - un unico usuario, "admin", rol ADMINISTRADOR y SIN clave: la aplicacion
       se la asigna al arrancar a partir de la configuracion Seed:AdminPassword
       (variable de entorno o appsettings.Production.json, nunca el repositorio).
       Desde el panel, ese administrador crea las demas cuentas.

   Es idempotente. Se niega a correr sobre una base que ya tiene pacientes o
   dosis: ahi se borrarian cuentas de verdad.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM vac.Paciente) OR EXISTS (SELECT 1 FROM vac.DosisAplicada)
    THROW 50900, 'Esta base ya tiene pacientes o dosis: 12_produccion.sql solo se ejecuta sobre una carga nueva sin datos.', 1;

BEGIN TRANSACTION;

/* Cuentas de demostracion creadas por 08_seguridad.sql (sin clave y sin datos asociados). */
DELETE v FROM vac.VinculoFamiliar v JOIN vac.Usuario u ON u.IdUsuario = v.IdUsuario
WHERE u.NombreUsuario IN ('epi01', 'jefe01', 'vac01', 'ciud01');
DELETE FROM vac.Usuario WHERE NombreUsuario IN ('epi01', 'jefe01', 'vac01', 'ciud01');

/* El administrador debe existir; si 08_seguridad.sql no lo creo, se crea aqui (sin clave). */
IF NOT EXISTS (SELECT 1 FROM vac.Usuario WHERE NombreUsuario = 'admin')
    INSERT vac.Usuario (NombreUsuario, NombreCompleto, IdRol)
    SELECT 'admin', 'Administrador del sistema', IdRol FROM vac.Rol WHERE Nombre = 'ADMINISTRADOR';

/* Campañas simuladas de 02_catalogos.sql: sin metas ni dosis solo estorbarian en el tablero. */
DELETE c FROM vac.Campana c
WHERE NOT EXISTS (SELECT 1 FROM vac.CampanaDistrito cd WHERE cd.IdCampana = c.IdCampana)
  AND NOT EXISTS (SELECT 1 FROM vac.DosisAplicada da WHERE da.IdCampana = c.IdCampana);

COMMIT;
GO

SELECT u.NombreUsuario, r.Nombre AS Rol, IIF(u.ClaveHash IS NULL, 'sin clave (la asigna la aplicacion)', 'con clave') AS Clave
FROM vac.Usuario u JOIN vac.Rol r ON r.IdRol = u.IdRol;
GO
