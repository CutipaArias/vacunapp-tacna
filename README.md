# Sistema de Base de Datos para el Control de Vacunación ante el Brote de Sarampión en Tacna

## Problema

Entre 2025 y 2026, Tacna registró un brote de sarampión: la DIRESA confirmó primero [4 casos](https://radiouno.pe/noticias/277640/tacna-confirma-cuatro-casos-de-sarampion-y-refuerza-acciones-de-bloqueo-y-vacunacion) y luego [9 casos](https://radiouno.pe/noticias/279686/confirman-9-casos-de-sarampion-en-tacna-ningun-paciente-contaba-con-vacuna), señalando que **ningún paciente tenía vacuna registrada**. Pese a las [campañas de refuerzo de la DIRESA](https://radiouno.pe/noticias/265555/diresa-tacna-lanza-semana-de-intensificacion-de-vacunacion-para-proteger-a-los-ninos), no existe un sistema que permita saber en tiempo real quién está vacunado, con qué dosis y en qué distrito, lo que dificulta priorizar campañas y detectar a tiempo a la población en riesgo.

## Objetivos

- **General:** diseñar una base de datos relacional normalizada (3FN) que centralice pacientes, dosis, campañas y centros de salud de Tacna.
- Reducir a 0% las columnas redundantes mediante normalización, verificable con el diagrama entidad-relación.
- Implementar al menos 5 procedimientos almacenados y 2 triggers (ej. alerta automática por dosis pendiente).
- Generar reportes de cobertura de vacunación por distrito con tiempo de respuesta menor a 2 segundos sobre 5,000+ registros simulados.
