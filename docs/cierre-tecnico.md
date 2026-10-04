# Cierre técnico – VacunApp Tacna v2.0

Estado al 03/10/2026 (entrega prevista: 23/10/2026). Resume qué se construyó, cómo se comprobó, las decisiones que lo condicionan y
lo que queda abierto. El detalle de cada decisión de diseño está en `SPEC.md` §12; las pruebas, en `docs/manual-pruebas.md`.

## 1. Qué se entrega

Aplicación web y base de datos para la DIRESA Tacna: registro de pacientes y dosis validado en la base, citas con cupos, stock por
establecimiento, brotes, alertas, campañas y tablero de cobertura, con cinco roles. Datos 100 % simulados (Ley 29733).

| Componente | Contenido |
|---|---|
| Base de datos (SQL Server 2022) | 23 tablas, 24 procedimientos, 9 triggers, 8 vistas, 4 funciones, 36 claves foráneas, 31 restricciones CHECK y 14 únicas. Doce scripts numerados e idempotentes (`database/01…12`, unas 4 500 líneas) |
| API (ASP.NET Core Minimal API, .NET 10) | 1 750 líneas de C#; sin ORM, parámetros tipados; autenticación por cookie con políticas por rol y establecimiento |
| Interfaz | HTML, CSS y JavaScript sin framework (1 300 líneas) y Chart.js 4.5.1 servido desde el propio sitio |
| Pruebas | 144 casos SQL (`07_pruebas.sql`), 337 pruebas xUnit, 22 escenarios de concurrencia, 32 de carga de producción y 51 de paquete y arranque |
| Despliegue | `desplegar.ps1` (Docker local), `scripts/desplegar-remoto.ps1` (hosting, modo `-Produccion`), `scripts/publicar.ps1` |
| Documentos | `docs/manual-usuario.md`, `docs/manual-pruebas.md`, `docs/despliegue-monsterasp.md`, `docs/seguridad.md`, `docs/capacidad.md` |

## 2. Cómo está armado

- **Las reglas viven en la base.** Edad, orden e intervalo de dosis, lote vigente, vacunador del establecimiento, stock, cupos de cita, brote
  único por enfermedad y distrito y la auditoría se aplican con triggers, procedimientos y restricciones; la API no puede saltárselas y
  tampoco un `INSERT` directo (hay casos SQL que lo prueban). Los errores de negocio son `THROW 5xxxx` con mensaje en español que la API
  entrega con HTTP 400 y el código.
- **La API es delgada.** Autentica, autoriza por rol y alcance (RN-22) y traduce a JSON. Un recurso ajeno y uno inexistente reciben la misma
  respuesta 403, para que no se pueda sondear qué existe.
- **Concurrencia.** Un bloqueo de aplicación por paciente y dosis y luego `UPDLOCK, HOLDLOCK` sobre las franjas en orden fijo de `IdHorario`.
- **Diagramas** (E-R, casos de uso, paquetes, arquitectura, despliegue, secuencia): `docs/diagramas` y `docs/img`.

## 3. Requisitos funcionales: dónde están y cómo se prueban

**Numeración.** Los números RF y RNF de este documento son los de la v2.0 que usan `SPEC.md` y `tasks/todo.md`. Los archivos FD03 y FD04 de la
carpeta del proyecto siguen con la numeración de la v1 (por ejemplo, allí RNF08 es Seguridad y RF13 es el carné): al actualizarlos hay que alinear
los números con esta tabla. Los nombres de los RNF de la sección 4 se infieren de `SPEC.md` §7 y §9 y de las tareas; conviene contrastarlos con el FD03 v2.0.

