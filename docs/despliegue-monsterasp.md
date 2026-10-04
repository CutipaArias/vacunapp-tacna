# Despliegue en MonsterASP.NET (plan gratuito)

Esta guía lleva VacunApp Tacna de su PC al hosting. **Todo lo que depende de sus credenciales lo ejecuta el equipo**: aquí no se
guarda ninguna clave y ningún comando de esta guía se ha ejecutado contra MonsterASP.NET.

| Qué | Dónde se comprobó |
|---|---|
| Paquete sin configuración local ni claves, y arranque en `Production` con HTTPS, `admin` creado desde `Seed__AdminPassword` y login | En local, con `tests/publicacion.ps1` (51 casos) |
| Carga de la base en modo producción (`-Produccion`) | En local, con `tests/produccion.ps1` (32 casos) |
| Variables de entorno, ZIP, HTTPS, acceso remoto a la base | Según la ayuda de MonsterASP.NET (enlaces al final). **No comprobado por nosotros**: confírmelo en el panel |

## 1. Antes de empezar

En su PC: [.NET 10 SDK](https://dotnet.microsoft.com/download), PowerShell y `sqlcmd` (herramientas de línea de comandos de SQL Server).
Clone el repositorio; no hace falta Docker.

En el panel de MonsterASP.NET anote para el sitio: nombre del sitio y runtime (.NET 10). Para la base: **host público**
(`dbNNNNN.public.databaseasp.net`), **host interno** (el mismo sin `public`), nombre de la base, usuario y contraseña.

## 2. Cargar la base (una sola vez, desde su PC)

1. En el panel, abra la configuración de la base de datos y active el **acceso remoto** (viene desactivado por defecto).
2. Con la base **vacía**, ejecute:

   ```powershell
   ./scripts/desplegar-remoto.ps1 -Produccion
   ```

   Pide servidor (host **público**), base, usuario y contraseña. La contraseña se lee como `SecureString`, viaja solo en la variable de
   entorno `SQLCMDPASSWORD` durante la ejecución y se borra al terminar.
3. Resultado esperado, en este orden: intercalación de la base (el proyecto usa `Modern_Spanish_CI_AS`; si el hosting asigna otra,
   revise que las búsquedas por nombre sigan ignorando mayúsculas y tildes), los diez scripts (`01` a `05`, `08` a `10`, `12` y
   `11`), el tamaño final y la línea `Base '…' lista en … (producción)`. En local la carga dura unos 2 s y deja 8,0 MB de datos y 3,9 MB de
   registro. El mensaje `Cannot shrink log file` de `DBCC SHRINKFILE` es inofensivo.
4. Qué carga: esquema (23 tablas, 24 procedimientos, 9 triggers, 8 vistas, 4 funciones), catálogos (4 provincias, 28 distritos, 14
   enfermedades, 10 vacunas, 19 dosis del esquema, 34 establecimientos, 68 vacunadores, 5 roles), seguridad, stock, agenda y
   estadísticas. **No carga** los 20 000 pacientes simulados ni `07_pruebas.sql`. `12_produccion.sql` retira las cuentas de
   demostración y las campañas simuladas: queda un único usuario, `admin`, **sin clave en la base**.
5. Si la base ya tiene el esquema `[vac]`, el script se detiene sin tocar nada. Si una carga se interrumpe a medias, vacíe la base desde el
   panel del hosting y repita. El script nunca borra datos.
6. Cuando termine, **desactive el acceso remoto** salvo que lo necesite: deja la base expuesta a Internet.

## 3. Generar el paquete

```powershell
./scripts/publicar.ps1 -Zip
```

Deja `publicar\panel` (66 archivos, 8,9 MB) y `publicar\panel.zip` (3,2 MB, archivos en la raíz). Solo escribe en la carpeta de salida; no
se conecta a nada. Antes de terminar revisa el paquete y falla si encuentra `appsettings.Development*.json`,
`appsettings.Production*.json`, plantillas `*.example.json`, `.env`, `.pfx`, `.pdb` o claves en `appsettings.json`/`web.config`. La
configuración local (con la clave de `sa` y las claves semilla) **no viaja en el paquete**.

El paquete depende del marco: el hosting aporta .NET 10 (la página del plan indica que admite .NET 10).

## 4. Configurar la aplicación (variables de entorno)

En el panel: **Websites → Manage website → Scripting → Environment Variables**. Los nombres con `__` equivalen a `:` en la
configuración de ASP.NET Core. La ayuda indica que la aplicación las toma **tras reiniciarse**.

| Variable | Valor | Notas |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Fija cookie `Secure`, HSTS y errores genéricos. |
| `ConnectionStrings__VacunApp` | `Server=dbNNNNN.databaseasp.net;Database=<base>;User Id=<usuario>;Password=<clave>;Encrypt=True;TrustServerCertificate=True` | Host **interno** (sin `public`). Plantilla en `panel/appsettings.Production.example.json`. |
| `Seed__AdminPassword` | clave inicial del administrador | 10 a 128 caracteres con mayúscula, minúscula, número y símbolo. Si no cumple, `admin` queda sin clave y el registro lo advierte. |
| `RateLimit__LoginPerMinute` | `20` (opcional) | Solicitudes de login por minuto y por IP. |

- **Cifrado de la conexión.** La plantilla usa `Encrypt=True;TrustServerCertificate=True` porque son datos de salud y funciona con el
  certificado del servidor. La ayuda de MonsterASP.NET dice que dentro del hosting el cifrado no es necesario y que puede afectar el
  rendimiento. Si la conexión falla o es lenta, pruebe `Encrypt=False`. Decisión a validar en el hosting.
- **Clave del administrador.** Solo se usa mientras `admin` no tenga clave en la base: al primer arranque se guarda su hash (PBKDF2) y
  desde entonces la variable no se vuelve a leer. Una vez que haya iniciado sesión, **elimine `Seed__AdminPassword`** del panel y reinicie.
- **Alternativa sin variables:** copie `panel/appsettings.Production.example.json` como `appsettings.Production.json` **en el servidor**
  (administrador de archivos) y complete los valores. Nunca dentro del ZIP ni del repositorio (`.gitignore` ya lo excluye).

## 5. Subir los archivos

Según la ayuda de MonsterASP.NET (despliegue por ZIP):

1. En el panel, abra el sitio → icono **Files** (administrador de archivos).
2. **Upload file** → seleccione `publicar\panel.zip`.
3. **Unzip** → destino **`/wwwroot`** (es la raíz pública del sitio; la carpeta `wwwroot` de la aplicación quedará dentro de ella).
4. Primera vez: nada más. **Actualizaciones:** marque *Overwrite files in target path* y *Restart application pool before unzip*, para
   reemplazar archivos en uso.
5. Reinicie la aplicación.

Alternativas documentadas por el hosting: Web Deploy con el perfil `.publishSettings`, o FTP/SFTP. El camino que preparamos y
comprobamos es el ZIP.

## 6. HTTPS (obligatorio)

La cookie de sesión se emite siempre con `Secure`. Por **http://** el navegador no la guarda: el inicio de sesión parece responder bien
y vuelve a pedir credenciales.

1. La ayuda indica que HTTPS **no está activo por defecto**: actívelo en el panel (Let's Encrypt). En el plan gratuito la ayuda dice que
   la renovación es manual, cada 90 días.
2. Active la redirección de HTTP a HTTPS **solo después** de comprobar que la aplicación funciona por HTTPS: la aplicación no
   redirige por sí misma.
3. **No comprobado:** la ayuda describe la activación para dominios propios. Confirme en el panel que el subdominio gratuito
   (`*.runasp.net`) la ofrece o ya la trae activa.

## 7. Primer arranque y comprobaciones

1. `https://<su-sitio>/login.html` carga la pantalla de acceso.
2. Entre como `admin` con la clave de `Seed__AdminPassword`. Deben verse las pestañas del administrador (Resumen, Tablero, Cobertura,
   Alertas, Pendientes, Brotes, Campañas, Paciente, Usuarios, Auditoría). Con la base sin pacientes el Resumen y el Tablero salen
   vacíos; es lo esperado.
3. En **Usuarios** cree una cuenta por rol (epidemiólogo, jefe con su establecimiento, vacunador con establecimiento y personal
   registrado, ciudadano) con claves que cumplan la política, y entréguelas por un canal seguro.
4. Elimine `Seed__AdminPassword` del panel, reinicie e inicie sesión de nuevo: debe seguir funcionando.
5. En las herramientas del navegador, la cookie `.VacunApp.Auth` debe ser `Secure`, `HttpOnly` y `SameSite=Strict`.
6. `curl -sI https://<su-sitio>/login.html` debe traer `Content-Security-Policy`, `X-Content-Type-Options: nosniff` y
   `Strict-Transport-Security`. HSTS no sale en `localhost` (ASP.NET lo omite); solo se puede ver en el sitio público.
7. Prueba de carga y concurrencia en producción (T6.3): pendiente, ver `tasks/todo.md`.

## 8. Actualizar a una versión nueva

1. `./scripts/publicar.ps1 -Zip` (limpia `publicar\panel` antes de publicar).
2. Subir y descomprimir con las dos casillas de actualización (paso 5).
3. Cambios en la base: v2.0 no tiene herramienta de migraciones. `desplegar-remoto.ps1` solo carga sobre una base vacía, así que un
   cambio de esquema se aplica con un script SQL específico, revisado antes.

## 9. Límites del plan gratuito y cifras medidas

| Límite | Medido | Fuente |
|---|---|---|
| 256 MB de RAM | 91,5 MB de memoria de trabajo tras recorrer todos los GET con la base de producción vacía; 145 MB de pico con 20 000 pacientes y 10 usuarios simultáneos | `tests/publicacion.ps1`; `docs/capacidad.md` |
| 1 GB de base | 12 MB tras `-Produccion`; 272 MB con 20 000 pacientes y 309 393 dosis simuladas | `docs/capacidad.md` |
| 5 GB de sitio | 8,9 MB descomprimidos | `scripts/publicar.ps1` |

Son mediciones **locales**. Los 256 MB son del proceso de la aplicación y SQL Server corre aparte; ambos supuestos están por confirmar
en el panel. El limitador de intentos de login vive en memoria: vale para una sola instancia, que es este despliegue. Cuenta por la IP
que ve la aplicación: si el hosting pusiera un proxy delante, todos los usuarios compartirían IP y el límite de 20 por minuto sería
global (se ajusta con `RateLimit__LoginPerMinute`; sin reenvío de la IP real no se distingue a los clientes, ver `docs/seguridad.md`).

## 10. Si algo falla

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| Error 500.30 / 500.31 al abrir el sitio | Falta `ConnectionStrings__VacunApp` o no conecta | Revisar la variable (host interno). Para ver el mensaje de arranque, en el `web.config` del servidor ponga `stdoutLogEnabled="true"` (escribe en `.\logs\stdout`; cree la carpeta `logs` si no existe) y vuelva a ponerlo en `false` después: el registro no debe quedar activo. |
| Error 500.19 o la aplicación no inicia | Runtime ausente o `web.config` dañado | Confirmar .NET 10 en el sitio y volver a subir el ZIP. |
| Login con 401 y la clave correcta | `admin` sin clave: variable ausente o clave que no cumple la política | Definir `Seed__AdminPassword` válida y reiniciar. |
| Login correcto pero vuelve al formulario | Se abrió por `http://` | Usar `https://` (sección 6). |
| «Usuario o contraseña incorrectos» con la clave correcta | Cuenta bloqueada: 5 fallos seguidos, 15 minutos | Esperar. |
| `desplegar-remoto.ps1` no conecta | Acceso remoto desactivado, o host interno en lugar del público | Activar el acceso remoto; usar el host con `public`. |
| `desplegar-remoto.ps1` dice que ya existe el esquema | Carga previa, completa o a medias | Vaciar la base desde el panel y repetir. |

## 11. Límites conocidos de la v2.0 que afectan a la operación

- **No hay cambio ni restablecimiento de contraseña** (ni propio ni por el administrador). Si alguien la olvida, el administrador puede
  dar de baja la cuenta y crear otra con otro nombre de usuario. Si olvida su clave el propio `admin`, desde un cliente SQL:

  ```sql
  UPDATE vac.Usuario SET ClaveHash = NULL, IntentosFallidos = 0, BloqueadoHasta = NULL WHERE NombreUsuario = 'admin';
  ```

  Defina `Seed__AdminPassword` con la clave nueva y reinicie; al arrancar se le asigna.
- **Las cuentas de ciudadano no se vinculan a pacientes desde la aplicación.** Un ciudadano solo ve y reserva para los pacientes de su
  vínculo (`vac.VinculoFamiliar`) y no existe pantalla ni API que lo cree. El administrador de la base lo hace con SQL, una fila por
  hijo:

  ```sql
  INSERT vac.VinculoFamiliar (IdUsuario, IdPaciente, Parentesco)
  SELECT u.IdUsuario, p.IdPaciente, 'MADRE'   -- MADRE, PADRE, TUTOR u OTRO
  FROM vac.Usuario u, vac.Paciente p
  WHERE u.NombreUsuario = '<usuario>' AND p.NumeroDocumento = '<DNI del niño>';
  ```

- Un distrito sin pacientes registrados no aparece en el Tablero ni en la cobertura: sin elegibles no hay denominador.

## Fuentes

- Despliegue con Visual Studio / Web Deploy: <https://help.monsterasp.net/books/deploy/page/how-to-deploy-net-core-web-application-using-visual-studio>
- Despliegue desde un ZIP: <https://help.monsterasp.net/books/deploy/page/how-to-deploy-website-content-from-zip-file>
- Despliegue por FTP/SFTP: <https://help.monsterasp.net/books/deploy/page/how-to-deploy-website-content-via-ftpsftp>
- Variables de entorno: <https://help.monsterasp.net/books/development/page/environment-variables-as-configuration-store>
- HTTPS con Let's Encrypt: <https://help.monsterasp.net/books/https/page/how-to-activate-https-with-lets-encrypt-certificate>
- Acceso remoto a la base: <https://help.monsterasp.net/books/databases/page/remote-access-for-database>
- Conexión segura a MSSQL: <https://help.monsterasp.net/books/databases/page/secure-connection-ssltls-to-mssql>
