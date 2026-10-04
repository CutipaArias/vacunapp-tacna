# Manual de usuario – VacunApp Tacna v2.0

VacunApp Tacna registra las dosis de vacunación de Tacna, administra citas con cupos y stock por establecimiento y muestra la cobertura
por distrito frente al brote de sarampión. Todos los datos del sistema son **simulados**.

Este manual describe lo que ve y hace cada rol. Las capturas se omiten porque cada pantalla depende de la sesión; los nombres de
pestañas, campos y botones son los de la aplicación.

## 1. Ingresar y salir

1. Abra la dirección del sistema en el navegador (Chrome, Edge, Firefox o Safari actuales). La interfaz está pensada para celulares desde
   360 px de ancho. En el servidor real la dirección debe ser `https://`; por `http://` el inicio de sesión no se mantiene.
2. Escriba **Usuario** y **Contraseña** y pulse **Ingresar**.
3. Para terminar, **Cerrar sesión** (arriba a la derecha). La sesión dura 8 horas; después el sistema vuelve a pedir el acceso.

| Situación | Qué ocurre |
|---|---|
| Usuario o contraseña incorrectos | «Usuario o contraseña incorrectos.» El mensaje es el mismo exista o no la cuenta. |
| 5 intentos fallidos seguidos en una cuenta | La cuenta se bloquea 15 minutos, aunque después escriba la clave correcta. Espere. |
| Demasiados intentos desde la misma conexión | El sistema rechaza temporalmente nuevos intentos de ingreso. |
| La cuenta no tiene opciones | «Su cuenta no tiene opciones habilitadas todavía.» Avise al administrador. |

El administrador crea su cuenta y le entrega la contraseña inicial. **La versión 2.0 no permite cambiar ni restablecer contraseñas desde la
aplicación** (ver sección 9).

## 2. Qué ve cada rol

El menú muestra solo las pestañas del rol; aunque alguien escriba la dirección de otra función, el servidor responde «prohibido».

| Pestaña | Ciudadano | Vacunador | Jefe de establecimiento | Epidemiólogo | Administrador |
|---|:-:|:-:|:-:|:-:|:-:|
| Resumen, Tablero, Cobertura por distrito | | | | ✓ | ✓ |
| Alertas, Pendientes | | ✓ | ✓ | ✓ | ✓ |
| Brotes, Campañas | | | | ✓ | ✓ |
| Paciente (carné) | ✓ (sus hijos) | ✓ + registrar paciente y dosis | ✓ (consulta) | ✓ (consulta) | ✓ (consulta) |
| Citas (reservar, reprogramar, cancelar) | ✓ | ✓ | ✓ | | |
| Citas del día | | ✓ | | | |
| Stock, Horarios | | | ✓ | | |
| Usuarios, Auditoría | | | | | ✓ |

**Alcance (RN-22).** El vacunador y el jefe solo ven y modifican lo de **su establecimiento** (las alertas y pendientes de su distrito). El
epidemiólogo y el administrador trabajan sobre toda la región. El ciudadano solo ve a los hijos vinculados a su cuenta.

## 3. Ciudadano (madre, padre o tutor)

### Ver el carné de mis hijos – pestaña **Paciente**
Aparece un botón por hijo vinculado, con su parentesco. Al pulsarlo se muestra su edad en meses, el distrito y dos tablas:
**Aplicada** (vacuna y dosis, fecha, lote y establecimiento) y **Pendiente** (vacuna, dosis y meses de atraso).

Si ve «Aún no tiene hijos vinculados a su cuenta», el vínculo no está registrado (ver sección 9).

### Reservar una cita – pestaña **Citas**
1. **Paciente:** elija al hijo o hija.
2. **Dosis:** elija la dosis pendiente. Solo se ofrecen las que le corresponden por edad, dosis previa e intervalo.
3. **Establecimiento y franja:** deje «Todos» o elija un centro de salud, y escoja la franja (día, hora y cupos libres). Si no hay franjas,
   pruebe con otro establecimiento o más adelante.
