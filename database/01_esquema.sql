/* =====================================================================
   VacunApp Tacna - 01. Esquema relacional (3FN)
   Motor: SQL Server 2022
   Crea la base de datos, el esquema [vac], las tablas, restricciones
   e índices. Es idempotente: elimina y vuelve a crear la base.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE master;
GO
IF DB_ID(N'VacunAppTacna') IS NOT NULL
BEGIN
    ALTER DATABASE VacunAppTacna SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE VacunAppTacna;
END
GO
CREATE DATABASE VacunAppTacna COLLATE Modern_Spanish_CI_AS;
GO
USE VacunAppTacna;
GO
CREATE SCHEMA vac AUTHORIZATION dbo;
GO

/* ---------------------------------------------------------------------
   Ubicación geográfica
   --------------------------------------------------------------------- */
CREATE TABLE vac.Provincia (
    IdProvincia   TINYINT      NOT NULL IDENTITY(1,1),
    Nombre        VARCHAR(40)  NOT NULL,
    CONSTRAINT PK_Provincia PRIMARY KEY (IdProvincia),
    CONSTRAINT UQ_Provincia_Nombre UNIQUE (Nombre)
);

CREATE TABLE vac.Distrito (
    IdDistrito    SMALLINT     NOT NULL IDENTITY(1,1),
    IdProvincia   TINYINT      NOT NULL,
    Ubigeo        CHAR(6)      NOT NULL,
    Nombre        VARCHAR(60)  NOT NULL,
    CONSTRAINT PK_Distrito PRIMARY KEY (IdDistrito),
    CONSTRAINT UQ_Distrito_Ubigeo UNIQUE (Ubigeo),
    CONSTRAINT FK_Distrito_Provincia FOREIGN KEY (IdProvincia) REFERENCES vac.Provincia (IdProvincia),
    CONSTRAINT CK_Distrito_Ubigeo CHECK (Ubigeo LIKE '23[0-9][0-9][0-9][0-9]')
);

/* ---------------------------------------------------------------------
   Establecimientos y personal de salud
   --------------------------------------------------------------------- */
CREATE TABLE vac.EstablecimientoSalud (
    IdEstablecimiento SMALLINT     NOT NULL IDENTITY(1,1),
    CodigoRenipress   CHAR(8)      NOT NULL,
    Nombre            VARCHAR(100) NOT NULL,
    Categoria         VARCHAR(5)   NOT NULL,
    IdDistrito        SMALLINT     NOT NULL,
    CONSTRAINT PK_EstablecimientoSalud PRIMARY KEY (IdEstablecimiento),
    CONSTRAINT UQ_Establecimiento_Renipress UNIQUE (CodigoRenipress),
    CONSTRAINT FK_Establecimiento_Distrito FOREIGN KEY (IdDistrito) REFERENCES vac.Distrito (IdDistrito),
    CONSTRAINT CK_Establecimiento_Categoria CHECK (Categoria IN ('I-1','I-2','I-3','I-4','II-1','II-2','III-1'))
);

CREATE TABLE vac.Vacunador (
    IdVacunador        INT          NOT NULL IDENTITY(1,1),
    Dni               CHAR(8)      NOT NULL,
    Nombres           VARCHAR(60)  NOT NULL,
    Apellidos         VARCHAR(80)  NOT NULL,
    Cargo             VARCHAR(30)  NOT NULL,
    IdEstablecimiento SMALLINT     NOT NULL,
    Activo            BIT          NOT NULL CONSTRAINT DF_Vacunador_Activo DEFAULT (1),
    CONSTRAINT PK_Vacunador PRIMARY KEY (IdVacunador),
    CONSTRAINT UQ_Vacunador_Dni UNIQUE (Dni),
    CONSTRAINT FK_Vacunador_Establecimiento FOREIGN KEY (IdEstablecimiento) REFERENCES vac.EstablecimientoSalud (IdEstablecimiento),
    CONSTRAINT CK_Vacunador_Dni CHECK (Dni NOT LIKE '%[^0-9]%'),
    CONSTRAINT CK_Vacunador_Cargo CHECK (Cargo IN ('Enfermera(o)','Técnico(a) en enfermería','Médico(a)','Obstetra'))
);

