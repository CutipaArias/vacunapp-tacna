/* =====================================================================
   VacunApp Tacna - 09. Stock de vacunas por lote y establecimiento
   Módulo stock (RF-09, CU09; RN-10 … RN-13). Es idempotente: se puede
   ejecutar N veces, también sobre una base con datos (no borra nada).

   Va después de 06_datos_prueba: las dosis históricas de la carga de
   prueba se insertaron antes de que existiera el control de stock, así
   que el inventario sembrado aquí es un inventario inicial y no se
   reconcilia con ellas.
   ===================================================================== */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
USE VacunAppTacna;
GO

/* ---------------------------------------------------------------------
   StockLote: existencias de un lote en un establecimiento.
   Cantidad nunca es negativa (CHECK): es la última defensa contra una
   carrera entre dos dosis que compiten por la última unidad.
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.StockLote', N'U') IS NULL
CREATE TABLE vac.StockLote (
    IdStock           INT      NOT NULL IDENTITY(1,1),
    IdLote            INT      NOT NULL,
    IdEstablecimiento SMALLINT NOT NULL,
    Cantidad          INT      NOT NULL CONSTRAINT DF_Stock_Cantidad DEFAULT (0),
    UmbralMinimo      INT      NOT NULL CONSTRAINT DF_Stock_Umbral DEFAULT (0),
    CONSTRAINT PK_StockLote PRIMARY KEY (IdStock),
    CONSTRAINT UQ_Stock_LoteEstablecimiento UNIQUE (IdLote, IdEstablecimiento),
    CONSTRAINT FK_Stock_Lote FOREIGN KEY (IdLote) REFERENCES vac.LoteVacuna (IdLote),
    CONSTRAINT FK_Stock_Establecimiento FOREIGN KEY (IdEstablecimiento) REFERENCES vac.EstablecimientoSalud (IdEstablecimiento),
    CONSTRAINT CK_Stock_Cantidad CHECK (Cantidad >= 0),
    CONSTRAINT CK_Stock_Umbral CHECK (UmbralMinimo >= 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Stock_Establecimiento')
    CREATE INDEX IX_Stock_Establecimiento ON vac.StockLote (IdEstablecimiento) INCLUDE (IdLote, Cantidad, UmbralMinimo);
GO

/* ---------------------------------------------------------------------
   MovimientoStock: bitácora de entradas, salidas y ajustes. Cantidad lleva
   signo (+ entra, - sale). IdDosis no es clave foránea a propósito: el
   historial del inventario debe sobrevivir a la corrección de una dosis.
   --------------------------------------------------------------------- */
