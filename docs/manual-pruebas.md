# Manual de pruebas – VacunApp Tacna v2.0

Cómo comprobar las cinco funciones clave del sistema, a mano y con las pruebas automáticas, y qué resultado debe salir. Las cifras son de la
ejecución del 03/10/2026 en Docker local (SQL Server 2022) con 20 000 pacientes simulados.

| # | Función clave | RF / RN / RNF | Criterio de éxito (SPEC §9) |
|---|---|---|---|
| 1 | Acceso por rol y alcance por establecimiento | RF-01, RF-02, RN-22, RNF-02 | 2 |
| 2 | Registro de dosis validado contra el esquema | RF-03, RF-04, RN-01…RN-09, RNF-04, RNF-05 | 3 |
| 3 | Reserva de cita con cupo y concurrencia | RF-05…RF-08, RN-14…RN-18, RNF-03 | 4 |
| 4 | Stock: descuento por dosis y alertas | RF-09, RN-10…RN-13 | 5 |
| 5 | Tablero de cobertura y vigilancia (brotes, alertas, campañas) | RF-10…RF-14, RN-19…RN-21, RNF-01 | 6 |

## 0. Preparación

Requisitos: Docker Desktop, .NET 10 SDK, PowerShell y `sqlcmd`.

```powershell
# 1. .env con MSSQL_SA_PASSWORD (copie .env.example). Nunca se versiona.
# 2. Base limpia con las pruebas SQL (unos 35 s):
./desplegar.ps1 -Pruebas
# 3. panel/appsettings.Development.json (copie el .example) con la cadena de conexión y, en "Seed",
#    AdminPassword y Password: son las claves de las cuentas de demostración; las elige usted y no se versionan.
dotnet run --project panel          # http://localhost:5198
```

Cuentas de demostración (se crean sin clave y la aplicación se la asigna al arrancar): `admin` (administrador), `epi01` (epidemiólogo),
`jefe01` (jefe), `vac01` (vacunador) y `ciud01` (ciudadano, vinculado a dos pacientes simulados). `admin` usa `Seed:AdminPassword`; las
demás, `Seed:Password`.

**Advertencia.** La suite xUnit (`dotnet test`) pone en NULL la clave de esas cuentas y les asigna las suyas. Si la corre contra la base de
desarrollo, las claves de `Seed:*` dejan de valer: para volver a ellas ejecute
`UPDATE vac.Usuario SET ClaveHash = NULL WHERE NombreUsuario IN ('admin','epi01','jefe01','vac01','ciud01');` y reinicie el panel. Haga las
pruebas manuales antes de la suite automática.

### Pruebas automáticas (resumen)

| Qué | Comando | Resultado esperado (03/10/2026) |
|---|---|---|
| Base: reglas, triggers y procedimientos | `./desplegar.ps1 -Pruebas` | 144 de 144 casos, dos despliegues seguidos (33 y 38 s) |
| API: autorización, reglas, mensajes | `dotnet test tests/VacunApp.Tests` | 337 de 337, unos 50 s |
| Concurrencia de reservas | `./tests/concurrencia.ps1` | 22 escenarios correctos (227 s) |
| Carga de producción | `./tests/produccion.ps1` | 32 de 32 |
| Paquete y arranque en Production | `./tests/publicacion.ps1` | 51 de 51 |

`dotnet test` necesita la variable `VACUNAPP_TEST_CONNECTION` (cadena de conexión a la base de desarrollo) o el archivo
`panel/appsettings.Development.json`.

## 1. Acceso por rol y alcance

**Manual**

1. Entre con cada cuenta y compare las pestañas con la tabla:

   | Cuenta | Pestañas esperadas |
   |---|---|
   | `admin` | Resumen, Tablero, Cobertura por distrito, Alertas, Pendientes, Brotes, Campañas, Paciente, Usuarios, Auditoría |
   | `epi01` | Resumen, Tablero, Cobertura por distrito, Alertas, Pendientes, Brotes, Campañas, Paciente |
   | `jefe01` | Alertas, Pendientes, Paciente, Citas, Stock, Horarios |
   | `vac01` | Alertas, Pendientes, Paciente, Citas, Citas del día |
   | `ciud01` | Paciente, Citas |

