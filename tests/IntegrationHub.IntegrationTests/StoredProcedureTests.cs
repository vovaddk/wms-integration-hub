using IntegrationHub.Core.Canonical;
using IntegrationHub.Data;

namespace IntegrationHub.IntegrationTests;

public sealed class StoredProcedureTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly ProductRepository _products = new(sql.Db);
    private readonly ReportRepository _reports = new(sql.Db);

    public async ValueTask InitializeAsync() => await sql.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task UpsertProducts_inserts_updates_and_skips_unchanged_rows()
    {
        var first = await _products.UpsertAsync(
            [new ProductRecord("A", "Alpha", "EA", 1.5m, null), new ProductRecord("B", "Beta", "EA", null, "123")],
            CancellationToken.None);
        first.ShouldBe(new UpsertResult(Inserted: 2, Updated: 0, Unchanged: 0));

        var second = await _products.UpsertAsync(
            [
                new ProductRecord("A", "Alpha v2", "EA", 1.5m, null), // changed
                new ProductRecord("B", "Beta", "EA", null, "123"),    // identical, incl. NULL weight
                new ProductRecord("C", "Gamma", "PCS", null, null),   // new
            ],
            CancellationToken.None);
        second.ShouldBe(new UpsertResult(Inserted: 1, Updated: 1, Unchanged: 1));

        (await sql.ScalarAsync<string>("SELECT name FROM dbo.products WHERE sku = N'A'")).ShouldBe("Alpha v2");
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.products")).ShouldBe(3);
    }

    [Fact]
    public async Task StockTurnoverReport_computes_running_balance_lag_and_turnover()
    {
        await _products.UpsertAsync([new ProductRecord("X", "X", "EA", null, null), new ProductRecord("Y", "Y", "EA", null, null)], CancellationToken.None);
        await sql.ExecuteAsync(
            """
            INSERT dbo.stock_movements (sku, quantity, movement_type, reference, occurred_at) VALUES
              (N'X',  100, N'RECEIPT',    N'r0', '2026-10-31T09:00:00'),  -- before the period: opening balance
              (N'X',   50, N'RECEIPT',    N'r1', '2026-11-01T08:00:00'),
              (N'X',  -20, N'ALLOCATION', N'o1', '2026-11-01T15:00:00'),
              (N'X',  -30, N'ALLOCATION', N'o2', '2026-11-02T10:00:00'),
              (N'X',  -10, N'ALLOCATION', N'o3', '2026-11-03T11:00:00'),
              (N'X',    5, N'ADJUSTMENT', N'a1', '2026-11-03T23:59:59'),
              (N'X',  -99, N'ALLOCATION', N'o4', '2026-11-04T00:00:00'),  -- after the period
              (N'Y',    7, N'RECEIPT',    N'r2', '2026-11-02T12:00:00');
            """);

        var rows = await _reports.GetStockTurnoverAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3), "X", CancellationToken.None);

        rows.Select(r => (r.MovementDate.Day, r.QtyIn, r.QtyOut, r.NetChange, r.OpeningBalance, r.ClosingBalance, r.QtyOutChange))
            .ShouldBe([
                (1, 50, 20, 30, 100, 130, (int?)null),
                (2, 0, 30, -30, 130, 100, 10),
                (3, 5, 10, -5, 100, 95, -20),
            ]);
        // 60 units out / average closing balance (130 + 100 + 95) / 3
        rows[0].TurnoverRatio!.Value.ShouldBe(0.5538m);

        var allSkus = await _reports.GetStockTurnoverAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3), null, CancellationToken.None);
        allSkus.Select(r => r.Sku).Distinct().ShouldBe(["X", "Y"]);
    }

    [Fact]
    public async Task IntegrationStats_aggregates_per_source_and_day()
    {
        await sql.ExecuteAsync(
            """
            INSERT dbo.inbox (source_system, message_type, payload, correlation_id, status, attempt_count, received_at, processed_at) VALUES
              (N'erp',  N'order', N'{}', N'c1', N'Processed',    1, '2026-11-01T10:00:00', '2026-11-01T10:00:01'),
              (N'erp',  N'order', N'{}', N'c2', N'Processed',    2, '2026-11-01T11:00:00', '2026-11-01T11:00:03'),
              (N'erp',  N'order', N'{}', N'c3', N'DeadLettered', 5, '2026-11-01T12:00:00', NULL),
              (N'shop', N'order', N'{}', N'c4', N'Failed',       1, '2026-11-01T13:00:00', NULL),
              (N'erp',  N'asn',   N'{}', N'c5', N'Received',     0, '2026-11-02T09:00:00', NULL),
              (N'erp',  N'asn',   N'{}', N'c6', N'Processed',    1, '2026-11-05T09:00:00', '2026-11-05T09:00:01');
            """);

        var rows = await _reports.GetIntegrationStatsAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), CancellationToken.None);

        rows.Count.ShouldBe(3);
        var erpDay1 = rows.Single(r => r.SourceSystem == "erp" && r.StatDate.Day == 1);
        erpDay1.ShouldBe(new IntegrationStatsRow("erp", new DateTime(2026, 11, 1), Total: 3, Processed: 2, Pending: 0, Retrying: 0,
            DeadLettered: 1, NeededRetry: 2, AvgLatencyMs: 2000, FailureRate: 0.3333m));
        rows.Single(r => r.SourceSystem == "shop").Retrying.ShouldBe(1);
        rows.Single(r => r.StatDate.Day == 2).Pending.ShouldBe(1);
    }
}