| RF | Implementación | Pruebas xUnit | Casos SQL |
|---|---|---|---|
| RF-01 Iniciar sesión | `/api/login`, `/api/logout`, `/api/yo`; `login.html` | AuthTests, SeguridadTests, RegistroAccesoFallidoTests | S1–S2 |
| RF-02 Usuarios y roles | `/api/usuarios` (listar, crear y actualizar nombre, rol, establecimiento y estado activo); pestaña Usuarios | UsuariosTests | S3–S17 |
| RF-03 Registrar paciente | `POST /api/pacientes`; tarjeta «Registrar paciente» | PacientesTests | N1–N3 |
| RF-04 Registrar dosis | `POST /api/dosis` → `usp_RegistrarDosis`, `trg_DosisAplicada_Validar` | DosisTests | N4–N9, N11 |
| RF-05 Reservar cita | `/api/agenda/franjas`, `POST /api/citas`, `GET /api/citas` | CitasTests | H5–H19 |
| RF-06 Reprogramar o cancelar | `/api/citas/{id}/reprogramar`, `/cancelar` | CitasGestionTests | H21–H32 |
| RF-07 Atender o inasistencia | `/api/citas/dia`, `/api/citas/{id}/atender`, `/inasistencia` | AtencionTests | H33–H41 |
| RF-08 Horarios y cupos | `/api/horarios` | HorariosTests | H1–H4, H20 |
| RF-09 Stock | `/api/stock` (consulta, ingresos, ajustes, alertas) | StockTests, GestionStockTests | K1–K4, D1–D8, G1–G12 |
| RF-10 Brotes | `/api/brotes` | BrotesTests, BrotesCierreTacnaTests | N10, P5, P6 |
| RF-11, RF-12 Alertas y pendientes | Generación: triggers de brote y paciente, `usp_GenerarAlertasAtrasadas`, inasistencia, stock. Consulta: `/api/alertas`, `/api/alertas/stock`, `/api/alertas/{id}/atender`, `/api/pendientes` | AlertasTests | P1, P2, P4, P5, P7, P8, D4–D6, H38–H41, H47–H56 |
| RF-13 Tablero de cobertura | `/api/dashboard`, `/api/resumen`, `/api/sarampion`, `/api/cobertura` | DashboardTests, ResumenTests | informe de rendimiento al final de `07_pruebas.sql` |
| RF-14 Campañas | `/api/campanas` (listar, crear, cerrar) | CampanasTests | H57–H68 |
| RF-15 Carné | `/api/paciente/{documento}`, `/api/mis-pacientes` | CarneTests | sin caso propio |
| RF-16 Auditoría | `/api/auditoria`; `trg_DosisAplicada_Auditoria` | AuditoriaTests | P3 |

La API no tiene un endpoint para corregir o eliminar dosis (T2.5 se descartó de la v2.0): la auditoría queda probada en la base con
`UPDATE` y `DELETE` directos.

## 4. Requisitos no funcionales con cifras medidas

Entorno: SQL Server 2022 en Docker, 20 000 pacientes y unas 308 000 dosis simuladas, medido el 03/10/2026.

| RNF | Meta | Resultado | Evidencia |
|---|---|---|---|
| RNF-01 Rendimiento | < 2 s con 5 000+ registros; local < 0,5 s con más de 300 000 dosis | Cobertura por distrito (todas las dosis, 532 filas) 398–414 ms; sarampión 169–181 ms; pendientes 193–203 ms; alertas 466–478 ms; resumen 176 ms. API del tablero: 62 ms el reporte y 168 ms la vista de sarampión. Con 10 usuarios simultáneos, la consulta más lenta (cobertura) llegó a 1,48 s | `desplegar.ps1 -Pruebas` (dos corridas), `docs/capacidad.md`, DashboardTests |
| RNF-02 Seguridad | Acceso autenticado por rol y establecimiento; solo hash; parámetros tipados; conexión cifrada | PBKDF2 (`PasswordHasher`); cookie `HttpOnly`, `SameSite=Strict` y `Secure` fuera de desarrollo; CSP sin scripts de terceros; bloqueo a los 5 intentos y límite por IP; 403 uniforme; conexión con `Encrypt=True`. Revisión de secretos y de paquetes vulnerables sin hallazgos | AutorizacionTests (24), SeguridadTests (13), `docs/seguridad.md` |
| RNF-03 Concurrencia | Con dos reservas del último cupo, una sola se acepta; 20 usuarios en la demostración | 22 escenarios con 2, 10 y 20 sesiones: siempre exactamente las reservas que caben, nunca sobrecupo ni interbloqueos. **Pendiente:** 20 usuarios simultáneos por HTTP contra el hosting (T6.3) | `tests/concurrencia.ps1` (227 s) |
| RNF-04 Integridad | 0 dosis que incumplan el esquema | 36 claves foráneas, 31 CHECK, 14 únicas y triggers; casos N1–N11 rechazan la dosis inválida por el procedimiento y por `INSERT` directo | 144/144 casos SQL |
| RNF-05 Confiabilidad | Transaccional (ACID) | `SET XACT_ABORT ON` y transacciones en cada procedimiento; una falla revierte toda la operación (p. ej. atender sin stock deja la cita sin cambios) | AtencionTests, casos D y H |
| RNF-06 Usabilidad | Mensajes en español; pantallas adaptables | Mensajes de negocio y de validación en español, sin nombres internos; hojas de estilo con ajustes para pantallas pequeñas. **No verificado con sesión real en un celular** | ArchivosEstaticosTests y revisión con respuestas simuladas |
| RNF-07 Mantenibilidad | Scripts numerados e idempotentes; pruebas automatizadas | Dos despliegues limpios seguidos sin errores (33 y 38 s, 144/144 las dos veces); carga de producción en 2 s | `desplegar.ps1`, `tests/produccion.ps1` |
| RNF-08 Recuperación | Base recreable con los scripts; sin garantía del plan gratuito | Esquema, catálogos y administrador se recrean con `desplegar-remoto.ps1 -Produccion` (ver sección 8) | `tests/produccion.ps1` |