2. Con `vac01` abra en otra pestaña `/api/usuarios/`: la respuesta debe ser **403** (solo administrador). Cierre la sesión y repita: **401**.
3. Con `ciud01`, en **Paciente** solo deben aparecer dos botones (sus hijos vinculados). Buscar un DNI ajeno no es posible desde su pantalla.
4. Escriba 5 veces una clave incorrecta para `epi01`: el mensaje es siempre «Usuario o contraseña incorrectos.»; después, la clave correcta
   también se rechaza durante 15 minutos.
5. Con `admin`, en **Usuarios** cree un usuario de cada rol (jefe y vacunador piden establecimiento; el vacunador, además, el personal
   registrado en ese establecimiento) y pruebe una contraseña débil: «La contraseña debe tener entre 10 y 128 caracteres, con mayúscula,
   minúscula, número y símbolo.» Desactive la cuenta de `epi01`: ya no podrá entrar. En la fila de `admin` no hay botón para desactivarse.

**Resultado esperado:** cada rol ve solo lo suyo; el servidor rechaza lo demás aunque se escriba la dirección; ninguna respuesta de error revela
si la cuenta existe.

**Automático:** `AuthTests` (12), `AutorizacionTests` (24: 403 por rol y establecimiento, 401 sin sesión), `UsuariosTests` (18),
`SeguridadTests` (13: cabeceras, cookie `Secure` en producción, sin SQL concatenado, límite de intentos),
`RegistroAccesoFallidoTests` (2). SQL: casos S1…S17 de `07_pruebas.sql` (los cinco roles, cuentas semilla sin clave en claro, nombre repetido, jefe o
vacunador sin establecimiento, vínculo solo para ciudadanos, no dejar el sistema sin administrador).

## 2. Registro de dosis validado

**Manual (con `vac01`)**

1. **Paciente → Registrar paciente:** DNI `89990001`, nombres y apellido de prueba, nacimiento 20 meses atrás, distrito cualquiera.
   Resultado: «Paciente registrado. Se muestra su carné.»
2. Repita con el mismo DNI: «Ya existe un paciente con ese documento.» Pruebe un DNI de 7 dígitos: «El DNI debe tener 8 dígitos.»
3. En el carné, en **Registrar dosis** elija una dosis pendiente de SPR (1.ª dosis), un lote, su establecimiento y la fecha de hoy.
   Resultado: «Dosis registrada (Id …). Las alertas asociadas se cerraron automáticamente.» La dosis pasa a la tabla **Aplicada** y desaparece de
   **Pendiente**.
4. En **Stock** de `jefe01`, las existencias de ese lote bajaron en una unidad.
5. Las reglas de rechazo (edad, dosis previa, intervalo, lote vencido, lote de otra vacuna, vacunador de otro establecimiento, dosis
   duplicada) se comprueban en la base con los casos negativos de `07_pruebas.sql`: la interfaz solo ofrece lotes vigentes con existencias y dosis
   pendientes, así que no permite provocarlas con clics.

**Resultado esperado:** 0 dosis inválidas insertables por cualquier vía. Cada rechazo revierte toda la operación (RNF-05): sin dosis, sin descuento
de stock y sin alerta cerrada.

**Automático:** `DosisTests` (14), `PacientesTests` (18), `CarneTests` (15); `07_pruebas.sql` (144 casos, cada uno en su transacción con
ROLLBACK, así que no deja datos): N1…N11 (documento duplicado, ubigeo, fecha futura, 2.ª dosis sin la 1.ª, lote vencido, lote de otra vacuna, edad
insuficiente, vacunador de otro establecimiento o inactivo, dosis repetida, fuera del periodo de campaña), P2 (una dosis válida cierra su alerta) y
P3 (la corrección queda auditada).

## 3. Reserva de cita con cupo y concurrencia

**Manual**

1. Con `jefe01`, **Horarios → Nueva franja:** SPR, fecha dentro de 3 días, hora 09:00, **cupo 1** → «Franja creada.»
2. Registre dos pacientes nuevos con edad para SPR 1.ª dosis (como en la función 2, DNI `89990002` y `89990003`).
3. Abra **dos navegadores**: `vac01` en uno y `jefe01` en otro. En **Citas**, ambos buscan a un paciente distinto (`89990002` y `89990003`), eligen
   SPR y llegan al paso 3 con la misma franja visible («… · 1 cupo»).