/* ---------------------------------------------------------------------
   Pacientes
   La edad NO se almacena: se deriva de FechaNacimiento (evita dependencia
   transitiva y datos desactualizados).
   --------------------------------------------------------------------- */
CREATE TABLE vac.Paciente (
    IdPaciente        INT          NOT NULL IDENTITY(1,1),
    TipoDocumento     VARCHAR(3)   NOT NULL,
    NumeroDocumento   VARCHAR(12)  NOT NULL,
    Nombres           VARCHAR(60)  NOT NULL,
    ApellidoPaterno   VARCHAR(40)  NOT NULL,
    ApellidoMaterno   VARCHAR(40)  NULL,
    FechaNacimiento   DATE         NOT NULL,
    Sexo              CHAR(1)      NOT NULL,
    IdDistrito        SMALLINT     NOT NULL,
    Direccion         VARCHAR(120) NULL,
    Telefono          VARCHAR(15)  NULL,
    FechaRegistro     DATETIME2(0) NOT NULL CONSTRAINT DF_Paciente_FechaRegistro DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Paciente PRIMARY KEY (IdPaciente),
    CONSTRAINT UQ_Paciente_Documento UNIQUE (TipoDocumento, NumeroDocumento),
    CONSTRAINT FK_Paciente_Distrito FOREIGN KEY (IdDistrito) REFERENCES vac.Distrito (IdDistrito),
    CONSTRAINT CK_Paciente_TipoDocumento CHECK (TipoDocumento IN ('DNI','CNV','CE','PAS')),
    CONSTRAINT CK_Paciente_Sexo CHECK (Sexo IN ('F','M')),
    CONSTRAINT CK_Paciente_FechaNacimiento CHECK (FechaNacimiento >= '1900-01-01')
);

/* ---------------------------------------------------------------------
   Catálogo de vacunas
   Una vacuna puede prevenir varias enfermedades (SPR: sarampión,
   paperas y rubéola), por eso la relación es N:M.
   --------------------------------------------------------------------- */
CREATE TABLE vac.Enfermedad (
    IdEnfermedad  TINYINT      NOT NULL IDENTITY(1,1),
    Nombre        VARCHAR(50)  NOT NULL,
    CodigoCie10   VARCHAR(5)   NOT NULL,
    CONSTRAINT PK_Enfermedad PRIMARY KEY (IdEnfermedad),
    CONSTRAINT UQ_Enfermedad_Nombre UNIQUE (Nombre)
);

CREATE TABLE vac.Vacuna (
    IdVacuna      TINYINT      NOT NULL IDENTITY(1,1),
    Codigo        VARCHAR(10)  NOT NULL,
    Nombre        VARCHAR(80)  NOT NULL,
    Via           VARCHAR(15)  NOT NULL,
    CONSTRAINT PK_Vacuna PRIMARY KEY (IdVacuna),
    CONSTRAINT UQ_Vacuna_Codigo UNIQUE (Codigo),
    CONSTRAINT CK_Vacuna_Via CHECK (Via IN ('Intramuscular','Subcutánea','Intradérmica','Oral'))
);

CREATE TABLE vac.VacunaEnfermedad (
    IdVacuna      TINYINT      NOT NULL,
    IdEnfermedad  TINYINT      NOT NULL,
    CONSTRAINT PK_VacunaEnfermedad PRIMARY KEY (IdVacuna, IdEnfermedad),
    CONSTRAINT FK_VE_Vacuna FOREIGN KEY (IdVacuna) REFERENCES vac.Vacuna (IdVacuna),
    CONSTRAINT FK_VE_Enfermedad FOREIGN KEY (IdEnfermedad) REFERENCES vac.Enfermedad (IdEnfermedad)
);