4. **Reservar cita.** Aparece «Cita reservada. Llegue 10 minutos antes con el DNI del paciente.»

Reglas: una sola cita activa por dosis, y una franja nunca admite más citas que su cupo. Si la franja se llenó mientras elegía, el sistema
lo indica y actualiza la lista.

### Reprogramar o cancelar – tabla **Citas del paciente**
En una cita **PROGRAMADA**: **Cancelar** (libera el cupo) o **Reprogramar** (elija la «Nueva franja» y pulse **Reprogramar**; si la
nueva franja se llenó, la cita original se conserva). Solo se puede hasta **24 horas antes** de la cita.

## 4. Vacunador

Trabaja en su establecimiento. Pestañas: Alertas, Pendientes, Paciente, Citas y Citas del día.

### Registrar un paciente – pestaña **Paciente**, tarjeta «Registrar paciente»
Complete tipo y número de documento, nombres, apellido paterno, nacimiento, sexo y distrito (apellido materno, dirección y teléfono son
opcionales) y pulse **Registrar**. Reglas: el DNI tiene 8 dígitos, el documento no puede repetirse y el nacimiento no puede ser futuro.
Después se muestra el carné.

### Registrar una dosis – pestaña **Paciente**
1. Escriba el **DNI** y pulse **Buscar** (ve el carné de cualquier paciente de la región).
2. Si tiene dosis pendientes aparece «Registrar dosis»: elija **Dosis pendiente**, **Lote** (los de esa vacuna), **Establecimiento** (el
   suyo) y **Fecha** (hoy por defecto) y pulse **Registrar**.
3. La dosis queda a su nombre y en su establecimiento, descuenta una unidad del lote y cierra las alertas que tuviera el paciente.

El sistema rechaza, con el motivo en español: edad fuera de rango (50103), falta de la dosis anterior o intervalo no cumplido (50104),
dosis ya registrada (50015), lote vencido (50101), lote de otra vacuna (50100) y **lote sin existencias** (50107).

### Atender las citas – pestaña **Citas del día**
Elija la **Fecha** y pulse **Ver**. Para cada cita **PROGRAMADA**:
- **Atender** (solo el día de la franja): elija el **Lote** (aparece con sus existencias) y pulse el botón. Registra la dosis con las mismas
  validaciones y descuenta el stock; si algo falla, la cita sigue programada.
- **No asistió** (solo cuando la hora ya pasó): pide confirmación y crea una alerta de seguimiento. No consume stock.

### Alertas y pendientes
- **Alertas:** filtre por **Tipo** (zona de brote, inasistencia, dosis atrasada) y use **Atender** o **Descartar** (con confirmación) cuando
  haya contactado o resuelto el caso. Debajo, las alertas de stock (solo lectura). Se muestran las primeras 200.
- **Pendientes:** lista nominal de niños con dosis atrasadas, con teléfono. Filtre por **Vacuna** o marque **Solo zona de brote**.

### Reservar por un paciente – pestaña **Citas**
Igual que el ciudadano, pero busca al paciente por **DNI** en lugar de elegir un hijo.

## 5. Jefe de establecimiento

Pestañas: Alertas, Pendientes, Paciente, Citas, Stock y Horarios, siempre sobre su establecimiento. **Consulta** el carné pero no registra
pacientes ni dosis; sí puede reservar citas por un paciente (pestaña **Citas**, búsqueda por DNI).

### Stock – pestaña **Stock**
- **Alertas de stock:** lotes con existencias iguales o por debajo del umbral mínimo y lotes que vencen en 30 días o menos. Se cierran
  solas al reponer el stock.
- **Stock de mi establecimiento:** vacuna, lote, laboratorio, vencimiento, existencias, umbral y estado (**Bajo** / **Normal**).
- **Ajustar:** botón de cada fila. Escriba la **Cantidad contada** y el **Motivo** (obligatorio) y pulse **Ajustar**. Queda registrado como
  movimiento.