4. El primero pulsa **Reservar cita**: «Cita reservada. Llegue 10 minutos antes con el DNI del paciente.»
5. El segundo pulsa **Reservar cita**: debe aparecer «La franja no tiene cupos disponibles.» y su lista de franjas se actualiza.
6. En el primero, cancele la cita (**Cancelar**, confirmar): «Cita cancelada. El cupo quedó libre.» El segundo ya puede reservar la franja.
7. Con una cita a menos de 24 horas (cree una franja para dentro de 2 horas y reserve), **Cancelar** y **Reprogramar** deben responder «Solo se
   puede cancelar o reprogramar hasta 24 horas antes de la cita.»
8. Atención: con la cita de esa franja de hoy, `vac01` abre **Citas del día**, elige el lote y pulsa **Atender**: «Dosis registrada y cita atendida.»
   **No asistió** solo se acepta cuando la hora ya pasó; pide confirmación y deja una alerta de seguimiento.

**Resultado esperado:** con dos reservas del último cupo, exactamente una se acepta (RNF-03); nunca hay más citas que cupo; una cita cancelada libera
su cupo.

**Automático:** `CitasTests` (20), `CitasGestionTests` (14), `AtencionTests` (22), `HorariosTests` (23); los casos H de `07_pruebas.sql` (franjas, cupo,
elegibilidad, vínculo familiar, cancelar y reprogramar hasta 24 h, atención e inasistencia). **Concurrencia real** con procesos independientes que esperan a la misma hora exacta:

```powershell
./tests/concurrencia.ps1        # 2, 10 y 20 sesiones
```

Resultado del 03/10/2026, 22 escenarios, todos correctos: 1 cupo disputado por 2, 10 y 20 sesiones (3 rondas cada una) → siempre exactamente 1
aceptada y las demás con el error 50126; una misma dosis en 10 y 20 franjas a la vez → 1 aceptada (50125 en las demás); 5 cupos disputados por 10
y 20 sesiones → exactamente 5 aceptadas; 2, 10 y 20 reprogramaciones y 2, 10 y 20 intercambios cruzados de cupo → sin pérdidas ni
interbloqueos. En la base nunca queda más de una cita por cupo.

## 4. Stock

**Manual**

1. Con `jefe01`, **Stock → Ingresar lote:** SPR, lote `PRB-0001`, laboratorio de prueba, vencimiento dentro de 1 año, **cantidad 2**, **umbral mínimo 1**
   → «Lote ingresado.» Aparece en la tabla con estado **Normal**.
2. Con `vac01` cierre sesión y vuelva a entrar (el menú de lotes se carga una sola vez, al abrir la pantalla). Registre tres pacientes nuevos con
   edad para SPR 1.ª dosis (DNI `89990004`, `89990005` y `89990006`) y registre a `89990004` una dosis de SPR con el lote `PRB-0001`.
3. En **Stock** del jefe: existencias 1, estado **Bajo**, y una alerta «Stock bajo» (RN-12).
4. Registre la segunda dosis (`89990005`): existencias 0. Sin recargar la pantalla de `vac01`, intente una tercera (`89990006`) con el mismo lote: «No hay
   stock suficiente del lote en el establecimiento: no se puede aplicar la dosis.» (RN-11) y la dosis no se registra. Al recargar, el lote ya no se ofrece.
5. El jefe pulsa **Ajustar** en el lote, cantidad 10 y motivo «Reposición de prueba»: «Existencia ajustada.» La alerta de stock bajo se cierra sola.
6. Intente **Ajustar** sin motivo: no se envía. Ingrese un lote con vencimiento pasado: «No se puede ingresar un lote vencido.»

**Resultado esperado:** cada dosis descuenta una unidad del lote del establecimiento (RN-10); con stock 0 la dosis se rechaza (RN-11); llegar al
umbral crea la alerta (RN-12) y reponer la cierra; un lote que vence en 30 días o menos genera alerta (RN-13).

**Automático:** `StockTests` (4), `GestionStockTests` (20); `07_pruebas.sql`: K1…K4 (restricciones de existencias), D1…D8 (RN-10 descuento y
movimiento, RN-11 stock cero, RN-12 alerta única al llegar al umbral, reposición que la cierra, RN-13 lote por vencer) y G1…G12 (ingresos y ajustes).

## 5. Tablero de cobertura y vigilancia

**Manual (con `epi01`)**

1. **Tablero**, vacuna SPR, esquema completo → **Consultar**. Deben verse: cobertura regional, conteo de distritos ÓPTIMA / ACEPTABLE / CRÍTICA, el gráfico
   de barras con las líneas de 95 % y 80 %, el gráfico por semáforo y la tabla de menor a mayor cobertura. El título indica los milisegundos.
