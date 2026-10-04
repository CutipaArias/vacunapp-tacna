# Spec: VacunApp Tacna v2.0

> Fuente de verdad: FD01–FD05 v2.0 (03/10/2026). Este spec traduce esos documentos a trabajo implementable sobre el repo existente (v1.0: 16 tablas, 9 SP, 5 triggers, 7 vistas, panel de solo consulta).
> Estado: **BORRADOR – pendiente de aprobación (Fase 1: Specify).**

## 0. Capability Map

| Module id | Responsabilidad | RF | Depende de |
|---|---|---|---|
| `identidad` | Roles, usuarios, hash de contraseña, sesión por cookie, políticas por rol/establecimiento, VinculoFamiliar | RF-01, RF-02 | — |
| `registro` | Pacientes, dosis aplicadas validadas (esquema), carné, auditoría JSON; migra `PersonalSalud`→`Vacunador` | RF-03, RF-04, RF-15, RF-16 | identidad |
| `stock` | LoteVacuna, StockLote, MovimientoStock, descuento por dosis, alertas de stock/lote por vencer | RF-09 | registro |
| `agenda` | HorarioAtencion (cupos), citas: reservar/reprogramar/cancelar/atender/inasistencia, concurrencia con UPDLOCK | RF-05, RF-06, RF-07, RF-08 | identidad, registro, stock |
| `vigilancia` | Brotes, alertas, pendientes, campañas, dashboard de cobertura (Chart.js) | RF-10–RF-14 | registro, agenda, stock |
| `despliegue` | Scripts idempotentes, datos simulados 20 000 pacientes, pruebas, MonsterASP.NET, manuales | RNF-07, RNF-08 | todos |

Build order: `identidad` → `registro` → `stock` → `agenda` → `vigilancia` → `despliegue` (hardening final).
(Alinea con FD04 §2.1: roles y registro validado primero; luego citas y cobertura; stock, brotes y alertas; al final campañas y auditoría.)

## 1. Objetivo
Aplicación web + SQL Server para la DIRESA Tacna: registrar dosis con validación automática, gestionar citas con cupos y stock por establecimiento, y mostrar cobertura de SPR por distrito el mismo día (meta 95 % con 2 dosis). Cinco roles: vacunador, jefe de establecimiento, epidemiólogo, ciudadano, administrador. Datos 100 % simulados (Ley 29733).

## 2. Tech Stack
SQL Server 2022 (Docker local; MonsterASP.NET en prod) · T-SQL numerado e idempotente · ASP.NET Core Minimal API (.NET 10) · Microsoft.Data.SqlClient con `SqlParameter` · autenticación por cookies + políticas por rol · HTML/CSS/JS sin framework + Chart.js · sin ORM.

## 3. Commands
```
Base de datos:  ./desplegar.ps1 -Pruebas          # recrea SQL Server en Docker, corre 01..07
Panel (dev):    dotnet run --project panel         # http://localhost:5198
Build:          dotnet build panel
Pruebas BD:     ./desplegar.ps1 -Pruebas           # 07_pruebas.sql debe terminar sin fallos
Pruebas API:    dotnet test tests/VacunApp.Tests   # (nuevo; xUnit contra SQL Server de Docker)
Concurrencia:   ./tests/concurrencia.ps1           # (nuevo) 2 reservas del último cupo → 1 aceptada
```

## 4. Project Structure
```
database/   01_esquema … 07_pruebas  (+ nuevos: 08_seguridad_agenda_stock según plan)
panel/      Program.cs (endpoints) · Auth/ · Data/ · wwwroot/ (UI por rol)
tests/      VacunApp.Tests (xUnit) · concurrencia.ps1
docs/       diagramas Mermaid/PlantUML, manuales
FD01–FD06   informes (no se tocan salvo cambio de spec)
SPEC.md · tasks/plan.md · tasks/todo.md
```

