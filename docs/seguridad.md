# Seguridad de VacunApp Tacna v2.0

Revisión inicial del módulo identidad (T1.6, 03/10/2026) con la lista de /security-and-hardening, ampliada en las fases de producción (T6.1 y T6.2).
Cada control tiene una prueba automática (`tests/VacunApp.Tests`) o una verificación indicada.

## Modelo de amenazas (resumen)

| Frontera de confianza | Activo | Amenaza principal | Control |
|---|---|---|---|
| Navegador → `/api/login` | Cuentas | Fuerza bruta / relleno de credenciales | Bloqueo de 15 min tras 5 fallos seguidos por cuenta; límite de 20 solicitudes/min por IP (429); mensaje único para cualquier fallo |
| Navegador → `/api/*` | Datos de pacientes | Acceso sin sesión o fuera de rol/establecimiento | Autorización negada por defecto, políticas por rol, `Alcance` por establecimiento (RN-22), 401/403 |
| Cookie de sesión | Sesión | Robo por XSS o CSRF | `HttpOnly`, `SameSite=Strict`, `Secure` fuera de Development, vigencia de 8 h sin renovación |
| API → SQL Server | Base de datos | Inyección SQL | Solo `SqlParameter` tipados y procedimientos almacenados; ninguna consulta se arma concatenando entrada |
| Base de datos | Contraseñas | Filtración de la tabla | Solo hash PBKDF2 de `PasswordHasher`; `CHECK` impide guardar texto corto como si fuera hash |
| Repositorio | Secretos | Credenciales publicadas | Sin contraseñas en código ni en el repo (`.env`, `appsettings.Development.json`, `*.pfx` y el paquete `publicar/` están en `.gitignore`) |

## Controles implementados

- **Contraseñas:** `PasswordHasher<T>` (PBKDF2, formato v3 con sal incluida). Política: 10–128 caracteres con mayúscula, minúscula, número y símbolo.
  El tope de 128 evita que alguien envíe claves enormes para consumir CPU en el hash.
- **Mensajes de login:** inexistente, inactivo, sin clave, clave incorrecta y cuenta bloqueada responden **exactamente igual**
  y se verifica un hash falso cuando el usuario no existe, para no revelar qué cuentas existen ni por el tiempo de respuesta.
- **Bloqueo por cuenta (adición al spec):** 5 fallos seguidos → 15 minutos (`MaxIntentos`, `MinutosBloqueo` en `AuthEndpoints`).
  El contador se actualiza con una sola sentencia atómica; mientras la cuenta está bloqueada no se alarga el bloqueo
  (así un atacante no puede dejar fuera a un usuario indefinidamente). Un acceso correcto reinicia el contador.
- **Límite por IP:** `RateLimit:LoginPerMinute` (20 por defecto). Contador en memoria: vale para **una sola instancia**,
  que es el despliegue previsto en MonsterASP.NET. Detrás de un proxy hay que configurar el reenvío de la IP real.
- **Sesión revalidada en cada solicitud:** una baja, un cambio de rol o de establecimiento surte efecto de inmediato.
- **Cabeceras:** `Content-Security-Policy` (`script-src 'self'`, sin `unsafe-inline` ni `unsafe-eval`; `unsafe-inline` solo en estilos
  por las barras de cobertura), `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Permissions-Policy`, `Cache-Control: no-store` en `/api/*` y HSTS fuera de Development.
- **Front-end:** ningún `<script>` ni manejador `on…=` en línea; todo texto de la base o escrito por un usuario (número de lote, nombre de campaña,
  nombres) pasa por `esc()` antes de entrar en `innerHTML` o se escribe con `textContent`. Revisión manual del 03/10/2026 de las 73 líneas con
  `innerHTML` de `panel.js` y `dashboard.js`: toda interpolación sin `esc()` es un número, una constante, un destino de `textContent` o un
  `confirm()`. Es una revisión a ojo, no una prueba automática; las gráficas se dibujan en un `canvas`, sin HTML.
- **Errores:** fuera de Development un error inesperado devuelve un mensaje genérico, sin traza; los errores de negocio
  (`THROW 50000+`) sí muestran su mensaje en español.
- **Auditoría de dependencias:** `dotnet list package --vulnerable --include-transitive` sin hallazgos (panel y pruebas).
- **Último administrador:** `usp_ActualizarUsuario` impide desactivar o degradar al único administrador activo.
- **Producción (T6.1 y T6.2):** `scripts/publicar.ps1` arma un paquete sin `appsettings.Development*.json`, `appsettings.Production*.json`,
  plantillas `*.example.json`, `.env`, `.pfx` ni símbolos `.pdb`, y se detiene si encuentra contraseñas en los `.json` o `.config`;
  `tests/publicacion.ps1` repite esa comprobación y busca los valores reales de las claves locales dentro del paquete. La base de producción
  (`-Produccion`) no lleva cuentas de demostración: solo `admin`, sin clave, que la aplicación asigna al arrancar desde `Seed:AdminPassword`
  (variable de entorno del hosting). La cookie sale `Secure` y se verificó con HTTPS local en modo `Production`.
- **Revisión de secretos antes de cada commit:** búsqueda de los valores reales de las claves locales en el diff y en el historial del repositorio
  (`git log -S`); sin hallazgos.

## Cómo se verifica

```powershell
./desplegar.ps1 -Pruebas            # 144 casos SQL
dotnet test tests/VacunApp.Tests    # 337 pruebas de la API (login, autorización, usuarios, seguridad y los demás módulos)
./tests/produccion.ps1              # base de producción: sin datos de prueba y solo el administrador
./tests/publicacion.ps1             # paquete sin secretos y arranque en modo Production con HTTPS
dotnet list panel package --vulnerable --include-transitive
```

## Pendiente y riesgos aceptados

| Tema | Estado |
|---|---|
| Contraseña de desarrollo de SQL Server (`sa`) | No está en el código ni en el historial de este repositorio; vive en `.env`, que no se versiona |
| Límite por IP con varias instancias o proxy | Contador en memoria: válido para una instancia. Detrás de un proxy sin reenvío de la IP real se cuenta por la IP del proxy; revisar si el hosting usa uno |
| CSRF | Mitigado con `SameSite=Strict` y API solo JSON; se añadiría token antifalsificación si se sirviera la API a otros orígenes |
| Autoservicio de cambio de contraseña / recuperación | Fuera del alcance de v2.0: el administrador crea las cuentas y no hay forma de cambiarlas desde la aplicación. Procedimiento manual en `docs/despliegue-monsterasp.md` §11 |
| Cadena de conexión con `TrustServerCertificate=True` | Cifra el canal, pero no valida la identidad del servidor. Aceptado para el hosting compartido; validar si el hosting ofrece un certificado verificable |
| `Seed__AdminPassword` en el hosting | Solo se usa mientras `admin` no tiene clave; tras el primer ingreso conviene quitarla de las variables de entorno |
| HTTPS en el hosting | Sin comprobar en el hosting real: la ayuda de MonsterASP.NET indica que no viene activo. Sin HTTPS la cookie `Secure` no se envía y no se puede iniciar sesión |
| Rendimiento de `usp_ListarPendientes` | Resuelto (tabla temporal y `11_estadisticas.sql`); ver `docs/capacidad.md` |
| Chart.js | Se sirve desde `panel/wwwroot/vendor/` (versión 4.5.1, MIT, integridad verificada contra npm); la política CSP se mantiene en `script-src 'self'` |
