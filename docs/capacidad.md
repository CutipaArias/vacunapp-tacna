# Prueba de capacidad (03/10/2026)

Objetivo: comprobar que el volumen del proyecto cabe en los límites del plan gratuito de MonsterASP.NET
(256 MB de RAM, 1 GB de base de datos). Supuestos: los 256 MB se aplican al proceso de la aplicación y SQL Server
corre en un servidor aparte; ambos supuestos deben confirmarse con el panel del hosting.

## Volumen cargado
| Tabla | Filas |
|---|---|
| Paciente | 20 000 |
| DosisAplicada | 309 393 |
| Alerta | 33 151 |
| StockLote / MovimientoStock | 2 040 / 2 048 |

## Tamaño de la base (SQL Server 2022, `sp_spaceused`)
| Concepto | Tamaño |
|---|---|
| Datos reservados (tablas + índices) | 57 MB (datos 20 MB, índices 32 MB) |
| Archivo de datos (.mdf) | 72 MB |
| Archivo de registro (.ldf) | 200 MB (tamaño inicial del contenedor local, casi vacío) |
| Total en disco | 272 MB (27 % de 1 GB) |
| Mayor tabla: DosisAplicada | 39 MB para 309 393 filas (≈ 0,13 KB por dosis) |

Extrapolación: con el registro en 200 MB, la parte de datos puede crecer a ~800 MB, es decir ~14 veces el volumen actual
(≈ 4 millones de dosis) antes de llegar al límite. Si el hosting cuenta el registro de transacciones, conviene
`ALTER DATABASE … SET RECOVERY SIMPLE` y un archivo de registro pequeño (local está en FULL).

## Memoria de la aplicación bajo carga
Compilación Release, 10 usuarios concurrentes durante 90 s (4 epidemiólogos con el tablero regional, 4 vacunadores
con carné y altas, 2 jefes con stock): 85 651 solicitudes, ≈ 950 por segundo.

| Métrica | Valor |
|---|---|
| Memoria de trabajo al iniciar | 71 MB |
| Memoria de trabajo media | 121 MB |
| **Memoria de trabajo pico** | **145 MB** (57 % de 256 MB) |
| Memoria privada pico | 82 MB |

## Tiempos de respuesta bajo esa carga
| Operación | p50 | p95 |
|---|---|---|
| Carné por DNI | 5 ms | 11 ms |
| Stock del jefe / alertas de stock | 3 ms / 8 ms | 9 ms / 17 ms |
| Resumen / sarampión | 262 / 252 ms | 473 / 342 ms |
| Alertas / campañas | 566 / 541 ms | 727 / 1 004 ms |
| Cobertura por distrito | 1 061 ms | 1 476 ms |

Los errores de la corrida (7 558) fueron altas de prueba rechazadas a propósito por la base (documento duplicado al
repetirse DNI aleatorios y dosis sin stock en el lote usado); ninguna lectura falló.

## Fuera de la medición de carga
`GET /api/pendientes` (`vac.usp_ListarPendientes`) no se incluyó en la carga concurrente. Medido después, de forma
aislada, con los mismos 20 000 pacientes (corrida repetida, base en caliente; el valor de 15 a 40 s que figuraba antes
no se reprodujo):

| Llamada | Tiempo |
|---|---|
| Global (`@Top = 200`) | ≈ 0,5 s |
| Por distrito / por vacuna | ≈ 0,4 s |
| `@SoloZonaBrote = 1` | ≈ 4,3 s (medición inicial; **no se reproduce**, ≈ 0,5 s en la revisión de T5.1: ver abajo) |

El último caso figuraba como **superior a la meta de 2 s (RNF-01)**. El tiempo se atribuyó a `fn_PendientesZonaBrote`,
no al índice de dosis: se probó cambiar `IX_Dosis_Esquema` a `(IdEsquema, IdPaciente)` y no mejoró nada medible (se
descartó).

**Revisión en T5.1 (03/10/2026):** la cifra de 4,3 s no se reproduce. Con los mismos 20 000 pacientes, tres brotes
activos y la base en caliente, `usp_ListarPendientes @SoloZonaBrote = 1, @Top = 200` tarda ≈ 0,48 s (480, 472 y 503 ms),
≈ 0,12–0,46 s por distrito, ≈ 0,46 s por vacuna y ≈ 0,57 s sin límite de filas; `fn_PendientesZonaBrote` sola, 37 ms
(4 030 filas). Tampoco cambia con `SET ARITHABORT OFF` (la configuración de la aplicación .NET). No se reescribió la
función: sin un caso lento reproducible sería optimizar a ciegas. El caso H47 de `database/07_pruebas.sql` mide este
llamado y falla si supera 2 s; corre también en MonsterASP con `desplegar-remoto.ps1 -Pruebas`, que es donde
interesa saber si el hosting compartido cambia el resultado.

## Reproducir
Generador de carga temporal (no incluido en el repositorio): inicia sesión con los usuarios semilla y repite las consultas
anteriores mientras mide `WorkingSet64` del proceso de la aplicación cada 0,5 s.
