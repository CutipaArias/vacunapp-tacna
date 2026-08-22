# VacunApp Tacna: Aplicación para el Registro y Control de Vacunación ante el Brote de Sarampión

## Problema

Entre 2025 y 2026, Tacna registró un brote de sarampión: la DIRESA confirmó primero [4 casos](https://radiouno.pe/noticias/277640/tacna-confirma-cuatro-casos-de-sarampion-y-refuerza-acciones-de-bloqueo-y-vacunacion) y luego [9 casos](https://radiouno.pe/noticias/279686/confirman-9-casos-de-sarampion-en-tacna-ningun-paciente-contaba-con-vacuna), señalando que ningún paciente tenía vacuna registrada. La DIRESA respondió con una [semana de intensificación de vacunación](https://radiouno.pe/noticias/265555/diresa-tacna-lanza-semana-de-intensificacion-de-vacunacion-para-proteger-a-los-ninos) y luego reforzó las [acciones de bloqueo tras la alerta sanitaria](https://radiouno.pe/noticias/273174/direccion-regional-de-salud-de-tacna-refuerza-vacunacion-tras-alerta-sanitaria-por-brote-de-sarampion). Pese a estas campañas, no existe un sistema que permita saber en tiempo real quién está vacunado, con qué dosis y en qué distrito, lo que dificulta priorizar campañas y detectar a tiempo a la población en riesgo.

## Aplicación propuesta

**VacunApp Tacna**: aplicación web donde el personal de los centros de salud registra cada dosis aplicada (paciente, vacuna, fecha, centro), consulta el historial de vacunación de un paciente, y visualiza un dashboard con la cobertura por distrito y las personas con dosis pendiente, respaldada por una base de datos relacional.

## Objetivos

- **General:** desarrollar una aplicación web con base de datos relacional normalizada (3FN) que centralice pacientes, dosis, campañas y centros de salud de Tacna, para apoyar la respuesta ante brotes como el de sarampión.
- Modelar y normalizar la base de datos (pacientes, dosis, campañas, centros de salud) reduciendo a 0% las columnas redundantes, verificable con el diagrama entidad-relación.
- Implementar el formulario de registro de vacunación con validación de datos obligatorios (paciente, vacuna, dosis, fecha, centro).
- Generar un dashboard de cobertura de vacunación por distrito, con tiempo de respuesta menor a 2 segundos sobre 5,000+ registros simulados.
- Implementar al menos 5 procedimientos almacenados y 2 triggers (ej. alerta automática cuando un paciente en zona de brote tiene una dosis pendiente).
