-- Table-valued parameter: the whole master-data file travels to the server in one round trip.
IF TYPE_ID(N'dbo.ProductTableType') IS NULL
    CREATE TYPE dbo.ProductTableType AS TABLE
    (
        sku        nvarchar(64)  NOT NULL PRIMARY KEY,
        name       nvarchar(200) NOT NULL,
        uom        nvarchar(8)   NOT NULL,
        weight_kg  decimal(9,3)  NULL,
        barcode    nvarchar(32)  NULL
    );
GO

-- Bulk upsert of product master data.
-- UPDATE-then-INSERT instead of MERGE: MERGE has a long history of bugs and needs HOLDLOCK to be
-- race-free anyway. Two plain statements under UPDLOCK + SERIALIZABLE are race-free, easy to read and
-- give exact counts. Rows whose values did not change are not touched (no needless updated_at churn).
CREATE OR ALTER PROCEDURE dbo.usp_UpsertProducts
    @Products dbo.ProductTableType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @updated int, @inserted int;

    BEGIN TRANSACTION;

    UPDATE p
    SET    p.name       = s.name,
           p.uom        = s.uom,
           p.weight_kg  = s.weight_kg,
           p.barcode    = s.barcode,
           p.updated_at = SYSUTCDATETIME()
    FROM   dbo.products AS p WITH (UPDLOCK, SERIALIZABLE)
    JOIN   @Products    AS s ON s.sku = p.sku
    -- NULL-safe "anything changed?" check: EXCEPT treats NULL = NULL.
    WHERE  EXISTS (SELECT p.name, p.uom, p.weight_kg, p.barcode
                   EXCEPT
                   SELECT s.name, s.uom, s.weight_kg, s.barcode);
    SET @updated = @@ROWCOUNT;

    INSERT dbo.products (sku, name, uom, weight_kg, barcode)
    SELECT s.sku, s.name, s.uom, s.weight_kg, s.barcode
    FROM   @Products AS s
    WHERE  NOT EXISTS (SELECT 1 FROM dbo.products AS p WITH (UPDLOCK, SERIALIZABLE) WHERE p.sku = s.sku);
    SET @inserted = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT @inserted AS inserted,
           @updated  AS updated,
           (SELECT COUNT(*) FROM @Products) - @inserted - @updated AS unchanged;
END
GO
