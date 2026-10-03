# Task List: VacunApp Tacna v2.0

Convención: S = 1–2 archivos, M = 3–5 archivos. Verificación base de todo task: `./desplegar.ps1 -Pruebas` y `dotnet build panel` (más `dotnet test tests/VacunApp.Tests` desde T0.1).
Plan: [plan.md](plan.md) · Spec: [../SPEC.md](../SPEC.md)

---
## Fase 0 – Cimientos

- [x] **T0.1 Proyecto de pruebas xUnit y línea base** (S)
  - Acceptance: existe `tests/VacunApp.Tests` con una prueba que levanta la API (`WebApplicationFactory`) y consulta `/api/resumen` contra Docker; `07_pruebas.sql` pasa 24/24 como línea base.
  - Verify: `dotnet test tests/VacunApp.Tests`
  - Files: `tests/VacunApp.Tests/*.csproj`, `tests/VacunApp.Tests/ResumenTests.cs`, `panel/Program.cs` (`public partial class Program`)
  - Deps: —

- [x] **T0.2 Migrar `PersonalSalud`→`Vacunador`** (M)
  - Acceptance: tabla y FK renombradas (`IdVacunador`); SP, triggers, vistas y datos de prueba actualizados; 0 referencias a `PersonalSalud` fuera de comentarios históricos; 24/24 pruebas siguen verdes.
  - Verify: `Select-String database\*.sql -Pattern PersonalSalud` sin resultados; `./desplegar.ps1 -Pruebas`
  - Files: `database/01…07.sql` (solo líneas afectadas)
  - Deps: T0.1

### Checkpoint 0
- [ ] Línea base verde antes y después de la migración; revisión contigo.

---
## Fase 1 – `identidad` (con /security-and-hardening)

- [x] **T1.1 Tablas `Rol`, `Usuario`, `VinculoFamiliar` + usuarios semilla** (M)
  - Acceptance: 3 tablas con restricciones (usuario único, FK a rol y establecimiento); 5 roles; 5 usuarios semilla; `ClaveHash` nunca en claro; script idempotente.
  - Verify: `./desplegar.ps1 -Pruebas` dos veces seguidas sin error; casos nuevos en `07_pruebas.sql`
  - Files: `database/08_seguridad.sql`, `database/07_pruebas.sql`, `desplegar.ps1`
  - Deps: T0.2

- [x] **T1.2 Login/logout por cookie con `PasswordHasher`** (M) — CU01
  - Acceptance: `POST /api/login` verifica hash PBKDF2 (PasswordHasher); credenciales malas o usuario inactivo → 401 con mensaje en español sin revelar cuál falló; cookie `HttpOnly`, `SameSite=Strict`, `Secure` fuera de Development; `POST /api/logout`; `GET /api/yo` devuelve rol y establecimiento.
  - Verify: tests xUnit: login ok, login mal, usuario inactivo, logout, `/api/yo` sin sesión → 401
  - Files: `panel/Program.cs`, `panel/Auth/AuthEndpoints.cs`, `panel/Data/UsuarioRepo.cs`, `tests/…/AuthTests.cs`
  - Deps: T1.1

- [x] **T1.3 Políticas por rol y alcance por establecimiento (RN-22)** (M)
  - Acceptance: todo `/api/*` existente exige sesión; políticas `Vacunador/Jefe/Epidemiologo/Ciudadano/Administrador`; helper `Alcance` filtra por establecimiento para vacunador/jefe; acceso fuera de alcance → 403.
  - Verify: tests xUnit matriz rol × endpoint (403/200) 
  - Files: `panel/Auth/Politicas.cs`, `panel/Auth/Alcance.cs`, `panel/Program.cs`, `tests/…/AutorizacionTests.cs`
  - Deps: T1.2

- [x] **T1.4 Pantalla de login y menú por rol** (M) — RNF-06
  - Acceptance: `login.html` usable en 360 px; tras login se muestra menú solo con opciones del rol; la página de consulta actual pasa a requerir sesión; errores en español.
  - Verify: manual en navegador con los 5 usuarios (captura de cada menú) + `read_console_messages` sin errores
  - Files: `panel/wwwroot/login.html`, `panel/wwwroot/app.js`, `panel/wwwroot/index.html`, `panel/wwwroot/estilos.css`
  - Deps: T1.3