/* Esquema nacional: qué dosis corresponde a qué edad.
   IntervaloMinDias = días mínimos desde la dosis anterior de la misma vacuna. */
CREATE TABLE vac.EsquemaDosis (
    IdEsquema         SMALLINT     NOT NULL IDENTITY(1,1),
    IdVacuna          TINYINT      NOT NULL,
    NumeroDosis       TINYINT      NOT NULL,
    Descripcion       VARCHAR(40)  NOT NULL,
    EdadMinimaMeses   SMALLINT     NOT NULL,
    EdadMaximaMeses   SMALLINT     NULL,
    IntervaloMinDias  SMALLINT     NOT NULL CONSTRAINT DF_Esquema_Intervalo DEFAULT (0),
    CONSTRAINT PK_EsquemaDosis PRIMARY KEY (IdEsquema),
    CONSTRAINT UQ_Esquema_VacunaDosis UNIQUE (IdVacuna, NumeroDosis),
    CONSTRAINT FK_Esquema_Vacuna FOREIGN KEY (IdVacuna) REFERENCES vac.Vacuna (IdVacuna),
    CONSTRAINT CK_Esquema_Numero CHECK (NumeroDosis BETWEEN 1 AND 5),
    CONSTRAINT CK_Esquema_Edades CHECK (EdadMinimaMeses >= 0 AND (EdadMaximaMeses IS NULL OR EdadMaximaMeses >= EdadMinimaMeses)),
    CONSTRAINT CK_Esquema_Intervalo CHECK (IntervaloMinDias >= 0)
);

CREATE TABLE vac.LoteVacuna (
    IdLote            INT          NOT NULL IDENTITY(1,1),
    IdVacuna          TINYINT      NOT NULL,
    NumeroLote        VARCHAR(20)  NOT NULL,
    Laboratorio       VARCHAR(50)  NOT NULL,
    FechaVencimiento  DATE         NOT NULL,
    CONSTRAINT PK_LoteVacuna PRIMARY KEY (IdLote),
    CONSTRAINT UQ_Lote_Numero UNIQUE (IdVacuna, NumeroLote),
    CONSTRAINT FK_Lote_Vacuna FOREIGN KEY (IdVacuna) REFERENCES vac.Vacuna (IdVacuna)
);

/* ---------------------------------------------------------------------
   Campañas y brotes
   --------------------------------------------------------------------- */
CREATE TABLE vac.Campana (
    IdCampana     SMALLINT     NOT NULL IDENTITY(1,1),
    Nombre        VARCHAR(100) NOT NULL,
    FechaInicio   DATE         NOT NULL,
    FechaFin      DATE         NOT NULL,
    Descripcion   VARCHAR(300) NULL,
    CONSTRAINT PK_Campana PRIMARY KEY (IdCampana),
    CONSTRAINT CK_Campana_Fechas CHECK (FechaFin >= FechaInicio)
);

CREATE TABLE vac.CampanaDistrito (
    IdCampana     SMALLINT     NOT NULL,
    IdDistrito    SMALLINT     NOT NULL,
    MetaDosis     INT          NOT NULL,
    CONSTRAINT PK_CampanaDistrito PRIMARY KEY (IdCampana, IdDistrito),
    CONSTRAINT FK_CD_Campana FOREIGN KEY (IdCampana) REFERENCES vac.Campana (IdCampana),
    CONSTRAINT FK_CD_Distrito FOREIGN KEY (IdDistrito) REFERENCES vac.Distrito (IdDistrito),
    CONSTRAINT CK_CD_Meta CHECK (MetaDosis > 0)
);

