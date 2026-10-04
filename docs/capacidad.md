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

## Pendientes (`usp_ListarPendientes`): incidente y corrección (03/10/2026)
`GET /api/pendientes` no se incluyó en la carga concurrente. Esta sección **corrige** lo que figuraba antes: una versión
anterior afirmaba que el caso lento "no se reproducía" (≈ 0,5 s). Esa medición se hizo sobre una base ya en estado
favorable y era incorrecta. Medido desde una base recién desplegada con `desplegar.ps1`, 20 000 pacientes, tres brotes
activos y `ARITHABORT OFF` (la configuración de la aplicación):

| Llamada (`@Top = 200`) | Despliegue limpio, sin pruebas | Tras `07_pruebas.sql` (antes de la corrección) |
|---|---|---|
| Global | 1,2 s la primera vez, 0,5 s después | **301 s** |
| Por distrito (230104) | 0,45 s | **30 s** |
| Por vacuna (SPR) | 0,38 s | **303 s** |
| `@SoloZonaBrote = 1` | **4,3 s** (primera y segunda corrida) | **49 s** (49 575 y 49 081 ms; 47 588 ms en la revisión manual) |

El caso H47 (zona de brote, meta < 2 s de RNF-01) falló en cada despliegue con pruebas (118/119). La pestaña
Pendientes era lenta en general por la misma causa.

**Causa confirmada.** No es caché frío (los tiempos se repiten en corridas sucesivas) ni se pudo atribuir a *parameter
sniffing* (actualizar estadísticas invalida el plan, así que no se separan). Es un plan frágil:
1. Después de la carga masiva y de las pruebas, las estadísticas de `vac.Brote` conservan **1 fila (hay 3)**.
   Actualizar solo esa tabla (`UPDATE STATISTICS vac.Brote`) bajó la zona de brote de 49 s a 4,4 s;
   actualizar `Paciente`, `DosisAplicada`, `Distrito` o `EsquemaDosis` no cambió nada.
2. Con esas estimaciones el optimizador resolvía el `NOT EXISTS` de `fn_DosisPendientes` con un **merge join
   muchos-a-muchos** sobre `IdEsquema` (estimaba 714 filas donde había 46 475): ≈ 51 s dentro de ese único operador.
3. Aun con estadísticas frescas, la zona de brote tardaba 4,4 s porque `fn_DosisPendientes` se evaluaba dos veces (una
   para la lista y otra, correlacionada, dentro del `EXISTS` por cada fila pendiente).
4. Las variantes sin brote sufrían el mismo merge join en el estado posterior a las pruebas (30–303 s), aunque
   actualizar estadísticas de las tablas no las arreglaba: la elección del plan es inestable, no solo un tema de estadísticas.

**Corrección** (`database/04_procedimientos.sql`, sin cambios de esquema ni de funciones):
- Zona de brote: los pacientes se materializan una vez en una tabla temporal con clave primaria
  (`fn_PendientesZonaBrote`) y la lista se filtra contra ella. Misma firma, columnas y orden.
- Todas las variantes: `OPTION (LOOP JOIN, HASH JOIN)`, que prohíbe el merge join. En la consulta por distrito
  bajó de 30 s a 90 ms con ese solo cambio (probado también `FORCE_LEGACY_CARDINALITY_ESTIMATION`: 82 ms; se
  prefirió el hint de unión, que no cambia las estimaciones de otras consultas).
- `database/11_estadisticas.sql` (`sp_updatestats`) se ejecuta al final de la carga, antes de `07_pruebas.sql`,
  en `desplegar.ps1` y `desplegar-remoto.ps1`. Es defensa adicional: **la corrección no depende de ella**
  (se verificó desplegando sin ese script).

**Verificación.**
- Equivalencia: con 20 000 pacientes, 9 574 filas en zona de brote; `EXCEPT` en ambos sentidos entre la versión
  anterior y la nueva = 0 filas (experimento) y casos H52/H53 en `07_pruebas.sql` (mismo conjunto con `@Top` grande
  y las primeras 200 filas en el mismo orden respecto de la regla).
- Casos H54–H56 (lista general, distrito y vacuna < 2 s): en rojo antes de la corrección (301, 30 y 303 s).
- `07_pruebas.sql`: **124/124** desde un despliegue limpio con `11_estadisticas.sql` y **124/124** sin él.
- Tiempos tras la corrección (estado posterior a las pruebas, sin actualizar estadísticas): zona de brote 0,27–0,30 s;
  distrito 0,08 s; vacuna 0,5–0,6 s; global 0,7 s.
- 10 llamadas simultáneas con `@SoloZonaBrote = 1`: sin errores; entre 0,28 y 1,1 s cada una (tabla temporal por sesión).

**Nota para T6 (MonsterASP).** El despliegue remoto replica la situación de riesgo: carga masiva reciente sobre
tablas pequeñas con estadísticas viejas. `desplegar-remoto.ps1` ya ejecuta `11_estadisticas.sql`, pero tras la carga
en MonsterASP hay que (1) comprobar que `sp_updatestats` tuvo permiso y (2) volver a medir las cuatro variantes de
`usp_ListarPendientes` y el caso H47 con `-Pruebas`. Si el hosting compartido no admitiera `sp_updatestats`, la
corrección del procedimiento sigue valiendo, pero hay que anotarlo.

## Reproducir
Generador de carga temporal (no incluido en el repositorio): inicia sesión con los usuarios semilla y repite las consultas
anteriores mientras mide `WorkingSet64` del proceso de la aplicación cada 0,5 s.
