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
- **Auditoría con usuario de aplicación (RN-23):** toda operación que corrija o elimine dosis fija antes `sp_set_session_context N'usuario'` con el usuario de la sesión, para que `trg_DosisAplicada_Auditoria` registre quién la hizo (si no, cae a `SUSER_SNAME()`).