- **Ingresar lote:** vacuna, número de lote, laboratorio, vencimiento, cantidad y, si quiere cambiarlo, umbral mínimo. Si el lote ya
  existe, la cantidad se suma y el vencimiento debe coincidir (50110). No se ingresan lotes vencidos (50111).

### Horarios – pestaña **Horarios**
- **Ver:** elija **Desde** y **Hasta** (por defecto, hoy y 30 días). La tabla muestra cupo, reservados, libres y estado.
- **Nueva franja:** vacuna, fecha, hora y cupo máximo (de 1 a 500; 10 por defecto). No se crean franjas en el pasado (50131) ni
  repetidas para la misma vacuna y hora (50130).
- **Editar:** cambia el **Cupo máximo** o desactiva la franja (**Activa: No**). El cupo no puede bajar de las citas ya reservadas (50128).

## 6. Epidemiólogo (DIRESA)

Alcance regional. Pestañas: Resumen, Tablero, Cobertura por distrito, Alertas, Pendientes, Brotes, Campañas y Paciente.

### Resumen y cobertura
- **Resumen:** pacientes registrados, dosis aplicadas, cobertura regional de SPR 1.ª y 2.ª dosis, brotes activos (con sus casos) y alertas
  pendientes; debajo, la cobertura de SPR por distrito con su riesgo.
- **Cobertura por distrito:** reporte por **Vacuna**, **Dosis** y **Provincia**, de menor a mayor cobertura.

### Tablero – pestaña **Tablero**
Elija **Vacuna** (SPR por defecto) y **Dosis** (esquema completo, 1, 2 o 3) y pulse **Consultar**.
- Indicadores: cobertura regional y distritos **ÓPTIMA** (desde 95 %), **ACEPTABLE** (desde 80 %) y **CRÍTICA** (bajo 80 %); distritos
  con riesgo de sarampión **ALTO** (brote activo o SPR 1.ª dosis < 80 %) o **MEDIO** (SPR 2.ª dosis < 95 %).
- Gráfico de barras por distrito con las líneas de 95 % y 80 %, gráfico de distritos por semáforo y, debajo, la tabla con los mismos datos
  ordenada de menor a mayor cobertura: ahí se ve dónde vacunar primero.

Los umbrales (95 % y 80 %) son supuestos del equipo basados en la meta de la OPS; deben validarse con la DIRESA. Un distrito sin
pacientes registrados no aparece, porque sin niños elegibles no hay cobertura que calcular.

### Brotes – pestaña **Brotes**
- **Declarar brote:** enfermedad, distrito, inicio (no futuro) y casos confirmados; pide confirmación. Genera una alerta por cada paciente
  del distrito con una dosis pendiente de una vacuna que previene esa enfermedad. Solo puede haber un brote activo por enfermedad y
  distrito (50022).
- **Cerrar:** en un brote activo. Las alertas por dosis atrasada vuelven a su estado anterior y las demás se descartan.

### Campañas – pestaña **Campañas**
- La tabla muestra periodo, **Estado** (PROGRAMADA, VIGENTE, FINALIZADA), meta, dosis aplicadas, avance, dosis por vacuna y el detalle por
  distrito (pulse el número de distritos).
- **Nueva campaña:** nombre, inicio, fin y descripción opcional; luego, por cada distrito, elija el **Distrito**, la **Meta de dosis**
  y pulse **Agregar distrito** (**Quitar** lo retira). Pulse **Crear campaña**.
- **Cerrar:** en una campaña vigente; fija su fin en hoy y desde mañana ya no admite dosis. Hoy sigue figurando como vigente (su estado
  se deduce de las fechas) y, si vuelve a cerrarla, el sistema responde «La campaña ya terminó o termina hoy.»
- Las dosis cuentan para la campaña cuando se registran dentro de su periodo. La campaña no tiene vacuna propia: el desglose «Por vacuna» se
  deduce de las dosis aplicadas.

### Alertas, pendientes y carné
Igual que el vacunador, pero sobre toda la región y con filtro por **Distrito**. En **Paciente** consulta el carné de cualquier paciente
por DNI.