Memoria de la aplicación: 91,5 MB de memoria de trabajo tras recorrer todos los GET con la base de producción vacía, y 145 MB de pico con
20 000 pacientes y 10 usuarios simultáneos, frente a los 256 MB del plan gratuito (`docs/capacidad.md`, `tests/publicacion.ps1`). Base de
datos: 12 MB tras la carga de producción y 272 MB con los 20 000 pacientes, frente a 1 GB.

## 5. Criterios de éxito (SPEC §9)

| # | Criterio | Estado |
|---|---|---|
| 1 | 23 tablas en 3FN; `desplegar.ps1` recrea todo sin errores dos veces seguidas | 23 tablas y dos despliegues limpios seguidos: cumplido. La 3FN es del diseño (FD04); no hay prueba automática |
| 2 | Login con 5 roles; cada rol ve solo su menú; vacunador y jefe limitados a su establecimiento (403) | Cumplido en API y pruebas (AutorizacionTests, 24); el menú por rol **no se vio con sesión real** |
| 3 | 0 dosis inválidas insertables (RN-03…RN-11) por cualquier vía | Cumplido |
| 4 | Dos reservas simultáneas del último cupo: exactamente una aceptada | Cumplido (22 escenarios) |
| 5 | Cada dosis descuenta stock; stock 0 la rechaza; el umbral genera alerta | Cumplido |
| 6 | Tablero con semáforos (RN-21) en menos de 2 s | Cumplido (62 ms el reporte y 168 ms la vista de sarampión, medido con 20 000 pacientes) |
| 7 | Interfaz usable en celular (≥ 360 px) | Parcial: diseño adaptable, sin verificación en un celular con sesión |
| 8 | Pruebas BD y API en verde; limitaciones de MonsterASP.NET documentadas | Pruebas en verde; los límites del hosting vienen de su ayuda, **sin comprobar en el hosting real** |

## 6. Decisiones técnicas que conviene conocer

| Decisión | Motivo | Dónde |
|---|---|---|
| Reglas en triggers, procedimientos y restricciones; API delgada | Valen por cualquier vía de entrada y se prueban en SQL | SPEC §5 |
| Cookie de sesión con `PasswordHasher` (PBKDF2); bloqueo a los 5 intentos | SQL Server no tiene bcrypt; el bloqueo no figuraba en FD03/FD04 y se añadió por seguridad | SPEC §12 |
| Carné por DNI de cualquier paciente para vacunador, jefe, epidemiólogo y administrador | Los pacientes no pertenecen a un establecimiento y hay que ubicarlos para vacunar | SPEC §12 |
| Alertas de stock calculadas al consultar | El hosting no tiene SQL Server Agent | SPEC §12 |
| `usp_ListarPendientes`: tabla temporal y `OPTION (LOOP JOIN, HASH JOIN)`, más `11_estadisticas.sql` | Plan inestable: 30–303 s tras una carga masiva reciente; ahora entre 0,09 y 4 s | SPEC §12, `docs/capacidad.md` |
| Tablero regional; umbrales de RN-21 reutilizados de la base | Una sola fuente de verdad para el semáforo | SPEC §12 |
| Chart.js servido desde `wwwroot/vendor`, no desde un CDN | La política CSP solo admite scripts propios y el hosting no debe depender de un tercero | SPEC §12 |
| Campañas sin cambio de esquema; estado derivado de las fechas; «por vacuna» deducido de las dosis | No hay columna de vacuna en `vac.Campana`; añadirla sería un cambio de esquema fuera del plan | SPEC §12 |
| Contenido estático con `charset=utf-8` | El navegador ya no adivina la codificación de los scripts | SPEC §12 |
| T2.5 (corregir o eliminar dosis) descartada | No existe el endpoint; la auditoría se prueba en la base | SPEC §12 |
| Producción: `12_produccion.sql` retira lo de demostración y `admin` queda sin clave | Ninguna clave en el repositorio; se asigna desde `Seed:AdminPassword` | SPEC §12 |
| El paquete publicado no lleva configuración local | Antes viajaban la clave de `sa` y las claves semilla de la máquina | SPEC §12 |

