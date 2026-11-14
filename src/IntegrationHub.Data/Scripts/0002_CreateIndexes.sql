-- Filtered index: only rows a worker may still claim are in it, so it stays tiny no matter how large the
-- processed history grows. The claim query repeats this exact predicate with literals, which is what lets
-- the optimizer match a filtered index (it cannot match it against a parameter).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_inbox_pending' AND object_id = OBJECT_ID(N'dbo.inbox'))
    CREATE INDEX IX_inbox_pending
        ON dbo.inbox (next_attempt_at)
        INCLUDE (status)
        WHERE status IN (N'Received', N'Failed', N'Processing');
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_outbox_pending' AND object_id = OBJECT_ID(N'dbo.outbox'))
    CREATE INDEX IX_outbox_pending
        ON dbo.outbox (next_attempt_at)
        INCLUDE (status)
        WHERE status IN (N'Pending', N'Failed', N'Processing');
GO

-- Covering index for usp_IntegrationStats: range seek on received_at, every other column it reads is included.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_inbox_received_at' AND object_id = OBJECT_ID(N'dbo.inbox'))
    CREATE INDEX IX_inbox_received_at
        ON dbo.inbox (received_at)
        INCLUDE (source_system, status, processed_at, attempt_count);
GO

-- Admin list of dead letters, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_inbox_dead_lettered' AND object_id = OBJECT_ID(N'dbo.inbox'))
    CREATE INDEX IX_inbox_dead_lettered
        ON dbo.inbox (received_at DESC)
        WHERE status = N'DeadLettered';
GO

-- Covering index for usp_StockTurnoverReport: rows arrive already sorted by (sku, occurred_at),
-- which is exactly the PARTITION BY / ORDER BY of its window functions, so no sort operator is needed.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_stock_movements_sku_occurred_at' AND object_id = OBJECT_ID(N'dbo.stock_movements'))
    CREATE INDEX IX_stock_movements_sku_occurred_at
        ON dbo.stock_movements (sku, occurred_at)
        INCLUDE (quantity);
GO