- [x] **T1.5 Gestión de usuarios y roles (admin)** (M) — RF-02, CU02
  - Acceptance: el administrador crea/desactiva usuarios y asigna rol y establecimiento; clave inicial hasheada por la API; validación de política de clave (mín. 8 caracteres); un no-admin recibe 403.
  - Verify: tests xUnit (crear, duplicado → 400, no-admin → 403)
  - Files: `panel/Auth/UsuariosEndpoints.cs`, `database/08_seguridad.sql` (SP `usp_CrearUsuario`), `panel/wwwroot/usuarios.html`, `tests/…/UsuariosTests.cs`
  - Deps: T1.3, T1.4

- [x] **T1.6 Revisión de seguridad del módulo** (S)
  - Acceptance: checklist de /security-and-hardening aplicada: sin SQL concatenado, límites de intentos de login (bloqueo temporal tras 5 fallos), cabeceras de seguridad, sin secretos en el repo; hallazgos corregidos o documentados.
  - Verify: grep de concatenación SQL sin resultados; test de bloqueo tras 5 intentos
  - Files: `panel/Program.cs`, `docs/seguridad.md`
  - Deps: T1.5

### Checkpoint A (identidad)
- [ ] 5 roles inician sesión, ven su menú, 403 comprobado; todo verde; revisión contigo.

---
## Fase 2 – `registro`

- [x] **T2.1 Registrar paciente con vacunador/jefe (RF-03, CU03)** (M)
  - Acceptance: `POST /api/pacientes` usa `usp_RegistrarPaciente`; RN-01/02 devuelven mensaje en español (documento duplicado, fecha futura, ubigeo inexistente); formulario en UI.
  - Verify: xUnit (ok, duplicado, fecha futura); casos RN-01/02 en `07_pruebas.sql`
  - Files: `panel/Program.cs`, `panel/Pacientes/PacientesEndpoints.cs`, `panel/wwwroot/pacientes.html`, `tests/…`
  - Deps: Checkpoint A

- [x] **T2.2 Registrar dosis aplicada por API/UI (RF-04, CU04)** (M)
  - Acceptance: vacunador registra dosis de su establecimiento (RN-22); el trigger `trg_DosisAplicada_Validar` cubre RN-03…RN-09 y devuelve el motivo; la UI lista las dosis elegibles del paciente.
  - Verify: xUnit por regla (edad mínima/máxima, dosis previa, intervalo, lote vencido, vacunador inactivo/otro establecimiento); pruebas BD RN-03…RN-09
  - Files: `database/04_procedimientos.sql`, `database/05_triggers.sql`, `panel/Pacientes/DosisEndpoints.cs`, `panel/wwwroot/dosis.html`, `tests/…`
  - Deps: T2.1

- [x] **T2.3 Carné de vacunación (RF-15, CU14)** (S)
  - Acceptance: vacunador ve cualquier paciente de su establecimiento; ciudadano solo los vinculados (RN-17); dosis aplicadas y pendientes.
  - Verify: xUnit: ciudadano consultando paciente ajeno → 403
  - Files: `panel/Pacientes/CarneEndpoints.cs`, `panel/wwwroot/carne.html`, `tests/…`
  - Deps: T2.1

- [x] **T2.4 Auditoría de dosis (RF-16, CU15)** (S)
  - Acceptance: el administrador consulta el JSON de correcciones/eliminaciones de `trg_DosisAplicada_Auditoria` (RN-23); otros roles 403.
  - Verify: prueba BD (UPDATE y DELETE generan fila) + xUnit 403
  - Files: `panel/Program.cs`, `panel/wwwroot/auditoria.html`, `tests/…`
  - Deps: T2.2

### Checkpoint B (registro)
- [ ] 0 dosis inválidas insertables por SP ni por INSERT directo; todo verde; revisión contigo.

---
## Fase 3 – `stock`

- [ ] **T3.1 Tablas `StockLote` y `MovimientoStock`** (M)
  - Acceptance: stock por lote y establecimiento con umbral mínimo; movimientos de entrada/salida; sembrado de stock para los establecimientos de prueba; sin cantidades negativas (CHECK).
  - Verify: `./desplegar.ps1 -Pruebas` (2 corridas); casos de CHECK
  - Files: `database/09_stock.sql`, `database/07_pruebas.sql`, `desplegar.ps1`
  - Deps: Checkpoint B

