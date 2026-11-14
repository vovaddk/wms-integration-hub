using System.Data;
using Dapper;

namespace IntegrationHub.Data;

public sealed record IntegrationStatsRow(
    string SourceSystem,
    DateTime StatDate,
    int Total,
    int Processed,
    int Pending,
    int Retrying,
    int DeadLettered,
    int NeededRetry,
    long? AvgLatencyMs,
    decimal FailureRate);

public sealed record StockTurnoverRow(
    string Sku,
    DateTime MovementDate,
    int QtyIn,
    int QtyOut,
    int NetChange,
    int OpeningBalance,
    int ClosingBalance,
    int? QtyOutChange,
    decimal? TurnoverRatio);

public sealed class ReportRepository(SqlConnectionFactory db)
{
    // The procedures return snake_case columns (idiomatic T-SQL); let Dapper map them to PascalCase members.
    static ReportRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public async Task<IReadOnlyList<IntegrationStatsRow>> GetIntegrationStatsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<IntegrationStatsRow>(new CommandDefinition(
            "dbo.usp_IntegrationStats",
            DateRange(from, to),
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StockTurnoverRow>> GetStockTurnoverAsync(DateOnly from, DateOnly to, string? sku, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<StockTurnoverRow>(new CommandDefinition(
            "dbo.usp_StockTurnoverReport",
            DateRange(from, to, sku),
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
        return rows.AsList();
    }

    private static DynamicParameters DateRange(DateOnly from, DateOnly to, string? sku = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("From", from.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("To", to.ToDateTime(TimeOnly.MinValue), DbType.Date);
        if (sku is not null)
        {
            parameters.Add("Sku", sku);
        }

        return parameters;
    }
}