## 5. Code Style
T-SQL: esquema `vac.`, prefijos `usp_`, `trg_`, `vw_`, `fn_`; `CREATE OR ALTER`; `SET XACT_ABORT ON`; errores con `THROW 5xxxx` y mensaje en español.
```sql
CREATE OR ALTER PROCEDURE vac.usp_ReservarCita @IdPaciente INT, @IdHorario INT, @IdEsquema INT
AS BEGIN SET NOCOUNT ON; SET XACT_ABORT ON;
  BEGIN TRAN;
  SELECT @cupo = CupoMaximo FROM vac.HorarioAtencion WITH (UPDLOCK, HOLDLOCK) WHERE IdHorario=@IdHorario;
  ...
  COMMIT;
END
```
C#: Minimal API, un grupo de endpoints por módulo, `SqlParameter` tipados siempre, `RequireAuthorization("Rol")`, SQL error → mensaje español.

## 6. Reglas y requisitos (trazables)
Se implementan literalmente RF-01…RF-16, RNF-01…08 y RN-01…RN-23 de FD03 §IV. Cada módulo spec (`SPEC-<id>.md`) listará los RF/RN que cubre y sus criterios de aceptación.

## 7. Testing Strategy
- **BD:** casos en `07_pruebas.sql` por cada RN con trigger/SP (positivo + negativo); hoy 24, objetivo ≥ 1 caso por RN-03…RN-20.
- **API:** xUnit de autorización (403 fuera de alcance de rol/establecimiento, RN-22) y de mapeo de errores.
- **Concurrencia:** script con 2+ sesiones sobre el último cupo; 20 usuarios simultáneos.
- **Rendimiento:** cobertura < 2 s (local < 0.5 s) con 20 000 pacientes / 300 000+ dosis.
- **Seguridad:** sin SQL concatenado; contraseñas solo hash; revisión OWASP básica.

## 8. Boundaries
- **Always:** scripts idempotentes; reglas en BD, no solo en API; parámetros tipados; mensajes en español; correr `desplegar.ps1 -Pruebas` antes de cerrar una tarea; actualizar este spec si cambia una decisión.
- **Ask first:** cambiar el esquema ya entregado en v1 de forma no compatible (renombrar `PersonalSalud`), agregar paquetes NuGet/npm, cambiar umbrales de negocio (30 d, 24 h, 95 %/80 %), publicar en MonsterASP.NET.
- **Never:** datos reales; contraseñas en claro o commiteadas (la `sa` de desarrollo está solo en docker-compose/README); borrar pruebas que fallan; SQL concatenado; tocar los .docx/FD sin pedirlo.

## 9. Success Criteria
1. 23 tablas en 3FN según FD04 §3.2.6; `desplegar.ps1` recrea todo sin errores dos veces seguidas (idempotencia).
2. Login con 5 roles; cada rol ve solo su menú; vacunador/jefe limitados a su establecimiento (403 comprobado).
3. 0 dosis inválidas insertables (RN-03…RN-11) por cualquier vía.
4. Con 2 reservas simultáneas del último cupo: exactamente 1 aceptada.
5. Cada dosis descuenta stock; stock 0 la rechaza; umbral genera alerta.
6. Dashboard de cobertura con semáforos (RN-21) < 2 s.
7. UI usable en celular (≥ 360 px).
8. Todas las pruebas BD/API en verde; documentadas las limitaciones de MonsterASP.NET.

## 10. Supuestos que estoy haciendo (corrígeme)
1. Se **evoluciona el repo actual** (v1→v2), no se reescribe; se conservan datos simulados y vistas existentes.
2. `PersonalSalud` pasa a ser `Vacunador` (FD04); se migra con script, sin romper `usp_RegistrarDosis`.
3. Desarrollo y pruebas en Docker local; **el despliegue a MonsterASP.NET lo haces tú** (credenciales), yo dejo los scripts y el manual.
4. Sin framework JS: UI en HTML/JS plano + Chart.js (CDN).
5. Contraseñas con hash PBKDF2 en la API (SQL Server no tiene bcrypt); la tabla guarda solo hash+sal.
6. Pruebas API con xUnit (paquete nuevo) — requiere tu OK por la regla "Ask first".
7. Los .docx/FD ya están alineados; no los regenero.