/* Un brote está activo mientras FechaFin sea NULL. */
CREATE TABLE vac.Brote (
    IdBrote          INT          NOT NULL IDENTITY(1,1),
    IdEnfermedad     TINYINT      NOT NULL,
    IdDistrito       SMALLINT     NOT NULL,
    FechaInicio      DATE         NOT NULL,
    FechaFin         DATE         NULL,
    CasosConfirmados SMALLINT     NOT NULL CONSTRAINT DF_Brote_Casos DEFAULT (0),
    CONSTRAINT PK_Brote PRIMARY KEY (IdBrote),
    CONSTRAINT FK_Brote_Enfermedad FOREIGN KEY (IdEnfermedad) REFERENCES vac.Enfermedad (IdEnfermedad),
    CONSTRAINT FK_Brote_Distrito FOREIGN KEY (IdDistrito) REFERENCES vac.Distrito (IdDistrito),
    CONSTRAINT CK_Brote_Fechas CHECK (FechaFin IS NULL OR FechaFin >= FechaInicio),
    CONSTRAINT CK_Brote_Casos CHECK (CasosConfirmados >= 0)
);
/* Solo un brote activo por enfermedad y distrito. */
CREATE UNIQUE INDEX UX_Brote_Activo ON vac.Brote (IdEnfermedad, IdDistrito) WHERE FechaFin IS NULL;

/* ---------------------------------------------------------------------
   Registro central: dosis aplicadas
   La vacuna se obtiene del esquema (IdEsquema) y del lote; el trigger de
   validación asegura que ambos coincidan.
   --------------------------------------------------------------------- */
CREATE TABLE vac.DosisAplicada (
    IdDosis           BIGINT       NOT NULL IDENTITY(1,1),
    IdPaciente        INT          NOT NULL,
    IdEsquema         SMALLINT     NOT NULL,
    IdLote            INT          NOT NULL,
    IdEstablecimiento SMALLINT     NOT NULL,
    IdVacunador        INT          NOT NULL,
    IdCampana         SMALLINT     NULL,
    FechaAplicacion   DATE         NOT NULL,
    FechaRegistro     DATETIME2(0) NOT NULL CONSTRAINT DF_Dosis_FechaRegistro DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_DosisAplicada PRIMARY KEY (IdDosis),
    CONSTRAINT UQ_Dosis_PacienteEsquema UNIQUE (IdPaciente, IdEsquema),
    CONSTRAINT FK_Dosis_Paciente FOREIGN KEY (IdPaciente) REFERENCES vac.Paciente (IdPaciente),
    CONSTRAINT FK_Dosis_Esquema FOREIGN KEY (IdEsquema) REFERENCES vac.EsquemaDosis (IdEsquema),
    CONSTRAINT FK_Dosis_Lote FOREIGN KEY (IdLote) REFERENCES vac.LoteVacuna (IdLote),
    CONSTRAINT FK_Dosis_Establecimiento FOREIGN KEY (IdEstablecimiento) REFERENCES vac.EstablecimientoSalud (IdEstablecimiento),
    CONSTRAINT FK_Dosis_Vacunador FOREIGN KEY (IdVacunador) REFERENCES vac.Vacunador (IdVacunador),
    CONSTRAINT FK_Dosis_Campana FOREIGN KEY (IdCampana) REFERENCES vac.Campana (IdCampana)
);

/* ---------------------------------------------------------------------
   Alertas y auditoría (alimentadas por triggers y procedimientos)
   --------------------------------------------------------------------- */