IF OBJECT_ID(N'vac.MovimientoStock', N'U') IS NULL
CREATE TABLE vac.MovimientoStock (
    IdMovimiento BIGINT        NOT NULL IDENTITY(1,1),
    IdStock      INT           NOT NULL,
    Tipo         VARCHAR(10)   NOT NULL,
    Cantidad     INT           NOT NULL,
    Motivo       VARCHAR(200)  NOT NULL,
    IdDosis      BIGINT        NULL,
    Usuario      NVARCHAR(128) NOT NULL CONSTRAINT DF_Mov_Usuario DEFAULT (COALESCE(CAST(SESSION_CONTEXT(N'usuario') AS NVARCHAR(128)), SUSER_SNAME())),
    Fecha        DATETIME2(0)  NOT NULL CONSTRAINT DF_Mov_Fecha DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_MovimientoStock PRIMARY KEY (IdMovimiento),
    CONSTRAINT FK_Mov_Stock FOREIGN KEY (IdStock) REFERENCES vac.StockLote (IdStock),
    CONSTRAINT CK_Mov_Tipo CHECK (Tipo IN ('ENTRADA', 'SALIDA', 'AJUSTE')),
    CONSTRAINT CK_Mov_Signo CHECK ((Tipo = 'ENTRADA' AND Cantidad > 0) OR (Tipo = 'SALIDA' AND Cantidad < 0) OR (Tipo = 'AJUSTE' AND Cantidad <> 0))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Mov_Stock')
    CREATE INDEX IX_Mov_Stock ON vac.MovimientoStock (IdStock, Fecha DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Mov_Dosis')
    CREATE INDEX IX_Mov_Dosis ON vac.MovimientoStock (IdDosis) WHERE IdDosis IS NOT NULL;
GO

/* ---------------------------------------------------------------------
   Alerta admite ahora alertas de stock (sin paciente ni esquema).
   Los índices que usan esas columnas se sueltan para poder volverlas
   NULL y se recrean enseguida.
   --------------------------------------------------------------------- */
IF COLUMNPROPERTY(OBJECT_ID(N'vac.Alerta'), N'IdPaciente', 'AllowsNull') = 0
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Alerta_Pendiente' AND object_id = OBJECT_ID(N'vac.Alerta'))
        DROP INDEX UX_Alerta_Pendiente ON vac.Alerta;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Alerta_Estado' AND object_id = OBJECT_ID(N'vac.Alerta'))
        DROP INDEX IX_Alerta_Estado ON vac.Alerta;
    ALTER TABLE vac.Alerta ALTER COLUMN IdPaciente INT NULL;
    ALTER TABLE vac.Alerta ALTER COLUMN IdEsquema SMALLINT NULL;
END
GO
IF COL_LENGTH(N'vac.Alerta', N'IdStock') IS NULL
    ALTER TABLE vac.Alerta ADD IdStock INT NULL CONSTRAINT FK_Alerta_Stock FOREIGN KEY (IdStock) REFERENCES vac.StockLote (IdStock);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Alerta_Tipo' AND definition LIKE N'%STOCK_BAJO%')
BEGIN
    ALTER TABLE vac.Alerta DROP CONSTRAINT CK_Alerta_Tipo;
    ALTER TABLE vac.Alerta ADD CONSTRAINT CK_Alerta_Tipo
        CHECK (TipoAlerta IN ('ZONA_BROTE', 'DOSIS_ATRASADA', 'STOCK_BAJO', 'LOTE_POR_VENCER'));
END
GO
/* Una alerta de paciente exige paciente y esquema; una de stock exige la fila de stock y nada de paciente. */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Alerta_Origen')
    ALTER TABLE vac.Alerta ADD CONSTRAINT CK_Alerta_Origen CHECK (
        (TipoAlerta IN ('ZONA_BROTE', 'DOSIS_ATRASADA') AND IdPaciente IS NOT NULL AND IdEsquema IS NOT NULL AND IdStock IS NULL)
     OR (TipoAlerta IN ('STOCK_BAJO', 'LOTE_POR_VENCER') AND IdStock IS NOT NULL AND IdPaciente IS NULL AND IdEsquema IS NULL));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Alerta_Pendiente' AND object_id = OBJECT_ID(N'vac.Alerta'))
    CREATE UNIQUE INDEX UX_Alerta_Pendiente ON vac.Alerta (IdPaciente, IdEsquema)
        WHERE Estado = 'PENDIENTE' AND IdPaciente IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Alerta_Estado' AND object_id = OBJECT_ID(N'vac.Alerta'))
    CREATE INDEX IX_Alerta_Estado ON vac.Alerta (Estado, TipoAlerta) INCLUDE (IdPaciente, IdEsquema, IdBrote, IdStock);
/* Evita alertas de stock pendientes duplicadas para la misma fila y tipo. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Alerta_StockPendiente' AND object_id = OBJECT_ID(N'vac.Alerta'))
    CREATE UNIQUE INDEX UX_Alerta_StockPendiente ON vac.Alerta (IdStock, TipoAlerta)
        WHERE Estado = 'PENDIENTE' AND IdStock IS NOT NULL;
GO

/* ---------------------------------------------------------------------
   trg_DosisAplicada_DescontarStock  (AFTER INSERT)         RN-10, RN-11
   Descuenta una unidad del lote en el establecimiento por cada dosis. El
   UPDATE condicionado (Cantidad >= N) es atómico: de dos dosis que
   compiten por la última unidad, la segunda no encuentra fila que
   descontar y se rechaza. Va después de Validar (orden First) para que
   los motivos clínicos tengan prioridad sobre el de stock.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_DosisAplicada_DescontarStock
ON vac.DosisAplicada
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    DECLARE @g TABLE (IdLote INT NOT NULL, IdEstablecimiento SMALLINT NOT NULL, N INT NOT NULL, PRIMARY KEY (IdLote, IdEstablecimiento));
    INSERT @g (IdLote, IdEstablecimiento, N)
    SELECT IdLote, IdEstablecimiento, COUNT(*) FROM inserted GROUP BY IdLote, IdEstablecimiento;

    UPDATE s
    SET Cantidad = s.Cantidad - g.N
    FROM vac.StockLote s
    JOIN @g g ON g.IdLote = s.IdLote AND g.IdEstablecimiento = s.IdEstablecimiento
    WHERE s.Cantidad >= g.N;

    IF @@ROWCOUNT < (SELECT COUNT(*) FROM @g)
        THROW 50107, 'No hay stock suficiente del lote en el establecimiento: no se puede aplicar la dosis.', 1;

    INSERT vac.MovimientoStock (IdStock, Tipo, Cantidad, Motivo, IdDosis)
    SELECT s.IdStock, 'SALIDA', -1, 'Dosis aplicada', i.IdDosis
    FROM inserted i
    JOIN vac.StockLote s ON s.IdLote = i.IdLote AND s.IdEstablecimiento = i.IdEstablecimiento;
END
GO

/* ---------------------------------------------------------------------
   trg_StockLote_Alerta  (AFTER INSERT, UPDATE)                    RN-12
   Al llegar al umbral mínimo (o bajar de él) crea una alerta STOCK_BAJO
   si no hay otra pendiente; al reponer por encima del umbral la cierra.
   --------------------------------------------------------------------- */
CREATE OR ALTER TRIGGER vac.trg_StockLote_Alerta
ON vac.StockLote
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    UPDATE a
    SET Estado = 'ATENDIDA', FechaAtencion = SYSDATETIME()
    FROM vac.Alerta a
    JOIN inserted i ON i.IdStock = a.IdStock
    WHERE a.Estado = 'PENDIENTE' AND a.TipoAlerta = 'STOCK_BAJO' AND i.Cantidad > i.UmbralMinimo;

    INSERT vac.Alerta (IdStock, TipoAlerta)
    SELECT i.IdStock, 'STOCK_BAJO'
    FROM inserted i
    WHERE i.Cantidad <= i.UmbralMinimo
      AND NOT EXISTS (SELECT 1 FROM vac.Alerta a WITH (UPDLOCK, HOLDLOCK)
                      WHERE a.IdStock = i.IdStock AND a.TipoAlerta = 'STOCK_BAJO' AND a.Estado = 'PENDIENTE');
END
GO

/* ---------------------------------------------------------------------
   usp_GenerarAlertasStock                                          RN-13
   Proceso por lotes (se invoca desde la API; SQL Server Agent no existe en
   el hosting gratuito). Alerta los lotes con existencias que vencen en
   @Dias días o menos (o ya vencidos) y cierra las alertas de lotes que se
   quedaron sin existencias. No duplica alertas pendientes.
   --------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE vac.usp_GenerarAlertasStock
    @Dias            INT = 30,
    @AlertasCreadas  INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Limite DATE = DATEADD(DAY, @Dias, CAST(GETDATE() AS DATE));

    UPDATE a
    SET Estado = 'ATENDIDA', FechaAtencion = SYSDATETIME()
    FROM vac.Alerta a
    JOIN vac.StockLote s ON s.IdStock = a.IdStock
    WHERE a.Estado = 'PENDIENTE' AND a.TipoAlerta = 'LOTE_POR_VENCER' AND s.Cantidad = 0;

    INSERT vac.Alerta (IdStock, TipoAlerta)
    SELECT s.IdStock, 'LOTE_POR_VENCER'
    FROM vac.StockLote s
    JOIN vac.LoteVacuna l ON l.IdLote = s.IdLote
    WHERE s.Cantidad > 0
      AND l.FechaVencimiento <= @Limite
      AND NOT EXISTS (SELECT 1 FROM vac.Alerta a WITH (UPDLOCK, HOLDLOCK)
                      WHERE a.IdStock = s.IdStock AND a.TipoAlerta = 'LOTE_POR_VENCER' AND a.Estado = 'PENDIENTE');
    SET @AlertasCreadas = @@ROWCOUNT;
END
GO

/* ---------------------------------------------------------------------
   Inventario inicial de demostración: solo si la tabla está vacía. Cada
   establecimiento recibe 1 000 unidades de cada lote vigente, con
   umbral mínimo de 50. Queda registrado como ENTRADA.
   --------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM vac.StockLote)
BEGIN
    INSERT vac.StockLote (IdLote, IdEstablecimiento, Cantidad, UmbralMinimo)
    SELECT l.IdLote, e.IdEstablecimiento, 1000, 50
    FROM vac.LoteVacuna l
    CROSS JOIN vac.EstablecimientoSalud e
    WHERE l.FechaVencimiento >= CAST(GETDATE() AS DATE);

    INSERT vac.MovimientoStock (IdStock, Tipo, Cantidad, Motivo)
    SELECT IdStock, 'ENTRADA', Cantidad, 'Inventario inicial' FROM vac.StockLote;
END
GO
