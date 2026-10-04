# Implementation Plan: VacunApp Tacna v2.0

Spec: [SPEC.md](../SPEC.md) (aprobado). Fecha límite: **23/10/2026**. Tareas en [tasks/todo.md](todo.md).

## Overview
Evolucionar VacunApp v1 (16 tablas, panel de solo consulta) a v2 (23 tablas, 5 roles, citas con cupos, stock, brotes/alertas, dashboard, despliegue en MonsterASP.NET). Se avanza por módulos en el orden del Capability Map; cada módulo se entrega en **rebanadas verticales** (script SQL + endpoint + UI + prueba).

## Estado actual relevante (verificado en el repo)
- `database/01..07.sql` se ejecutan en orden con `desplegar.ps1` (sqlcmd dentro del contenedor `vacunapp-sql`). No existe carpeta `tests/` ni `tasks/`.
- `panel/Program.cs` (138 líneas): Minimal API sin autenticación, `Db` helper con `SqlParameter`, middleware que convierte `THROW 50000+` en 400. UI en `panel/wwwroot/index.html`.
- `PersonalSalud`/`IdPersonal` se usa en ~25 lugares de 02–07 y en `DosisAplicada` (FK) → el renombrado a `Vacunador` es el cambio más riesgoso.
- Faltan por crear 7 tablas: `Rol`, `Usuario`, `VinculoFamiliar`, `HorarioAtencion`, `Cita`, `StockLote`, `MovimientoStock` (16 − PersonalSalud + Vacunador = 16; +7 = 23).

## Decisiones de arquitectura
1. **Scripts nuevos en vez de reescribir los viejos cuando sea posible**: `08_seguridad.sql`, `09_stock.sql`, `10_agenda.sql`; los cambios a tablas existentes (renombrar `PersonalSalud`) se hacen en `01_esquema.sql` porque `desplegar.ps1` recrea la base desde cero. *Pregunta resuelta por ser BD de demo con datos simulados regenerables.*
2. **Idempotencia**: `CREATE OR ALTER` para código; para tablas, `IF OBJECT_ID(...) IS NULL`. El script de despliegue a MonsterASP.NET **no** hace DROP DATABASE (la base ya existe allí) — se separa `00_crear_bd.sql` (solo local).
3. **Contraseñas**: `PasswordHasher<T>` de ASP.NET Core (PBKDF2) en la API; la tabla guarda solo el hash (`ClaveHash`). Sin sal propia ni algoritmo manual.
4. **Autorización**: cookie auth + claims `rol` e `idEstablecimiento`; políticas por rol; un helper `Alcance` aplica RN-22 (vacunador/jefe → su establecimiento) en cada endpoint. La BD sigue validando reglas de negocio aunque la API falle.
5. **Concurrencia**: `usp_ReservarCita` con transacción + `UPDLOCK, HOLDLOCK` sobre la franja; `trg_Cita_Validar` revalida el cupo.
6. **Pruebas**: BD → `07_pruebas.sql` ampliado (un caso positivo y negativo por RN). API → xUnit con `WebApplicationFactory` contra el SQL Server de Docker. Concurrencia → `tests/concurrencia.ps1`.
7. **UI**: HTML/JS plano, una página por rol bajo `wwwroot/`, `app.js` compartido, Chart.js por CDN solo en el dashboard.
8. **Despliegue**: la publicación a MonsterASP.NET requiere credenciales (FTP/Web Deploy y cadena de conexión). **Yo preparo scripts, `appsettings.Production`, pasos y verificación; las credenciales las introduces tú** (no las manejo yo). 

## Dependencias entre módulos
```
identidad ─► registro ─► stock ─► agenda ─► vigilancia ─► despliegue
   (Rol, Usuario)   (Vacunador,    (Lote,     (Horario,    (Brote, Alerta,   (MonsterASP,
                     Dosis)        Stock)      Cita)        Dashboard)        manuales)
```
`agenda` depende de `stock` porque atender una cita registra una dosis que descuenta stock.

## Fases y checkpoints
- **Fase 0 – Cimientos** (T0.1–T0.2): estructura de pruebas y migración `PersonalSalud→Vacunador`. Riesgo alto, va primero.
- **Fase 1 – identidad** (T1.1–T1.5) → *Checkpoint A*
- **Fase 2 – registro** (T2.1–T2.4) → *Checkpoint B*
- **Fase 3 – stock** (T3.1–T3.3) → *Checkpoint C*
- **Fase 4 – agenda** (T4.1–T4.5) → *Checkpoint E*
- **Fase 5 – vigilancia** (T5.1–T5.4) → *Checkpoint F*
- **Fase 6 – despliegue** (T6.1–T6.4) → *Checkpoint final*

Cada checkpoint: `./desplegar.ps1 -Pruebas` verde, `dotnet build` y `dotnet test` verdes, revisión contigo antes de seguir.

## Cronograma orientativo (hoy 03/10 → 23/10)
| Fechas | Fases |
|---|---|
| 03–06 oct | Fase 0 + Fase 1 (identidad con security-and-hardening) |
| 07–10 oct | Fase 2 + Fase 3 |
| 11–15 oct | Fase 4 (agenda y concurrencia) |
| 16–19 oct | Fase 5 |
| 20–22 oct | Fase 6 (despliegue, prueba de carga, manuales) |
| 23 oct | Colchón / entrega |

## Riesgos y mitigaciones
| Riesgo | Impacto | Mitigación |
|---|---|---|
| Renombrar `PersonalSalud` rompe SP/triggers/datos de prueba (25 referencias) | Alto | T0.2 aislada, con búsqueda exhaustiva y `07_pruebas.sql` como red de seguridad antes y después |
| Concurrencia mal resuelta (sobrecupo) | Alto | Prueba automatizada de 2+ sesiones desde T4.2; HOLDLOCK + revalidación en trigger |
| Plan gratuito MonsterASP.NET (256 MB RAM, 1 GB BD) no soporta 20 000 pacientes / 300 000 dosis | Alto | Probar carga de datos temprano en T6.1; plan B: reducir el volumen de datos sembrados en prod |
| MonsterASP.NET sin runtime .NET 10 o sin sqlcmd remoto | Medio | Verificar en T6.1 antes de dejar el despliegue para el final; permitir ejecutar scripts vía SSMS |
| Autorización por establecimiento olvidada en algún endpoint | Alto | Helper central `Alcance` + test xUnit por endpoint (403) |
| Umbrales de negocio son supuestos del equipo | Bajo | Parametrizados en BD; documentados |
| `06_datos_prueba.sql` lento al agregar usuarios/citas/stock | Medio | Sembrar usuarios, horarios y stock en script aparte; no tocar la generación masiva |

## Preguntas abiertas (no bloquean el inicio)
- Datos de acceso/URL de MonsterASP.NET: los necesito recién en T6.1.
- Usuarios semilla por rol: propongo `admin`, `epi01`, `jefe01`, `vac01`, `ciud01` con contraseña de demo que se muestra solo en el manual de pruebas (¿OK?).