CREATE TABLE vac.Alerta (
    IdAlerta          BIGINT       NOT NULL IDENTITY(1,1),
    IdPaciente        INT          NOT NULL,
    IdEsquema         SMALLINT     NOT NULL,
    IdBrote           INT          NULL,
    TipoAlerta        VARCHAR(20)  NOT NULL,
    FechaGeneracion   DATETIME2(0) NOT NULL CONSTRAINT DF_Alerta_Fecha DEFAULT (SYSDATETIME()),
    Estado            VARCHAR(10)  NOT NULL CONSTRAINT DF_Alerta_Estado DEFAULT ('PENDIENTE'),
    FechaAtencion     DATETIME2(0) NULL,
    CONSTRAINT PK_Alerta PRIMARY KEY (IdAlerta),
    CONSTRAINT FK_Alerta_Paciente FOREIGN KEY (IdPaciente) REFERENCES vac.Paciente (IdPaciente),
    CONSTRAINT FK_Alerta_Esquema FOREIGN KEY (IdEsquema) REFERENCES vac.EsquemaDosis (IdEsquema),
    CONSTRAINT FK_Alerta_Brote FOREIGN KEY (IdBrote) REFERENCES vac.Brote (IdBrote),
    CONSTRAINT CK_Alerta_Tipo CHECK (TipoAlerta IN ('ZONA_BROTE','DOSIS_ATRASADA')),
    CONSTRAINT CK_Alerta_Estado CHECK (Estado IN ('PENDIENTE','ATENDIDA','DESCARTADA')),
    CONSTRAINT CK_Alerta_Atencion CHECK ((Estado = 'PENDIENTE' AND FechaAtencion IS NULL) OR (Estado <> 'PENDIENTE' AND FechaAtencion IS NOT NULL))
);
/* Evita alertas pendientes duplicadas para la misma dosis. */
CREATE UNIQUE INDEX UX_Alerta_Pendiente ON vac.Alerta (IdPaciente, IdEsquema) WHERE Estado = 'PENDIENTE';

CREATE TABLE vac.AuditoriaDosis (
    IdAuditoria       BIGINT       NOT NULL IDENTITY(1,1),
    IdDosis           BIGINT       NOT NULL,
    Operacion         CHAR(1)      NOT NULL,
    DatosAnteriores   NVARCHAR(MAX) NULL,
    DatosNuevos       NVARCHAR(MAX) NULL,
    Usuario           SYSNAME      NOT NULL CONSTRAINT DF_Auditoria_Usuario DEFAULT (SUSER_SNAME()),
    Fecha             DATETIME2(0) NOT NULL CONSTRAINT DF_Auditoria_Fecha DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_AuditoriaDosis PRIMARY KEY (IdAuditoria),
    CONSTRAINT CK_Auditoria_Operacion CHECK (Operacion IN ('U','D'))
);

/* ---------------------------------------------------------------------
   Índices nonclustered para las consultas de cobertura y alertas
   (ver Recursos/S3.0_Indices: índices compuestos y de cobertura)
   --------------------------------------------------------------------- */
CREATE INDEX IX_Paciente_Distrito    ON vac.Paciente (IdDistrito) INCLUDE (FechaNacimiento);
CREATE INDEX IX_Paciente_Apellidos   ON vac.Paciente (ApellidoPaterno, ApellidoMaterno, Nombres);
CREATE INDEX IX_Dosis_Esquema        ON vac.DosisAplicada (IdEsquema) INCLUDE (IdPaciente, FechaAplicacion);
CREATE INDEX IX_Dosis_Fecha          ON vac.DosisAplicada (FechaAplicacion) INCLUDE (IdEstablecimiento, IdCampana);
CREATE INDEX IX_Dosis_Campana        ON vac.DosisAplicada (IdCampana) WHERE IdCampana IS NOT NULL;
CREATE INDEX IX_Alerta_Estado        ON vac.Alerta (Estado, TipoAlerta) INCLUDE (IdPaciente, IdEsquema, IdBrote);
CREATE INDEX IX_Establecimiento_Dist ON vac.EstablecimientoSalud (IdDistrito);
GO

/* ---------------------------------------------------------------------
   Función auxiliar: edad en meses cumplidos a una fecha de referencia.
   Es escalar y determinista; SQL Server 2019+ la inlinea (scalar UDF inlining).
   --------------------------------------------------------------------- */
CREATE FUNCTION vac.fn_EdadMeses (@FechaNacimiento DATE, @FechaReferencia DATE)
RETURNS INT
WITH SCHEMABINDING
AS
BEGIN
    RETURN DATEDIFF(MONTH, @FechaNacimiento, @FechaReferencia)
         - CASE WHEN DAY(@FechaReferencia) < DAY(@FechaNacimiento) THEN 1 ELSE 0 END;
END
GO