2. Compare un distrito con **Cobertura por distrito** (SPR, dosis 1): la cobertura y la clasificación coinciden. Con 95 % o más debe figurar ÓPTIMA,
   entre 80 % y menos de 95 %, ACEPTABLE, y con menos de 80 %, CRÍTICA. `DashboardTests` comprueba todos los distritos de los datos simulados contra
   esos umbrales, pero no fabrica casos exactamente en el límite (94,9 o 79,9 %): si le interesa, revíselos a mano con datos propios.
3. **Brotes → Declarar brote:** enfermedad Sarampión, un distrito sin brote activo (vea la tabla), inicio de hoy, 1 caso → «Brote declarado. Se
   generaron N alertas.» Repita el mismo distrito: «Ya existe un brote activo de esa enfermedad en el distrito.»
4. **Alertas** → tipo «Zona de brote»: aparecen los pacientes del distrito con dosis pendiente. **Atender** y **Descartar** piden confirmación.
5. **Brotes → Cerrar** el brote: «Brote cerrado. X alertas vuelven a «dosis atrasada» y Y se descartan.»
6. **Campañas → Nueva campaña:** nombre «Prueba manual», inicio hoy, fin dentro de 7 días, distrito Tacna con meta 50 → **Agregar distrito** → **Crear campaña**.
   Aparece VIGENTE con avance 0 %. **Cerrar** (confirmar): «Campaña … cerrada.»; la tabla muestra su fin en hoy.

**Resultado esperado:** semáforo según RN-21 (≥ 95, ≥ 80, < 80); riesgo de sarampión ALTO con brote activo o SPR 1.ª dosis < 80 %, MEDIO con SPR
2.ª dosis < 95 %; un solo brote activo por enfermedad y distrito (RN-19); alertas al declarar (RN-20); campaña con metas por distrito y avance.

**Automático:** `DashboardTests` (18), `BrotesTests` (15), `BrotesCierreTacnaTests` (2), `AlertasTests` (21), `CampanasTests` (31); `07_pruebas.sql`: P1…P8 (alertas por
zona de brote, cierre y descarte, generación por lotes idempotente) y H57…H68 (campañas).

**Rendimiento (RNF-01).** Salida de `./desplegar.ps1 -Pruebas` del 03/10/2026, 20 000 pacientes y unas 308 000 dosis, dos despliegues seguidos
(límite 2 s):

| Reporte | Primera corrida | Segunda corrida |
|---|---|---|
| `vw_CoberturaDistrito` (todas las dosis, 532 filas) | 414 ms | 398 ms |
| `vw_CoberturaSarampion` (28 distritos) | 169 ms | 181 ms |
| `vw_DosisPendientes` (unas 36 300 filas) | 203 ms | 193 ms |
| `vw_AlertasPendientes` (unas 33 000 filas) | 478 ms | 466 ms |
| `vw_ResumenGeneral` | 176 ms | 176 ms |

## 6. Despliegue y producción

| Comprobación | Cómo | Resultado |
|---|---|---|
| Idempotencia (RNF-07) | `./desplegar.ps1 -Pruebas` dos veces seguidas | Sin errores, 144/144 las dos veces |
| Carga de producción sin datos de prueba | `./tests/produccion.ps1` | 32/32: 23 tablas y 191 objetos como en desarrollo, tablas de datos vacías, solo `admin` sin clave, segunda ejecución rechazada sin tocar nada |
| Paquete y arranque | `./tests/publicacion.ps1` | 51/51: sin secretos en el paquete; arranque en Production con HTTPS; login de `admin`; GET sin error 5xx con la base vacía; 91,5 MB de memoria |
| Hosting real | `docs/despliegue-monsterasp.md`, sección 7 | **Pendiente**: lo ejecuta el equipo con sus credenciales |

## 7. Qué no cubren estas pruebas

- **Interfaz con sesión iniciada:** las pantallas se comprobaron en el navegador con respuestas simuladas de la API (sin iniciar sesión), no con
  datos reales de punta a punta. La tabla de pasos manuales de este documento es la forma de cerrar esa verificación.
- **Prueba de carga en el hosting real** (20 usuarios simultáneos, T6.3): pendiente de que exista la URL pública. `tests/concurrencia.ps1` actual
  ataca SQL Server directamente.
- Los umbrales de negocio son supuestos del equipo, pendientes de validar con la DIRESA.