## 7. Límites conocidos

1. **Sin cambio ni restablecimiento de contraseña.** Procedimiento manual en `docs/despliegue-monsterasp.md` (sección 11).
2. **Sin alta de vínculos ciudadano–paciente en la aplicación.** En producción, el ciudadano no ve a sus hijos hasta que se inserta el vínculo con
   SQL (misma sección).
3. **Sin alta ni baja de personal vacunador ni de establecimientos.** Los 34 establecimientos y los 68 vacunadores del catálogo son simulados y
   solo se cargan desde `02_catalogos.sql`; para personal real hay que insertarlos con SQL antes de crear sus cuentas.
4. **Dosis:** no se corrigen ni eliminan desde la aplicación, y corregir una dosis en la base no devuelve stock (FD03 no lo define; pendiente de
   validar con la DIRESA).
5. **Campañas:** no tienen vacuna propia ni obligan a que las dosis sean de una vacuna.
6. **Tablero y cobertura:** un distrito sin pacientes no aparece.
7. **Alertas y pendientes:** se devuelven las primeras 200 filas (máximo 1 000).
8. **Limitador de intentos de login** en memoria, válido para una sola instancia; detrás de un proxy sin reenvío de la IP real se cuenta por la IP del proxy.
9. **Conexión con `TrustServerCertificate=True`:** cifra, pero no valida la identidad del servidor.
10. **La tolerancia de 1 mes** de las alertas atrasadas está en dos sitios (el generador y el cierre de brote); si cambia, hay que cambiarla en ambos.
11. **Consultas que usan `fn_DosisPendientes`:** `usp_ListarPendientes` se estabilizó; no se detectó el mismo problema en `vw_DosisPendientes`
    (193–203 ms), pero comparten el patrón y conviene volver a medir tras cargas masivas.
12. **Umbrales de negocio** (30 días, 24 horas, 95 % y 80 %) y lista de dosis: supuestos del equipo, por validar con la DIRESA.
13. **Pruebas:** la interfaz se comprobó con respuestas simuladas, no con una sesión real de punta a punta. Durante el desarrollo se observó una falla
    aislada de la suite xUnit que no se pudo reproducir. La suite reasigna las claves de las cuentas semilla en la base de desarrollo
    (ver `docs/manual-pruebas.md`).

## 8. Recuperación (RNF-08)

- **Lo que los scripts recrean:** esquema, catálogos, vistas, procedimientos, triggers y el administrador (`desplegar-remoto.ps1 -Produccion`
  sobre una base vacía, 2 s en local). Los lotes, las franjas, las cuentas de usuario y todo el movimiento (pacientes, dosis, citas, alertas, brotes,
  campañas, auditoría) **no** se recrean: son datos, no scripts.
- **Lo que hace falta además:** una copia de la base. La ayuda de MonsterASP.NET describe cómo restaurar una base desde un archivo `.bak` subido al panel
  y menciona la descarga automática de copias por FTP para planes de pago; si el plan gratuito incluye copias o solo exportación manual **no se ha
  confirmado**. Hasta saberlo, exporte la base periódicamente.
- **Procedimiento ante una falla:** vaciar la base desde el panel, ejecutar `-Produccion`, definir `Seed__AdminPassword`, reiniciar y volver a crear
  usuarios; si hay copia, restaurarla en lugar de lo anterior.

## 9. Pendiente

| Tarea | Responsable |
|---|---|
| Crear el sitio y la base en MonsterASP.NET, cargar con `-Produccion`, definir variables, subir el ZIP, activar HTTPS (`docs/despliegue-monsterasp.md`) | Equipo, con sus credenciales |
| T6.3: prueba de carga y concurrencia contra la URL pública (20 usuarios simultáneos, tablero en menos de 2 s); `tests/concurrencia.ps1` hoy ataca SQL Server directamente | Después de publicar |
| Recorrer `docs/manual-pruebas.md` con sesión real, incluido un celular | Equipo |
| Actualizar FD01–FD06 con las decisiones finales (los `.docx` no se tocaron) | Equipo |
| Validar con la DIRESA los umbrales y el tratamiento del stock al corregir dosis | Equipo y docente |
| Decidir si se cubren los límites 1 a 3 antes de la entrega (cambio de contraseña, vínculo ciudadano–paciente, alta de personal) | Equipo |
