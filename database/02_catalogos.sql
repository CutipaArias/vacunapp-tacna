/* =====================================================================
   VacunApp Tacna - 02. Datos maestros (catálogos)
   - 4 provincias y 28 distritos del departamento de Tacna (ubigeo INEI).
   - Enfermedades inmunoprevenibles y vacunas del esquema regular.
   - Esquema de dosis por edad, basado en el Esquema Nacional de
     Vacunación del MINSA (NTS N.° 196-MINSA/DGIESP-2022), simplificado.
   - Establecimientos, personal y campañas: SIMULADOS para el proyecto.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO
SET NOCOUNT ON;

/* Provincias y distritos */
INSERT INTO vac.Provincia (Nombre) VALUES ('Tacna'), ('Candarave'), ('Jorge Basadre'), ('Tarata');

INSERT INTO vac.Distrito (IdProvincia, Ubigeo, Nombre)
SELECT p.IdProvincia, d.Ubigeo, d.Nombre
FROM (VALUES
    ('Tacna','230101','Tacna'),
    ('Tacna','230102','Alto de la Alianza'),
    ('Tacna','230103','Calana'),
    ('Tacna','230104','Ciudad Nueva'),
    ('Tacna','230105','Inclán'),
    ('Tacna','230106','Pachía'),
    ('Tacna','230107','Palca'),
    ('Tacna','230108','Pocollay'),
    ('Tacna','230109','Sama'),
    ('Tacna','230110','Coronel Gregorio Albarracín Lanchipa'),
    ('Tacna','230111','La Yarada Los Palos'),
    ('Candarave','230201','Candarave'),
    ('Candarave','230202','Cairani'),
    ('Candarave','230203','Camilaca'),
    ('Candarave','230204','Curibaya'),
    ('Candarave','230205','Huanuara'),
    ('Candarave','230206','Quilahuani'),
    ('Jorge Basadre','230301','Locumba'),
    ('Jorge Basadre','230302','Ilabaya'),
    ('Jorge Basadre','230303','Ite'),
    ('Tarata','230401','Tarata'),
    ('Tarata','230402','Héroes Albarracín'),
    ('Tarata','230403','Estique'),
    ('Tarata','230404','Estique-Pampa'),
    ('Tarata','230405','Sitajara'),
    ('Tarata','230406','Susapaya'),
    ('Tarata','230407','Tarucachi'),
    ('Tarata','230408','Ticaco')
) AS d (Provincia, Ubigeo, Nombre)
JOIN vac.Provincia p ON p.Nombre = d.Provincia;

/* Enfermedades (código CIE-10) */
INSERT INTO vac.Enfermedad (Nombre, CodigoCie10) VALUES
    ('Tuberculosis',                    'A15'),
    ('Hepatitis B',                     'B16'),
    ('Difteria',                        'A36'),
    ('Tos ferina',                      'A37'),
    ('Tétanos',                         'A35'),
    ('Haemophilus influenzae tipo b',   'J14'),
    ('Poliomielitis',                   'A80'),
    ('Diarrea por rotavirus',           'A08.0'),
    ('Neumonía neumocócica',            'J13'),
    ('Sarampión',                       'B05'),
    ('Rubéola',                         'B06'),
    ('Parotiditis',                     'B26'),
    ('Varicela',                        'B01'),
    ('Fiebre amarilla',                 'A95');

/* Vacunas */
INSERT INTO vac.Vacuna (Codigo, Nombre, Via) VALUES
    ('BCG',   'Bacilo de Calmette-Guérin',                      'Intradérmica'),
    ('HVB',   'Hepatitis B pediátrica',                         'Intramuscular'),
    ('PENTA', 'Pentavalente (DPT-HvB-Hib)',                     'Intramuscular'),
    ('POLIO', 'Antipolio (IPV/bOPV)',                           'Intramuscular'),
    ('ROTA',  'Rotavirus',                                      'Oral'),
    ('NEUMO', 'Neumococo conjugada',                            'Intramuscular'),
    ('SPR',   'Sarampión, Paperas y Rubéola',                   'Subcutánea'),
    ('VARI',  'Varicela',                                       'Subcutánea'),
    ('AMA',   'Antiamarílica',                                  'Subcutánea'),
    ('DPT',   'DPT (refuerzos)',                                'Intramuscular');

