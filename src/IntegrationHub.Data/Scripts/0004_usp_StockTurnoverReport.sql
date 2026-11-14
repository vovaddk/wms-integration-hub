-- Daily stock movement report per SKU for [@From, @To] (both inclusive):
-- inbound/outbound per day, running closing balance, day-over-day outbound change and a turnover ratio.
CREATE OR ALTER PROCEDURE dbo.usp_StockTurnoverReport
    @From date,
    @To   date,
    @Sku  nvarchar(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @fromTs datetime2(3) = @From,
            @toTs   datetime2(3) = DATEADD(day, 1, CAST(@To AS datetime2(3)));  -- half-open range: index-friendly

    WITH opening AS
    (
        -- Balance carried into the period.
        SELECT   sm.sku, SUM(sm.quantity) AS opening_qty
        FROM     dbo.stock_movements AS sm
        WHERE    sm.occurred_at < @fromTs
          AND    (@Sku IS NULL OR sm.sku = @Sku)
        GROUP BY sm.sku
    ),
    daily AS
    (
        SELECT   sm.sku,
                 CAST(sm.occurred_at AS date)                                AS movement_date,
                 SUM(CASE WHEN sm.quantity > 0 THEN  sm.quantity ELSE 0 END) AS qty_in,
                 SUM(CASE WHEN sm.quantity < 0 THEN -sm.quantity ELSE 0 END) AS qty_out,
                 SUM(sm.quantity)                                            AS net_change
        FROM     dbo.stock_movements AS sm
        WHERE    sm.occurred_at >= @fromTs
          AND    sm.occurred_at <  @toTs
          AND    (@Sku IS NULL OR sm.sku = @Sku)
        GROUP BY sm.sku, CAST(sm.occurred_at AS date)
    ),
    balances AS
    (
        SELECT d.*,
               -- Running total. ROWS (not the default RANGE) avoids the on-disk worktable spool.
               COALESCE(o.opening_qty, 0)
                 + SUM(d.net_change) OVER (PARTITION BY d.sku ORDER BY d.movement_date
                                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS closing_balance
        FROM   daily AS d
        LEFT JOIN opening AS o ON o.sku = d.sku
    )
    SELECT   b.sku,
             b.movement_date,
             b.qty_in,
             b.qty_out,
             b.net_change,
             b.closing_balance - b.net_change AS opening_balance,
             b.closing_balance,
             b.qty_out - LAG(b.qty_out) OVER (PARTITION BY b.sku ORDER BY b.movement_date) AS qty_out_change,
             -- Turnover = units shipped in the period / average closing balance on days with movements.
             CAST(SUM(b.qty_out) OVER (PARTITION BY b.sku)
                  / NULLIF(AVG(CAST(b.closing_balance AS decimal(18,4))) OVER (PARTITION BY b.sku), 0)
                  AS decimal(18,4)) AS turnover_ratio
    FROM     balances AS b
    ORDER BY b.sku, b.movement_date
    OPTION (RECOMPILE);  -- "@Sku IS NULL OR ..." catch-all: get a plan for the actual parameter values.
END
GO
