VacunApp Tacna: Base de Datos para el Registro y Control de Vacunación ante el Brote de Sarampión
Problema

Entre 2025 y 2026, Tacna registró un brote de sarampión: la DIRESA confirmó primero 4 casos y luego 9 casos, señalando que ningún paciente tenía vacuna registrada. La DIRESA respondió con una semana de intensificación de vacunación y luego reforzó las acciones de bloqueo tras la alerta sanitaria. Pese a estas campañas, no existe un sistema que permita saber en tiempo real quién está vacunado, con qué dosis y en qué distrito, lo que dificulta priorizar campañas y detectar a tiempo a la población en riesgo.

Solución propuesta

VacunApp Tacna: base de datos relacional con procedimientos almacenados, triggers y vistas que centralizan el registro de dosis aplicadas y generan reportes de cobertura por distrito y alertas de pacientes con dosis pendiente, expuestos mediante una interfaz mínima de consulta (sin necesidad de una aplicación web completa).

Objetivos
General: diseñar e implementar una base de datos relacional normalizada (3FN), con procedimientos almacenados, triggers y vistas, que centralice el registro de vacunación de Tacna y genere reportes de cobertura para apoyar la respuesta ante brotes como el de sarampión.
Modelar y normalizar la base de datos (pacientes, dosis, campañas, centros de salud) reduciendo a 0% las columnas redundantes, verificable con el diagrama entidad-relación.
Implementar al menos 5 procedimientos almacenados y 2 triggers (ej. alerta automática cuando un paciente en zona de brote tiene una dosis pendiente).
Crear vistas/consultas que generen el reporte de cobertura de vacunación por distrito, con tiempo de respuesta menor a 2 segundos sobre 5,000+ registros simulados.
Exponer estos reportes mediante una interfaz mínima de consulta (script o panel simple), sin necesidad de desarrollar una aplicación web completa.