INSERT INTO vac.VacunaEnfermedad (IdVacuna, IdEnfermedad)
SELECT v.IdVacuna, e.IdEnfermedad
FROM (VALUES
    ('BCG','Tuberculosis'),
    ('HVB','Hepatitis B'),
    ('PENTA','Difteria'), ('PENTA','Tos ferina'), ('PENTA','Tétanos'),
    ('PENTA','Hepatitis B'), ('PENTA','Haemophilus influenzae tipo b'),
    ('POLIO','Poliomielitis'),
    ('ROTA','Diarrea por rotavirus'),
    ('NEUMO','Neumonía neumocócica'),
    ('SPR','Sarampión'), ('SPR','Rubéola'), ('SPR','Parotiditis'),
    ('VARI','Varicela'),
    ('AMA','Fiebre amarilla'),
    ('DPT','Difteria'), ('DPT','Tos ferina'), ('DPT','Tétanos')
) AS x (Vacuna, Enfermedad)
JOIN vac.Vacuna v     ON v.Codigo = x.Vacuna
JOIN vac.Enfermedad e ON e.Nombre = x.Enfermedad;

/* Esquema de dosis: edad mínima/máxima en meses e intervalo mínimo
   respecto de la dosis anterior de la misma vacuna. */
INSERT INTO vac.EsquemaDosis (IdVacuna, NumeroDosis, Descripcion, EdadMinimaMeses, EdadMaximaMeses, IntervaloMinDias)
SELECT v.IdVacuna, x.NumeroDosis, x.Descripcion, x.EdadMin, x.EdadMax, x.Intervalo
FROM (VALUES
    ('BCG',   1, 'Recién nacido',        0,  11,   0),
    ('HVB',   1, 'Recién nacido',        0,   1,   0),
    ('PENTA', 1, '1.a dosis (2 meses)',  2, NULL,  0),
    ('PENTA', 2, '2.a dosis (4 meses)',  4, NULL, 28),
    ('PENTA', 3, '3.a dosis (6 meses)',  6, NULL, 28),
    ('POLIO', 1, '1.a dosis (2 meses)',  2, NULL,  0),
    ('POLIO', 2, '2.a dosis (4 meses)',  4, NULL, 28),
    ('POLIO', 3, '3.a dosis (6 meses)',  6, NULL, 28),
    ('ROTA',  1, '1.a dosis (2 meses)',  2,   5,   0),
    ('ROTA',  2, '2.a dosis (4 meses)',  4,   8,  28),
    ('NEUMO', 1, '1.a dosis (2 meses)',  2, NULL,  0),
    ('NEUMO', 2, '2.a dosis (4 meses)',  4, NULL, 28),
    ('NEUMO', 3, 'Refuerzo (12 meses)', 12, NULL, 28),
    ('SPR',   1, '1.a dosis (12 meses)',12, NULL,  0),
    ('SPR',   2, '2.a dosis (18 meses)',18, NULL, 28),
    ('VARI',  1, 'Dosis única (12 meses)',12, NULL, 0),
    ('AMA',   1, 'Dosis única (15 meses)',15, NULL, 0),
    ('DPT',   1, '1.er refuerzo (18 meses)',18, NULL, 180),
    ('DPT',   2, '2.o refuerzo (4 años)',  48, NULL, 180)
) AS x (Vacuna, NumeroDosis, Descripcion, EdadMin, EdadMax, Intervalo)
JOIN vac.Vacuna v ON v.Codigo = x.Vacuna;

/* Establecimientos de salud (SIMULADOS; códigos RENIPRESS ficticios).
   Uno por distrito, más establecimientos adicionales en los distritos
   con mayor población. */
INSERT INTO vac.EstablecimientoSalud (CodigoRenipress, Nombre, Categoria, IdDistrito)
SELECT RIGHT('0000000' + CAST(900 + ROW_NUMBER() OVER (ORDER BY d.Ubigeo, x.Orden) AS VARCHAR(8)), 8),
       x.Nombre, x.Categoria, d.IdDistrito