## 11. Open Questions
- ¿Apruebas el orden y los límites de módulos del Capability Map?
- ¿Despliegue a MonsterASP.NET dentro de este alcance o solo scripts + manual?
- ¿OK a agregar xUnit?
- Fecha límite: FD04 indica 23/10/2026 — ¿se mantiene?

## 12. Adiciones aprobadas al spec
- **Bloqueo de login (añadido 03/10/2026, T1.6):** tras 5 intentos fallidos consecutivos de un usuario, el login se bloquea temporalmente; no figura en FD03/FD04 y se agrega por seguridad.
- **Contraseñas:** `PasswordHasher` de ASP.NET Core (PBKDF2); la tabla guarda solo el hash.
- **Usuarios semilla:** `admin`, `epi01`, `jefe01`, `vac01`, `ciud01`. La contraseña de demostración solo se documenta en el manual de pruebas y se lee de configuración (nunca en código ni repo); la de `admin` la define el equipo al desplegar.
- **Despliegue (T6.1/T6.2):** el equipo introduce las credenciales y publica; el asistente prepara scripts, configuración y pasos.
- **Prueba de capacidad:** antes de `agenda` se prueba la carga de 20 000 pacientes en el límite de 256 MB RAM / 1 GB BD; si no cabe, se reduce el volumen en producción.
- **T4.2 no se da por terminada** si la prueba de 2/10/20 sesiones sobre un cupo no pasa.
- **Consulta de carné por DNI (RN-17, aprobado 03/10/2026, Checkpoint B):** el personal clínico (vacunador y jefe de establecimiento), así como el epidemiólogo y el administrador, consulta el carné de **cualquier** paciente por DNI, porque los pacientes no pertenecen a un establecimiento y deben ubicarse para vacunarlos. El ciudadano solo consulta a los pacientes con vínculo familiar; un DNI ajeno y uno inexistente reciben la misma respuesta 403.
- **Auditoría con usuario de aplicación (RN-23):** toda operación que corrija o elimine dosis fija antes `sp_set_session_context N'usuario'` con el usuario de la sesión, para que `trg_DosisAplicada_Auditoria` registre quién la hizo (si no, cae a `SUSER_SNAME()`). **T2.5 queda fuera del alcance de v2.0 (descartada, 03/10/2026):** la API no tiene ningún endpoint que corrija o elimine dosis, así que no hay operación sobre la que fijar ese contexto; el trigger de auditoría queda probado en la base (`07_pruebas.sql`, UPDATE y DELETE generan fila). Si una versión futura añade ese endpoint, deberá fijar el contexto y llevar su prueba de auditoría.
- **Stock (Fase 3, aprobado en el plan; decisiones de diseño 03/10/2026):** las alertas de stock (`STOCK_BAJO`, `LOTE_POR_VENCER`) se guardan en `vac.Alerta` (sin paciente ni esquema; `CK_Alerta_Origen` impide mezclar tipos) y no cuentan en el KPI de alertas de pacientes. `usp_GenerarAlertasStock` se invoca al consultar `/api/stock/alertas` (no hay SQL Server Agent en el hosting). El inventario sembrado (1 000 unidades por lote vigente y establecimiento, umbral 50) es un inventario inicial de demostración: no se reconcilia con las dosis históricas de la carga de prueba. **Limitación conocida:** corregir o eliminar una dosis no devuelve stock (FD03 no lo define; pendiente de validar con la DIRESA, no se implementa). El catálogo de lotes ofrece al jefe y al vacunador solo lotes con existencias en su establecimiento.
- **Agenda (Fase 4, T4.1/T4.2, decisiones de diseño 03/10/2026):** una franja es única por establecimiento + vacuna + fecha y hora (`UQ_Horario_Franja`); cupo entre 1 y 500; no se crean franjas en el pasado. Una cita ocupa cupo mientras no esté `CANCELADA` (programada, atendida e inasistencia cuentan). La elegibilidad (edad, dosis anterior, intervalo mínimo, dosis no aplicada) se evalúa **a la fecha de la franja** con `fn_CitaElegible`. Una sola cita `PROGRAMADA` por paciente y dosis (`UX_Cita_DosisActiva`). `usp_ReservarCita` toma primero un bloqueo de aplicación por paciente+dosis y luego `UPDLOCK, HOLDLOCK` sobre la franja (orden fijo, sin interbloqueos); `trg_Cita_Validar` revalida cupo y elegibilidad en inserciones directas. El cupo de una franja no puede bajar de las citas ya reservadas. Códigos 50120–50135. Prueba de concurrencia `tests/concurrencia.ps1` (2, 10 y 20 sesiones: una franja de 1 cupo → 1 aceptada; misma dosis en N franjas → 1; 5 cupos → 5).
- **Cancelar, reprogramar, atender e inasistencia (Fase 4, T4.4/T4.5, decisiones de diseño 03/10/2026):** orden de bloqueos común a todo el módulo: 1.º `sp_getapplock` por paciente+dosis, 2.º franjas en orden ascendente de `IdHorario` con búsquedas puntuales (un `IN (...)` con `HOLDLOCK` produce recorridos de rango e interbloqueos entre reprogramaciones cruzadas; el escenario E de `tests/concurrencia.ps1` lo cubre). Cancelar y reprogramar solo hasta 24 h antes de la franja (RN-16; códigos 50137 cita no programada, 50138 menos de 24 h, 50136 cita inexistente, 50129 misma franja). Reprogramar es atómico y evalúa la 24 h sobre la cita original. **Atender** (`usp_AtenderCita`, solo el día de la franja, código 50140) invoca `usp_RegistrarDosis`: no duplica validaciones clínicas ni descuento de stock, marca la cita `ATENDIDA` en la misma transacción (si la dosis falla, p. ej. sin stock, la cita no cambia) y el trigger existente cierra las alertas pendientes de esa dosis; el vacunador es siempre el de la sesión y el establecimiento el de la franja. **Inasistencia** (`usp_RegistrarInasistencia`, solo cuando la hora ya pasó, código 50141; 50139 si la cita no está programada) marca `NO_ASISTIO`, no toca el stock, libera la dosis para volver a reservar y crea una alerta de seguimiento nueva tipo `INASISTENCIA` (se agrega a `CK_Alerta_Tipo`/`CK_Alerta_Origen`; no se crea si la dosis ya tiene una alerta pendiente). La alerta se cierra sola cuando la dosis se aplica. El personal solo opera citas de su establecimiento; una cita ajena y una inexistente reciben el mismo 403.
- **Brotes (Fase 5, T5.1, decisiones de diseño 03/10/2026):** `GET/POST /api/brotes` y `POST /api/brotes/{id}/cerrar`, solo con la política Regional (administrador y epidemiólogo; el resto recibe 403). La lógica ya vivía en la base (`usp_DeclararBrote`, `usp_CerrarBrote`, `UX_Brote_Activo`, `trg_Brote_GenerarAlertas`); T5.1 añade validaciones con mensaje propio en vez de dejar que salte un CHECK (50024 casos negativos, 50025 inicio futuro, 50026 cierre anterior al inicio) y convierte el rechazo del índice único en dos declaraciones simultáneas en el mismo 50022. Declarar devuelve `alertasGeneradas` y cerrar devuelve `alertasDescartadas`. Declarar un brote escala a `ZONA_BROTE` las alertas `DOSIS_ATRASADA` pendientes de la zona (comportamiento previo del trigger). **Rendimiento (RNF-01), corregido 03/10/2026:** `usp_ListarPendientes @SoloZonaBrote = 1` tardaba 4,3 s tras un despliegue limpio y 49 s tras `07_pruebas.sql` (la primera revisión, que decía que no se reproducía, era errónea); la lista general y las demás variantes llegaban a 30–303 s por el mismo plan inestable (merge join muchos-a-muchos, agravado por estadísticas de `Brote` con 1 fila frente a 3). Corregido solo en el procedimiento (zona de brote materializada una vez en una tabla temporal y `OPTION (LOOP JOIN, HASH JOIN)`) más `11_estadisticas.sql` al final de la carga; casos H47 y H52–H56 de `07_pruebas.sql` como guarda. Detalle y nota para T6 en `docs/capacidad.md`.
- **Alertas y pendientes (Fase 5, T5.2, decisiones de diseño 03/10/2026):** `GET /api/alertas` (filtros `tipo`, `ubigeo`, `documento`, `top` ≤ 1000), `GET /api/alertas/stock`, `POST /api/alertas/{id}/atender` y `GET /api/pendientes` con la política ConsultaClinica. Alcance (RN-22): administrador y epidemiólogo ven toda la región; vacunador y jefe, los pacientes del distrito de su establecimiento más las alertas `INASISTENCIA` cuyo paciente faltó a una cita en su establecimiento (puede vivir en otro distrito); pedir otro ubigeo da 403. Una alerta ajena y una inexistente reciben el mismo 403; una alerta ya cerrada dentro del alcance da 400/50041. Las alertas de stock no se atienden a mano (se cierran al reponer) y se recalculan al consultar. Cambio de contrato: `/api/alertas` y `/api/pendientes` dejan de ser solo regionales. Cierre de brote (RN-20, corregido tras T5.2): `usp_CerrarBrote` ya no descarta todo; las alertas pendientes del brote se resuelven así: si otro brote activo cubre la dosis, pasan a ese brote; si la dosis tiene más de 1 mes de atraso (la tolerancia de `usp_GenerarAlertasAtrasadas`), vuelven a `DOSIS_ATRASADA` (el brote las había escalado y el paciente sigue pendiente); el resto se descarta. No hay marca de origen en el esquema: el atraso se recalcula con la misma regla del generador. La respuesta de `POST /api/brotes/{id}/cerrar` trae `alertasRestauradas`, `alertasDescartadas` y `alertasReasignadas`. Los pacientes nunca salen de la lista de pendientes, que se calcula de las dosis y no de las alertas.
- **Tablero de cobertura (Fase 5, T5.3, decisiones de diseño 03/10/2026):** `GET /api/dashboard?vacuna=&dosis=` con la política Regional (administrador y epidemiólogo; jefe, vacunador y ciudadano reciben 403, sin sesión 401). Por decisión del equipo el tablero es regional: se descarta la variante «el jefe ve solo su zona» que figuraba en `tasks/todo.md`, porque el jefe ya ve sus alertas y pendientes por alcance. **Los umbrales no se reescriben: se reutilizan.** El semáforo lo calcula `vac.usp_ReporteCoberturaDistrito` (RN-21): **ÓPTIMA** ≥ 95 %, **ACEPTABLE** ≥ 80 %, **CRÍTICA** < 80 % (meta OMS/OPS de 95 % para interrumpir la transmisión), sobre `vac.vw_CoberturaDistrito` (elegibles = pacientes que ya alcanzaron la edad mínima de esa dosis; vacunados = los que la tienen aplicada). El riesgo de sarampión sale de `vac.vw_CoberturaSarampion`: **ALTO** con brote activo o SPR 1.ª dosis < 80 %, **MEDIO** con SPR 2.ª dosis < 95 %, **BAJO** en el resto. La API solo une ambos resultados por distrito (los dos valores, 95 y 80, que devuelve en `umbrales` sirven para dibujar las líneas de referencia; la clasificación de cada distrito la decide siempre la base) y las pruebas comparan su salida con las consultas de la base. Sin parámetros: SPR con el esquema completo (2.ª dosis); sin `dosis`, la última del esquema de esa vacuna; una vacuna o dosis inexistente da 400 en español. Distritos de menor a mayor cobertura, para priorizar. Medido: 62 ms el reporte y 168 ms la vista de sarampión con 20 000 pacientes (meta RNF-01: < 2 s). **Chart.js 4.5.1** (MIT, integridad sha512 verificada contra el registro de npm) se sirve desde `panel/wwwroot/vendor/` y no desde un CDN: la política CSP del panel solo admite scripts propios y el hosting no debe depender de un tercero; se quitó únicamente el comentario `sourceMappingURL`. Es la única biblioteca de interfaz añadida. Las gráficas llevan `aria-label` y los mismos datos están en la tabla de detalle.
