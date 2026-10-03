# Seguridad del módulo identidad (T1.6)

Revisión hecha el 03/10/2026 con la lista de /security-and-hardening. Cada control tiene una prueba automática
(`tests/VacunApp.Tests`) o una verificación indicada.

## Modelo de amenazas (resumen)

| Frontera de confianza | Activo | Amenaza principal | Control |
|---|---|---|---|
| Navegador → `/api/login` | Cuentas | Fuerza bruta / relleno de credenciales | Bloqueo de 15 min tras 5 fallos seguidos por cuenta; límite de 20 solicitudes/min por IP (429); mensaje único para cualquier fallo |
| Navegador → `/api/*` | Datos de pacientes | Acceso sin sesión o fuera de rol/establecimiento | Autorización negada por defecto, políticas por rol, `Alcance` por establecimiento (RN-22), 401/403 |
| Cookie de sesión | Sesión | Robo por XSS o CSRF | `HttpOnly`, `SameSite=Strict`, `Secure` fuera de Development, vigencia de 8 h sin renovación |
| API → SQL Server | Base de datos | Inyección SQL | Solo `SqlParameter` tipados y procedimientos almacenados; ninguna consulta se arma concatenando entrada |
| Base de datos | Contraseñas | Filtración de la tabla | Solo hash PBKDF2 de `PasswordHasher`; `CHECK` impide guardar texto corto como si fuera hash |
| Repositorio | Secretos | Credenciales publicadas | Sin contraseñas en código ni en el repo (`.env`, `appsettings.Development.json` y claves semilla están en `.gitignore`) |

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
- **Front-end:** ningún `<script>` ni manejador `on…=` en línea; todo texto de la base pasa por `esc()` antes de entrar en `innerHTML`
  (revisión manual de las 22 asignaciones) o se escribe con `textContent`.
- **Errores:** fuera de Development un error inesperado devuelve un mensaje genérico, sin traza; los errores de negocio
  (`THROW 50000+`) sí muestran su mensaje en español.
- **Auditoría de dependencias:** `dotnet list package --vulnerable --include-transitive` sin hallazgos (panel y pruebas).
- **Último administrador:** `usp_ActualizarUsuario` impide desactivar o degradar al único administrador activo.

## Cómo se verifica

```powershell
./desplegar.ps1 -Pruebas            # 41 casos SQL
dotnet test tests/VacunApp.Tests    # 73 pruebas (login, autorización, usuarios, seguridad)
dotnet list panel package --vulnerable --include-transitive
```

## Pendiente y riesgos aceptados

| Tema | Estado |
|---|---|
| Contraseña de desarrollo de SQL Server (`sa`) | Ya no está en el código; el README publicado en `main` la sigue mostrando (decisión del equipo) |
| Límite por IP con varias instancias o proxy | Documentado arriba; revisar al desplegar (T6.2) |
| CSRF | Mitigado con `SameSite=Strict` y API solo JSON; se añadiría token antifalsificación si se sirviera la API a otros orígenes |
| Autoservicio de cambio de contraseña / recuperación | Fuera del alcance de v2.0 (el administrador crea las cuentas) |
| `usp_ListarPendientes` tarda 15–40 s | Es un tema de rendimiento (RNF-01), no de seguridad; tarea aparte creada |
| Chart.js por CDN (T5.3) | Cuando se añada habrá que permitir su origen en `script-src` y fijar SRI |