- [ ] **T3.2 Descuento de stock por dosis y alertas (RN-10…RN-13)** (M)
  - Acceptance: `trg_DosisAplicada_DescontarStock` descuenta 1; stock 0 rechaza (RN-11); `trg_StockLote_Alerta` crea alerta al llegar al umbral; `usp_GenerarAlertasStock` alerta lotes que vencen en ≤ 30 días; sin alertas duplicadas.
  - Verify: pruebas BD RN-10…RN-13; carrera: dos dosis simultáneas con stock 1 → solo una
  - Files: `database/09_stock.sql`, `database/05_triggers.sql`, `database/04_procedimientos.sql`, `database/07_pruebas.sql`
  - Deps: T3.1

- [ ] **T3.3 Gestión de stock por el jefe (RF-09, CU09)** (M)
  - Acceptance: el jefe registra ingreso de lote y ajuste solo en su establecimiento; ve stock actual y alertas; otros establecimientos → 403.
  - Verify: xUnit (ok, otro establecimiento 403, cantidad negativa 400)
  - Files: `panel/Stock/StockEndpoints.cs`, `panel/wwwroot/stock.html`, `tests/…`
  - Deps: T3.2

### Checkpoint C (stock)
- [ ] Cada dosis descuenta stock; stock 0 la rechaza; alertas visibles al jefe.

---
## Fase 4 – `agenda`

- [ ] **T4.1 Tablas `HorarioAtencion` y `Cita` + gestión de horarios (RF-08, CU08)** (M)
  - Acceptance: franjas con cupo máximo por establecimiento, vacuna y fecha/hora; índice único que evita franjas duplicadas; el jefe las crea/edita en su establecimiento.
  - Verify: xUnit (jefe ok, otro establecimiento 403); `desplegar` idempotente
  - Files: `database/10_agenda.sql`, `panel/Agenda/HorariosEndpoints.cs`, `panel/wwwroot/horarios.html`, `tests/…`
  - Deps: Checkpoint C

- [ ] **T4.2 `usp_ReservarCita` con control de concurrencia (RF-05, CU05, RN-14/15/17)** (M) — **tarea de mayor riesgo**
  - Acceptance: reserva con `UPDLOCK, HOLDLOCK`; `trg_Cita_Validar` revalida cupo y elegibilidad; no hay dos citas activas para la misma dosis; el ciudadano solo reserva para pacientes vinculados.
  - Verify: `tests/concurrencia.ps1`: 2, 10 y 20 sesiones sobre 1 cupo → exactamente 1 aceptada; pruebas BD RN-14/15
  - Files: `database/10_agenda.sql`, `tests/concurrencia.ps1`, `database/07_pruebas.sql`
  - Deps: T4.1

- [ ] **T4.3 Reservar desde la UI (celular)** (M) — RNF-06
  - Acceptance: flujo ≤ 4 pasos (paciente → dosis → establecimiento/franja → confirmar); muestra cupos disponibles; mensaje claro si la franja se llenó; usable a 360 px.
  - Verify: manual con `resize_window` mobile + xUnit del endpoint
  - Files: `panel/Agenda/CitasEndpoints.cs`, `panel/wwwroot/reservar.html`, `panel/wwwroot/app.js`, `tests/…`
  - Deps: T4.2

- [ ] **T4.4 Cancelar y reprogramar (RF-06, CU06, RN-16)** (S)
  - Acceptance: permitido hasta 24 h antes; liberan el cupo; reprogramar es atómico (si la nueva franja está llena, la cita original se conserva).
  - Verify: pruebas BD (23 h → rechaza, 25 h → acepta); xUnit
  - Files: `database/10_agenda.sql`, `panel/Agenda/CitasEndpoints.cs`, `tests/…`
  - Deps: T4.2

- [ ] **T4.5 Atender cita e inasistencia (RF-07, CU07, RN-18)** (M)
  - Acceptance: atender = registrar dosis + cita ATENDIDA + cierre de alertas, en una sola transacción (si falla la dosis, la cita no cambia); inasistencia → NO_ASISTIO + alerta de seguimiento.
  - Verify: pruebas BD (rollback si el stock es 0); xUnit
  - Files: `database/10_agenda.sql`, `panel/Agenda/AtencionEndpoints.cs`, `panel/wwwroot/citas-dia.html`, `tests/…`
  - Deps: T4.2, T2.2, T3.2

### Checkpoint D (agenda)
- [ ] Prueba de concurrencia (1 de N) y rollback verdes; flujo reservar→atender de punta a punta; revisión contigo.

---
## Fase 5 – `vigilancia`