## 7. Administrador

Alcance regional. Tiene todo lo del epidemiólogo y, además, **Usuarios** y **Auditoría**.

### Usuarios – pestaña **Usuarios**
- **Nuevo usuario:** usuario (3 a 30 caracteres: letras, números, punto o guion bajo), nombre completo, contraseña inicial, rol y, según el
  rol, establecimiento (jefe y vacunador) y personal vacunador registrado en ese establecimiento (solo vacunador). La contraseña tiene de
  10 a 128 caracteres con mayúscula, minúscula, número y símbolo. Se guarda solo como hash; entréguela al usuario por un canal seguro.
- **Usuarios del sistema:** **Desactivar** o **Activar** una cuenta. No puede desactivar la suya ni dejar el sistema sin un administrador
  activo (50218).
- No hay edición de datos, cambio de contraseña ni vínculo de ciudadanos con pacientes (sección 9).

### Auditoría – pestaña **Auditoría**
Muestra las correcciones y eliminaciones de dosis con su estado anterior y nuevo, el usuario, el paciente y el lote. Filtre por **DNI**
y **Operación**. Lo más reciente va primero. La aplicación no ofrece corregir ni eliminar dosis; la auditoría registra los cambios
hechos en la base de datos.

## 8. Mensajes frecuentes

| Mensaje | Qué significa y qué hacer |
|---|---|
| La franja no tiene cupos disponibles. | Alguien reservó antes; elija otra franja. |
| El paciente ya tiene una cita activa para esta dosis. | Cancele o reprograme la cita existente. |
| La dosis no es elegible para el paciente en esa fecha (edad, dosis anterior, intervalo o ya aplicada). | Revise el carné: falta la dosis previa, no cumple el intervalo o ya se aplicó. |
| Solo se puede cancelar o reprogramar hasta 24 horas antes de la cita. | Es tarde para cambiarla; el paciente puede acudir o ser marcado como inasistencia. |
| Solo se puede atender la cita el día de la franja. / …marcar la inasistencia cuando la hora ya pasó. | Use la fecha y la hora correspondientes en **Citas del día**. |
| No hay stock suficiente del lote en el establecimiento: no se puede aplicar la dosis. | El jefe debe ingresar o ajustar el lote en **Stock**. |
| Ya existe un paciente con ese documento. | Búsquelo por DNI en lugar de registrarlo de nuevo. |
| Ya existe un brote activo de esa enfermedad en el distrito. | Cierre el anterior o consulte la tabla de brotes. |
| El sistema está ocupado; intente de nuevo en unos segundos. | Hubo muchas operaciones simultáneas sobre el mismo cupo; repita. |
| Ocurrió un error interno. Intente nuevamente. | Falla no prevista; repita y, si continúa, avise al administrador. |

## 9. Límites de esta versión

- **No hay cambio ni restablecimiento de contraseña.** Si la olvida, el administrador puede dar de baja la cuenta y crear otra con otro
  nombre de usuario; la del propio administrador se recupera desde la base (`docs/despliegue-monsterasp.md`, sección 11).
- **El vínculo de un ciudadano con sus hijos no se crea desde la aplicación.** Sin él, el ciudadano ve «Aún no tiene hijos vinculados» y
  no puede reservar. Lo registra quien administra la base de datos (misma sección).
- **Los vacunadores y los establecimientos no se dan de alta desde la aplicación.** Los del catálogo son simulados; el personal y los centros
  reales los inserta quien administra la base (`docs/despliegue-monsterasp.md`, sección 11) antes de que el administrador cree sus cuentas.
- **No se corrigen ni eliminan dosis desde la aplicación** (decisión de alcance de la v2.0).
- Alertas y pendientes muestran las primeras 200 filas.
- Los umbrales de negocio (30 días, 24 horas, 95 % y 80 %) y la lista de dosis son supuestos del equipo, pendientes de validar con la DIRESA.
- Los datos son simulados (Ley 29733); no registre personas reales.
