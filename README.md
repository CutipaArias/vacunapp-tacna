# VacunApp Tacna: Base de Datos para el Registro y Control de Vacunación ante el Brote de Sarampión

## Problema

Entre 2025 y 2026, Tacna registró un brote de sarampión: la DIRESA confirmó primero [4 casos](https://radiouno.pe/noticias/277640/tacna-confirma-cuatro-casos-de-sarampion-y-refuerza-acciones-de-bloqueo-y-vacunacion) y luego [9 casos](https://radiouno.pe/noticias/279686/confirman-9-casos-de-sarampion-en-tacna-ningun-paciente-contaba-con-vacuna), señalando que ningún paciente tenía vacuna registrada. La DIRESA respondió con una [semana de intensificación de vacunación](https://radiouno.pe/noticias/265555/diresa-tacna-lanza-semana-de-intensificacion-de-vacunacion-para-proteger-a-los-ninos) y luego reforzó las [acciones de bloqueo tras la alerta sanitaria](https://radiouno.pe/noticias/273174/direccion-regional-de-salud-de-tacna-refuerza-vacunacion-tras-alerta-sanitaria-por-brote-de-sarampion). Pese a estas campañas, no existe un sistema que permita saber en tiempo real quién está vacunado, con qué dosis y en qué distrito, lo que dificulta priorizar campañas y detectar a tiempo a la población en riesgo.

## Solución propuesta

**VacunApp Tacna**: base de datos relacional con procedimientos almacenados, triggers y vistas que centralizan el registro de dosis aplicadas y generan reportes de cobertura por distrito y alertas de pacientes con dosis pendiente, expuestos mediante una interfaz mínima de consulta (sin necesidad de una aplicación web completa).

## Objetivos

- **General:** diseñar e implementar una base de datos relacional normalizada (3FN), con procedimientos almacenados, triggers y vistas, que centralice el registro de vacunación de Tacna y genere reportes de cobertura para apoyar la respuesta ante brotes como el de sarampión.
- Modelar y normalizar la base de datos (pacientes, dosis, campañas, centros de salud) reduciendo a 0% las columnas redundantes, verificable con el diagrama entidad-relación.
- Implementar al menos 5 procedimientos almacenados y 2 triggers (ej. alerta automática cuando un paciente en zona de brote tiene una dosis pendiente).
- Crear vistas/consultas que generen el reporte de cobertura de vacunación por distrito, con tiempo de respuesta menor a 2 segundos sobre 5,000+ registros simulados.
- Exponer estos reportes mediante una interfaz mínima de consulta (script o panel simple), sin necesidad de desarrollar una aplicación web completa.

## Resultados

| Objetivo | Meta | Resultado |
|---|---|---|
| Modelo normalizado | 3FN, 0 % de columnas redundantes | 16 tablas en 3FN ([diagrama E-R](docs/img/er.png)) |
| Lógica en el motor | ≥ 5 procedimientos y ≥ 2 triggers | 9 procedimientos, 5 triggers, 7 vistas y 3 funciones |
| Rendimiento | Cobertura por distrito en < 2 s con 5 000+ registros | 451 ms con 20 000 pacientes y 309 101 dosis |
| Interfaz mínima | Script o panel simple | Panel web en .NET 10 (`panel/`) |
| Pruebas | | 24/24 casos correctos (`database/07_pruebas.sql`) |

Los datos de pacientes, establecimientos y dosis son **simulados**. Los distritos (ubigeo INEI), el esquema de vacunación (NTS N.° 196-MINSA) y los 9 casos del brote se basan en fuentes reales.

## Cómo ejecutarlo

Requisitos: [Docker Desktop](https://www.docker.com/products/docker-desktop/), [.NET 10 SDK](https://dotnet.microsoft.com/download) y PowerShell.

```powershell
# 0. Cree .env a partir de .env.example y elija su contraseña de SQL Server
# 1. Levanta SQL Server 2022 en Docker y crea la base con datos de prueba (unos 30 s)
./desplegar.ps1 -Pruebas

# 2. Inicia el panel de consulta en http://localhost:5198
dotnet run --project panel
```

Conexión directa (SSMS o Azure Data Studio): servidor `localhost,1433`, usuario `sa` y la contraseña que definió en `MSSQL_SA_PASSWORD` (archivo `.env`, copiado de `.env.example`; no se versiona). El panel lee su cadena de conexión de `panel/appsettings.Development.json` (copie `appsettings.Development.example.json`).

```sql
EXEC vac.usp_ReporteCoberturaDistrito @CodigoVacuna = 'SPR', @NumeroDosis = 1;
EXEC vac.usp_ListarPendientes @Ubigeo = '230104', @SoloZonaBrote = 1;
EXEC vac.usp_HistorialPaciente '70000001';
SELECT * FROM vac.vw_CoberturaSarampion ORDER BY CoberturaSPR1;
```

## Estructura

```text
database/
  01_esquema.sql          16 tablas, restricciones e índices
  02_catalogos.sql        provincias, 28 distritos, vacunas, esquema, establecimientos, campañas
  03_vistas.sql           funciones y vistas de reporte
  04_procedimientos.sql   procedimientos almacenados
  05_triggers.sql         triggers
  06_datos_prueba.sql     20 000 pacientes y sus dosis (simulados)
  07_pruebas.sql          pruebas funcionales y de rendimiento
panel/                    API minimal .NET 10 + página web
docs/diagramas/           fuentes Mermaid de los diagramas
docs/img/                 diagramas en PNG
FD01…FD06-*.md / .docx    informes del proyecto (formato EPIS)
docker-compose.yml        contenedor de SQL Server
desplegar.ps1             despliegue completo
```

## Objetos de la base de datos

| Tipo | Objeto | Función |
|---|---|---|
| Procedimiento | `usp_RegistrarPaciente` | Registra un paciente validando documento, fecha y ubigeo |
| Procedimiento | `usp_RegistrarDosis` | Registra una dosis a partir de DNI, vacuna, dosis, lote y vacunador |
| Procedimiento | `usp_DeclararBrote` / `usp_CerrarBrote` | Abre o cierra un brote por enfermedad y distrito |
| Procedimiento | `usp_ReporteCoberturaDistrito` | Cobertura por distrito, vacuna y dosis, con clasificación |
| Procedimiento | `usp_ListarPendientes` | Niños con dosis pendiente (opcional: solo en zona de brote) |
| Procedimiento | `usp_HistorialPaciente` | Carné: datos, dosis aplicadas y pendientes |
| Procedimiento | `usp_GenerarAlertasAtrasadas` | Proceso por lotes de alertas de dosis atrasadas |
| Procedimiento | `usp_AtenderAlerta` | Marca una alerta como atendida o descartada |
| Trigger | `trg_DosisAplicada_Validar` | Rechaza dosis fuera de edad, sin la dosis previa, con lote vencido o de otra vacuna |
| Trigger | `trg_DosisAplicada_AtenderAlertas` | Cierra las alertas al registrar la dosis |
| Trigger | `trg_Brote_GenerarAlertas` | Al declarar un brote, alerta sobre cada niño del distrito con dosis pendiente |
| Trigger | `trg_Paciente_AlertaZonaBrote` | Alerta al registrar o mudar a un niño a una zona de brote |
| Trigger | `trg_DosisAplicada_Auditoria` | Guarda en JSON toda corrección o eliminación de una dosis |
| Vista | `vw_CoberturaDistrito`, `vw_CoberturaSarampion`, `vw_DosisPendientes`, `vw_AlertasPendientes`, `vw_AvanceCampana`, `vw_HistorialVacunacion`, `vw_ResumenGeneral` | Reportes |

## Documentación

| Documento | Markdown | Word |
|---|---|---|
| FD01 Informe de Factibilidad | [md](FD01-Informe-Factibilidad.md) | [docx](FD01-EPIS-Informe%20de%20Factibilidad.docx) |
| FD02 Documento de Visión | [md](FD02-Informe-Vision.md) | [docx](FD02-EPIS-Informe%20Vision.docx) |
| FD03 Especificación de Requerimientos | [md](FD03-Informe-SRS.md) | [docx](FD03-EPIS-Informe%20Especificación%20Requerimientos.docx) |
| FD04 Arquitectura de Software | [md](FD04-Informe-SAD.md) | [docx](FD04-EPIS-Informe%20Arquitectura%20de%20Software.docx) |
| FD05 Informe Final | [md](FD05-Informe-Final.md) | [docx](FD05-EPIS-Informe%20ProyectoFinal.docx) |
| FD06 Propuesta del Proyecto | [md](FD06-Propuesta-Proyecto.md) | [docx](FD06-EPIS-PropuestaProyecto.docx) |