- [ ] **T5.1 Brotes: declarar/cerrar con autorización (RF-10, CU10, RN-19/20)** (M)
  - Acceptance: solo epidemiólogo/admin; un solo brote activo por enfermedad y distrito (índice único filtrado); declarar genera alertas de pendientes y devuelve cuántas.
  - Verify: pruebas BD RN-19/20; xUnit (vacunador → 403)
  - Files: `panel/Vigilancia/BrotesEndpoints.cs`, `panel/wwwroot/brotes.html`, `database/07_pruebas.sql`, `tests/…`
  - Deps: Checkpoint D

- [ ] **T5.2 Alertas y pendientes por alcance (RF-11, RF-12, CU11)** (M)
  - Acceptance: alertas de brote, atraso, inasistencia y stock; vacunador/jefe ven su establecimiento, epidemiólogo toda la región; `usp_AtenderAlerta` accesible.
  - Verify: xUnit de alcance; prueba BD de generación sin duplicados
  - Files: `panel/Vigilancia/AlertasEndpoints.cs`, `panel/wwwroot/alertas.html`, `tests/…`
  - Deps: T5.1

- [ ] **T5.3 Dashboard de cobertura con semáforos (RF-13, CU12, RN-21, RNF-01)** (M)
  - Acceptance: gráficos Chart.js por distrito, semáforo ÓPTIMA/ACEPTABLE/CRÍTICA, riesgo de sarampión ALTO/MEDIO; jefe ve solo su zona; respuesta < 2 s con 20 000 pacientes / 300 000+ dosis.
  - Verify: medición con `SET STATISTICS TIME` y tiempo de la API; xUnit de forma del JSON
  - Files: `panel/Vigilancia/DashboardEndpoints.cs`, `panel/wwwroot/dashboard.html`, `panel/wwwroot/dashboard.js`, `tests/…`
  - Deps: T5.1

- [ ] **T5.4 Campañas con metas y avance (RF-14, CU13)** (S)
  - Acceptance: el epidemiólogo crea campañas con metas por distrito y ve el avance (`vw_AvanceCampana`).
  - Verify: xUnit crear/consultar; 403 para otros roles
  - Files: `panel/Vigilancia/CampanasEndpoints.cs`, `panel/wwwroot/campanas.html`, `tests/…`
  - Deps: T5.3

### Checkpoint E (vigilancia)
- [ ] Los 16 RF cubiertos; matriz RF→prueba completa; revisión contigo.

---
## Fase 6 – `despliegue`

- [ ] **T6.1 Reconocimiento de MonsterASP.NET y script de BD de producción** (M)
  - Acceptance: confirmo (con tus datos de acceso) versión de runtime .NET, límites reales y forma de ejecutar scripts; `00_crear_bd.sql` separado de los scripts idempotentes; plan de carga de datos que quepa en 1 GB.
  - Verify: scripts corren sin error dos veces contra la BD remota; el tamaño de datos queda < 1 GB
  - Files: `database/*.sql`, `desplegar.ps1` (modo `-Remoto`), `docs/despliegue.md`
  - Deps: Checkpoint E · **Requiere que tú proporciones credenciales; yo no las introduzco en formularios ni las guardo en el repo**

- [ ] **T6.2 Publicar la aplicación** (M)
  - Acceptance: `dotnet publish` generado; cadena de conexión y secretos fuera del repo (variables de entorno / `appsettings.Production.json` ignorado por git); app accesible por HTTPS; login funciona.
  - Verify: `read_network_requests` / navegador contra la URL pública
  - Files: `panel/appsettings.Production.example.json`, `.gitignore`, `docs/despliegue.md`
  - Deps: T6.1

- [ ] **T6.3 Prueba de carga y concurrencia en producción** (S)
  - Acceptance: 20 usuarios simultáneos sin errores 5xx; reserva del último cupo → 1 aceptada; dashboard < 2 s; resultados documentados (incluyendo límites del plan gratuito).
  - Verify: `tests/concurrencia.ps1 -Url <prod>` + medición
  - Files: `tests/carga.ps1`, `docs/resultados-pruebas.md`
  - Deps: T6.2

- [ ] **T6.4 Manuales y cierre** (S)
  - Acceptance: README actualizado a v2.0 (23 tablas, roles, cómo correr/desplegar); manual de usuario por rol y manual de recuperación (RNF-08); SPEC.md actualizado con las decisiones finales.
  - Verify: otra persona sigue el README y levanta todo en limpio
  - Files: `README.md`, `docs/manual-usuario.md`, `docs/recuperacion.md`, `SPEC.md`
  - Deps: T6.3

### Checkpoint final
- [ ] Criterios de éxito 1–8 de SPEC.md cumplidos y evidenciados; listo para entrega el 23/10/2026.