FROM (VALUES
    ('230101', 1, 'Hospital Regional Hipólito Unanue',     'II-2'),
    ('230101', 2, 'C.S. San Francisco',                    'I-4'),
    ('230101', 3, 'C.S. Metropolitano',                    'I-3'),
    ('230102', 1, 'C.S. Alto de la Alianza',               'I-3'),
    ('230102', 2, 'P.S. Juan Velasco Alvarado',            'I-2'),
    ('230103', 1, 'P.S. Calana',                           'I-2'),
    ('230104', 1, 'C.S. Ciudad Nueva',                     'I-4'),
    ('230104', 2, 'P.S. Cono Norte',                       'I-2'),
    ('230105', 1, 'C.S. Inclán',                           'I-3'),
    ('230106', 1, 'P.S. Pachía',                           'I-2'),
    ('230107', 1, 'P.S. Palca',                            'I-2'),
    ('230108', 1, 'C.S. Pocollay',                         'I-3'),
    ('230109', 1, 'P.S. Sama Las Yaras',                   'I-2'),
    ('230110', 1, 'C.S. Cono Sur',                         'I-4'),
    ('230110', 2, 'C.S. La Esperanza',                     'I-3'),
    ('230110', 3, 'P.S. Viñani',                           'I-2'),
    ('230111', 1, 'C.S. La Yarada',                        'I-3'),
    ('230201', 1, 'C.S. Candarave',                        'I-4'),
    ('230202', 1, 'P.S. Cairani',                          'I-1'),
    ('230203', 1, 'P.S. Camilaca',                         'I-1'),
    ('230204', 1, 'P.S. Curibaya',                         'I-1'),
    ('230205', 1, 'P.S. Huanuara',                         'I-1'),
    ('230206', 1, 'P.S. Quilahuani',                       'I-1'),
    ('230301', 1, 'C.S. Locumba',                          'I-3'),
    ('230302', 1, 'C.S. Ilabaya',                          'I-3'),
    ('230303', 1, 'C.S. Ite',                              'I-3'),
    ('230401', 1, 'C.S. Tarata',                           'I-4'),
    ('230402', 1, 'P.S. Chucatamani',                      'I-1'),
    ('230403', 1, 'P.S. Estique',                          'I-1'),
    ('230404', 1, 'P.S. Estique-Pampa',                    'I-1'),
    ('230405', 1, 'P.S. Sitajara',                         'I-1'),
    ('230406', 1, 'P.S. Susapaya',                         'I-1'),
    ('230407', 1, 'P.S. Tarucachi',                        'I-1'),
    ('230408', 1, 'P.S. Ticaco',                           'I-1')
) AS x (Ubigeo, Orden, Nombre, Categoria)
JOIN vac.Distrito d ON d.Ubigeo = x.Ubigeo;

/* Personal de salud (SIMULADO): dos vacunadores por establecimiento. */
DECLARE @Nombres TABLE (N TINYINT PRIMARY KEY, Nombre VARCHAR(30));
INSERT INTO @Nombres VALUES (0,'Rosa'),(1,'Carmen'),(2,'Luis'),(3,'Milagros'),(4,'Jorge'),(5,'Ana'),(6,'Víctor'),(7,'Gladys'),(8,'Edwin'),(9,'Silvia');
DECLARE @Apellidos TABLE (N TINYINT PRIMARY KEY, Apellido VARCHAR(40));
INSERT INTO @Apellidos VALUES (0,'Mamani Quispe'),(1,'Condori Flores'),(2,'Ticona Choque'),(3,'Apaza Laura'),(4,'Chambilla Pari'),(5,'Coaquira Ramos'),(6,'Vargas Cutipa'),(7,'Huanca Nina'),(8,'Limache Copa'),(9,'Calizaya Ale'),(10,'Rojas Valdivia');

INSERT INTO vac.PersonalSalud (Dni, Nombres, Apellidos, Cargo, IdEstablecimiento)
SELECT CAST(40000000 + e.IdEstablecimiento * 10 + k.K AS CHAR(8)),
       n.Nombre, a.Apellido,
       CASE k.K WHEN 1 THEN 'Enfermera(o)' ELSE 'Técnico(a) en enfermería' END,
       e.IdEstablecimiento
FROM vac.EstablecimientoSalud e
CROSS JOIN (VALUES (1), (2)) AS k (K)
JOIN @Nombres   n ON n.N = (e.IdEstablecimiento * 3 + k.K) % 10
JOIN @Apellidos a ON a.N = (e.IdEstablecimiento * 7 + k.K) % 11;

/* Campañas (SIMULADAS, inspiradas en las acciones de la DIRESA Tacna). */
INSERT INTO vac.Campana (Nombre, FechaInicio, FechaFin, Descripcion) VALUES
    ('Semana de Intensificación de Vacunación 2026', '2026-04-20', '2026-04-30',
     'Puesta al día del esquema regular en niños menores de 5 años.'),
    ('Barrido y bloqueo contra el sarampión 2026',   '2026-07-01', '2026-08-31',
     'Respuesta al brote de sarampión: búsqueda casa por casa de niños sin SPR.');
GO
