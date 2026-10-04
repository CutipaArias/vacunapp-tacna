# VacunApp Tacna v2.0: registro, control y vigilancia de la vacunación ante el brote de sarampión

Curso SI783 Base de Datos II, Escuela Profesional de Ingeniería de Sistemas, UPT. Autores: Brayan Cutipa Arias y Raul Miranda Platas. Docente: Ing. Patrick Cuadros Quiroga.

## Problema

Entre 2025 y 2026, Tacna registró un brote de sarampión: la DIRESA confirmó primero [4 casos](https://radiouno.pe/noticias/277640/tacna-confirma-cuatro-casos-de-sarampion-y-refuerza-acciones-de-bloqueo-y-vacunacion) y luego [9 casos](https://radiouno.pe/noticias/279686/confirman-9-casos-de-sarampion-en-tacna-ningun-paciente-contaba-con-vacuna), señalando que ningún paciente tenía vacuna registrada. La DIRESA respondió con una [semana de intensificación de vacunación](https://radiouno.pe/noticias/265555/diresa-tacna-lanza-semana-de-intensificacion-de-vacunacion-para-proteger-a-los-ninos) y luego reforzó las [acciones de bloqueo tras la alerta sanitaria](https://radiouno.pe/noticias/273174/direccion-regional-de-salud-de-tacna-refuerza-vacunacion-tras-alerta-sanitaria-por-brote-de-sarampion). Pese a estas campañas, no existe un sistema que permita saber en tiempo real quién está vacunado, con qué dosis y en qué distrito, lo que dificulta priorizar campañas y detectar a tiempo a la población en riesgo.

## Solución

**VacunApp Tacna** es una base de datos SQL Server con las reglas de negocio dentro del motor (restricciones, triggers y procedimientos almacenados) y una aplicación web que la usa con cinco roles:

| Rol | Qué hace |
|---|---|
| Ciudadano | Consulta el carné de los niños vinculados a su cuenta y reserva, reprograma o cancela citas |
| Vacunador | Registra pacientes y dosis, atiende las citas del día y marca inasistencias; consulta carnés, alertas y pendientes del distrito de su establecimiento |
| Jefe de establecimiento | Define horarios y cupos, gestiona el stock (ingresos, ajustes, alertas), administra las citas de su establecimiento y consulta carnés, alertas y pendientes de su distrito |
| Epidemiólogo | Tablero de cobertura con semáforos, riesgo de sarampión, brotes, campañas, alertas y pendientes de toda la región |
| Administrador | Lo del epidemiólogo, más usuarios y roles, y auditoría de dosis |

Una dosis inválida (edad, orden, intervalo, lote vencido o de otra vacuna, vacunador de otro establecimiento, sin stock) se rechaza en la base, venga de la aplicación o de un `INSERT` directo. Los datos de pacientes, establecimientos y dosis son **simulados** (Ley N.° 29733); los distritos (ubigeo INEI), el esquema de vacunación (NTS N.° 196-MINSA) y los 9 casos del brote se basan en fuentes reales.

## Resultados

| Objetivo | Meta | Resultado (03/10/2026) |
|---|---|---|
| Modelo normalizado | 3FN | 23 tablas, 36 claves foráneas, 31 restricciones CHECK y 14 únicas ([diagrama E-R](docs/img/er.png)) |
| Lógica en el motor | Procedimientos y triggers | 24 procedimientos, 9 triggers, 8 vistas y 4 funciones |
| Rendimiento | Cobertura por distrito en < 2 s | 398–414 ms con 20 000 pacientes y unas 308 000 dosis; el tablero completo responde en 62 ms (reporte) y 168 ms (sarampión) |
| Reglas de concurrencia | Con dos reservas del último cupo, una sola se acepta | 22 escenarios con 2, 10 y 20 sesiones, sin sobrecupo ni interbloqueos |
| Pruebas | Todas en verde | 144 casos SQL, 337 pruebas xUnit de la API, 32 de carga de producción y 51 de paquete publicado |
| Despliegue | Scripts repetibles | Dos despliegues limpios seguidos sin errores; carga de producción en 2 s |

Detalle, métodos y límites en [`docs/cierre-tecnico.md`](docs/cierre-tecnico.md).

## Cómo ejecutarlo en su equipo

Requisitos: [Docker Desktop](https://www.docker.com/products/docker-desktop/), [.NET 10 SDK](https://dotnet.microsoft.com/download) y PowerShell.

```powershell
# 0. Cree .env a partir de .env.example y elija su contraseña de SQL Server
# 1. Levanta SQL Server 2022 en Docker y crea la base con datos de prueba y las pruebas SQL (menos de 1 minuto)
./desplegar.ps1 -Pruebas

# 2. Copie panel/appsettings.Development.example.json a panel/appsettings.Development.json,
#    complete la cadena de conexión y agregue en "Seed" las claves de las cuentas de demostración
#    (Seed:AdminPassword para admin y Seed:Password para las demás; mínimo 10 caracteres con mayúscula,
#    minúscula, dígito y símbolo). Nada de eso se versiona.
dotnet run --project panel          # http://localhost:5198
```

Las cuentas de demostración son `admin`, `epi01`, `jefe01`, `vac01` y `ciud01`; sin claves en la configuración quedan sin acceso. Los pasos de cada rol están en el [manual de usuario](docs/manual-usuario.md).

Conexión directa (SSMS o Azure Data Studio): servidor `localhost,1433`, usuario `sa` y la contraseña de `MSSQL_SA_PASSWORD` (archivo `.env`, no se versiona).

```sql
EXEC vac.usp_ReporteCoberturaDistrito @CodigoVacuna = 'SPR', @NumeroDosis = 1;
EXEC vac.usp_ListarPendientes @Ubigeo = '230104', @SoloZonaBrote = 1;
EXEC vac.usp_HistorialPaciente '70000001';
SELECT * FROM vac.vw_CoberturaSarampion ORDER BY CoberturaSPR1;
```

### Pruebas

```powershell
./desplegar.ps1 -Pruebas                       # 144 casos SQL
dotnet test tests/VacunApp.Tests               # 337 pruebas de la API (necesita la variable VACUNAPP_TEST_CONNECTION)
./tests/concurrencia.ps1                       # 22 escenarios de reservas simultáneas (unos 4 minutos)
./tests/produccion.ps1                         # carga en modo producción sobre una base temporal
./tests/publicacion.ps1                        # paquete publicable y arranque en modo Production
```

El procedimiento manual de cada función clave está en [`docs/manual-pruebas.md`](docs/manual-pruebas.md). Las pruebas xUnit reasignan las claves de las cuentas semilla en la base de desarrollo; ahí también se explica cómo volver a ellas.

## Producción (MonsterASP.NET)

La base de producción no lleva datos de prueba ni cuentas de demostración: queda solo el esquema, los catálogos y el usuario `admin` sin clave.

```powershell
./scripts/desplegar-remoto.ps1 -Produccion     # carga la base remota (pide servidor, base y usuario)
./scripts/publicar.ps1 -Zip                    # genera publicar/panel y su ZIP, sin configuración local
```

La configuración (cadena de conexión, clave del administrador, entorno) va en variables de entorno del hosting, no en archivos. Pasos completos, límites del plan gratuito y solución de problemas: [`docs/despliegue-monsterasp.md`](docs/despliegue-monsterasp.md).

## Estructura

```text
database/
  01_esquema.sql          tablas, restricciones e índices
  02_catalogos.sql        provincias, 28 distritos, vacunas, esquema, establecimientos y vacunadores (simulados)
  03_vistas.sql           funciones y vistas de reporte
  04_procedimientos.sql   registro, brotes, alertas, cobertura y pendientes
  05_triggers.sql         validación de dosis, alertas y auditoría
  06_datos_prueba.sql     20 000 pacientes y sus dosis (simulados)
  07_pruebas.sql          pruebas funcionales y de rendimiento
  08_seguridad.sql        roles, usuarios, vínculos familiares
  09_stock.sql            lotes, existencias, movimientos y alertas de stock
  10_agenda.sql           horarios, cupos y citas; campañas con metas
  11_estadisticas.sql     estadísticas tras la carga masiva (estabiliza los planes)
  12_produccion.sql       limpia lo de demostración para la base real
panel/                    API Minimal .NET 10 (Auth, Pacientes, Agenda, Stock, Vigilancia) y páginas web
scripts/                  desplegar-remoto.ps1 (base en el hosting) y publicar.ps1 (paquete del panel)
tests/                    VacunApp.Tests (xUnit) y scripts de concurrencia, producción y publicación
tasks/                    plan.md y todo.md (plan de trabajo y estado)
docs/                     manuales, cierre técnico, despliegue, seguridad y capacidad
docs/diagramas/ docs/img/ fuentes Mermaid y PNG de los diagramas
docker-compose.yml        contenedor de SQL Server
desplegar.ps1             despliegue local completo
SPEC.md                   especificación y decisiones de diseño (§12)
FD01…FD06-*.md / .docx    informes del proyecto (formato EPIS)
```

## Objetos principales de la base de datos

| Tipo | Objeto | Función |
|---|---|---|
| Procedimiento | `usp_RegistrarPaciente`, `usp_RegistrarDosis` | Registro validado de pacientes y dosis |
| Procedimiento | `usp_DeclararBrote`, `usp_CerrarBrote` | Abre o cierra un brote por enfermedad y distrito |
| Procedimiento | `usp_ReporteCoberturaDistrito` | Cobertura por distrito, vacuna y dosis, con semáforo (95 % y 80 %) |
| Procedimiento | `usp_ListarPendientes`, `usp_HistorialPaciente` | Dosis pendientes y carné |
| Procedimiento | `usp_GenerarAlertasAtrasadas`, `usp_GenerarAlertasStock`, `usp_AtenderAlerta` | Alertas de dosis atrasadas, de stock y su atención |
| Procedimiento | `usp_ReservarCita`, `usp_ReprogramarCita`, `usp_CancelarCita`, `usp_AtenderCita`, `usp_RegistrarInasistencia` | Citas con cupo y control de concurrencia |
| Procedimiento | `usp_CrearHorario`, `usp_ActualizarHorario`, `usp_IngresarLote`, `usp_AjustarStock` | Franjas, lotes y existencias |
| Procedimiento | `usp_CrearCampana`, `usp_CerrarCampana` | Campañas con metas por distrito |
| Procedimiento | `usp_CrearUsuario`, `usp_ActualizarUsuario`, `usp_ValidarAsignacionUsuario` | Usuarios, roles y asignación a establecimiento |
| Trigger | `trg_DosisAplicada_Validar` | Rechaza dosis fuera de edad, sin la dosis previa, con lote vencido o de otra vacuna, o con vacunador ajeno |
| Trigger | `trg_DosisAplicada_DescontarStock`, `trg_StockLote_Alerta` | Descuenta una unidad por dosis y alerta al llegar al umbral |
| Trigger | `trg_DosisAplicada_AtenderAlertas`, `trg_DosisAplicada_Auditoria` | Cierra las alertas al aplicar la dosis; guarda en JSON toda corrección o eliminación |
| Trigger | `trg_Brote_GenerarAlertas`, `trg_Paciente_AlertaZonaBrote` | Alertas al declarar un brote o al mudar a un niño a una zona de brote |
| Trigger | `trg_Cita_Validar`, `trg_Vinculo_SoloCiudadano` | Revalidan cupo y elegibilidad; solo un ciudadano se vincula a pacientes |
| Vista | `vw_CoberturaDistrito`, `vw_CoberturaSarampion`, `vw_DosisPendientes`, `vw_AlertasPendientes`, `vw_AvanceCampana`, `vw_HistorialVacunacion`, `vw_ResumenGeneral`, `vw_AuditoriaDosis` | Reportes |
| Función | `fn_EdadMeses`, `fn_DosisPendientes`, `fn_PendientesZonaBrote`, `fn_CitaElegible` | Edad, pendientes, zona de brote y elegibilidad de cita |

## Documentación

| Documento | Contenido |
|---|---|
| [Manual de usuario](docs/manual-usuario.md) | Uso por rol |
| [Manual de pruebas](docs/manual-pruebas.md) | Cinco funciones clave: pasos, resultado esperado y prueba automática |
| [Cierre técnico](docs/cierre-tecnico.md) | Alcance, requisitos con cifras medidas, decisiones, límites conocidos y pendientes |
| [Despliegue en MonsterASP.NET](docs/despliegue-monsterasp.md) | Carga de la base, paquete, variables de entorno y verificaciones |
| [Seguridad](docs/seguridad.md) | Revisión de seguridad |
| [Capacidad](docs/capacidad.md) | Mediciones con 20 000 pacientes y el límite del plan gratuito |
| [SPEC](SPEC.md) | Especificación y decisiones de diseño |

| Informe | Markdown | Word |
|---|---|---|
| FD01 Informe de Factibilidad | [md](FD01-Informe-Factibilidad.md) | [docx](FD01-EPIS-Informe%20de%20Factibilidad.docx) |
| FD02 Documento de Visión | [md](FD02-Informe-Vision.md) | [docx](FD02-EPIS-Informe%20Vision.docx) |
| FD03 Especificación de Requerimientos | [md](FD03-Informe-SRS.md) | [docx](FD03-EPIS-Informe%20Especificación%20Requerimientos.docx) |
| FD04 Arquitectura de Software | [md](FD04-Informe-SAD.md) | [docx](FD04-EPIS-Informe%20Arquitectura%20de%20Software.docx) |
| FD05 Informe Final | [md](FD05-Informe-Final.md) | [docx](FD05-EPIS-Informe%20ProyectoFinal.docx) |
| FD06 Propuesta del Proyecto | [md](FD06-Propuesta-Proyecto.md) | [docx](FD06-EPIS-PropuestaProyecto.docx) |
