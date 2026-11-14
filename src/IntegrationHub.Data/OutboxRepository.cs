using Dapper;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Processing;

namespace IntegrationHub.Data;

/// <summary>Same claim/lease/fencing pattern as <see cref="InboxRepository"/>, for outgoing events.</summary>
public sealed class OutboxRepository(SqlConnectionFactory db) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<OutboxMessage>(new CommandDefinition(
            """
            WITH due AS
            (
                SELECT TOP (@BatchSize) *
                FROM dbo.outbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE status IN (N'Pending', N'Failed', N'Processing')
                  AND next_attempt_at <= CAST(SYSUTCDATETIME() AS datetime2(3)) -- same rounding as the stored value
                ORDER BY next_attempt_at
            )
            UPDATE due
            SET status = N'Processing',
                attempt_count = attempt_count + 1,
                next_attempt_at = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.id AS Id, inserted.event_id AS EventId, inserted.event_type AS EventType, inserted.payload AS Payload,
                   inserted.correlation_id AS CorrelationId, inserted.attempt_count AS AttemptCount, inserted.created_at AS CreatedAt;
            """,
            new { BatchSize = batchSize, LeaseSeconds = (int)lease.TotalSeconds }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task MarkDispatchedAsync(OutboxMessage message, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.outbox
            SET status = N'Dispatched', dispatched_at = SYSUTCDATETIME(), last_error = NULL
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount }, cancellationToken: ct));
    }

    public async Task ScheduleRetryAsync(OutboxMessage message, TimeSpan delay, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.outbox
            SET status = N'Failed',
                next_attempt_at = DATEADD(millisecond, @DelayMs, SYSUTCDATETIME()),
                last_error = @Error
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount, DelayMs = (int)delay.TotalMilliseconds, Error = SqlErrors.Truncate(error) },
            cancellationToken: ct));
    }

    public async Task DeadLetterAsync(OutboxMessage message, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.outbox
            SET status = N'DeadLettered', last_error = @Error
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount, Error = SqlErrors.Truncate(error) }, cancellationToken: ct));
    }
}
