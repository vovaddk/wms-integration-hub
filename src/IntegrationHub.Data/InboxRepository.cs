using System.Data;
using Dapper;
using IntegrationHub.Core.Idempotency;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Processing;
using Microsoft.Data.SqlClient;

namespace IntegrationHub.Data;

public sealed record DeadLetter(
    long Id, string SourceSystem, string MessageType, string CorrelationId, int AttemptCount, string? LastError, DateTime ReceivedAt);

public sealed class InboxRepository(SqlConnectionFactory db) : IInboxStore
{
    /// <summary>
    /// Stages an inbound message exactly once per (source, idempotency key).
    /// The inbox row and the key are inserted in one transaction; a concurrent duplicate loses on the
    /// primary key of idempotency_keys (not on a racy "SELECT then INSERT"), rolls back, and then reads the winner.
    /// </summary>
    public async Task<IntakeResult> ReceiveAsync(InboundMessage message, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct))
        {
            try
            {
                var inboxId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT dbo.inbox (source_system, message_type, payload, correlation_id)
                    OUTPUT inserted.id
                    VALUES (@SourceSystem, @MessageType, @Payload, @CorrelationId);
                    """,
                    message, tx, cancellationToken: ct));

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT dbo.idempotency_keys (source_system, idempotency_key, message_type, payload_hash, inbox_id)
                    VALUES (@SourceSystem, @IdempotencyKey, @MessageType, @PayloadHash, @InboxId);
                    """,
                    new { message.SourceSystem, message.IdempotencyKey, message.MessageType, message.PayloadHash, InboxId = inboxId },
                    tx, cancellationToken: ct));

                await tx.CommitAsync(ct);
                return new IntakeResult(IntakeOutcome.Accepted, inboxId);
            }
            catch (SqlException ex) when (SqlErrors.IsUniqueViolation(ex))
            {
                await tx.RollbackAsync(ct);
            }
        }

        var existing = await connection.QuerySingleAsync<StoredKey>(new CommandDefinition(
            """
            SELECT message_type AS MessageType, payload_hash AS PayloadHash, inbox_id AS InboxId
            FROM dbo.idempotency_keys
            WHERE source_system = @SourceSystem AND idempotency_key = @IdempotencyKey;
            """,
            new { message.SourceSystem, message.IdempotencyKey }, cancellationToken: ct));

        var outcome = IdempotencyDecision.ForExistingKey(existing.MessageType, existing.PayloadHash, message.MessageType, message.PayloadHash);
        return new IntakeResult(outcome, existing.InboxId);
    }

    /// <summary>
    /// Claims due rows for this worker. UPDLOCK + READPAST: rows another worker is claiming right now are
    /// skipped instead of waited on, so concurrent workers never get the same row and never block each other.
    /// The claimed row gets a lease (next_attempt_at in the future); if this worker dies, the row becomes due again.
    /// </summary>
    public async Task<IReadOnlyList<InboxMessage>> ClaimBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<InboxMessage>(new CommandDefinition(
            """
            WITH due AS
            (
                SELECT TOP (@BatchSize) *
                FROM dbo.inbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE status IN (N'Received', N'Failed', N'Processing')
                  AND next_attempt_at <= SYSUTCDATETIME()
                ORDER BY next_attempt_at
            )
            UPDATE due
            SET status = N'Processing',
                attempt_count = attempt_count + 1,
                next_attempt_at = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.id AS Id, inserted.source_system AS SourceSystem, inserted.message_type AS MessageType,
                   inserted.payload AS Payload, inserted.correlation_id AS CorrelationId, inserted.attempt_count AS AttemptCount;
            """,
            new { BatchSize = batchSize, LeaseSeconds = (int)lease.TotalSeconds }, cancellationToken: ct));
        return rows.AsList();
    }

    // All state transitions below are fenced by attempt_count: if the lease expired and another worker
    // re-claimed the row (attempt_count + 1), a late update from this worker matches nothing.
    public async Task ScheduleRetryAsync(InboxMessage message, TimeSpan delay, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.inbox
            SET status = N'Failed',
                next_attempt_at = DATEADD(millisecond, @DelayMs, SYSUTCDATETIME()),
                last_error = @Error
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount, DelayMs = (int)delay.TotalMilliseconds, Error = SqlErrors.Truncate(error) },
            cancellationToken: ct));
    }

    public async Task DeadLetterAsync(InboxMessage message, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.inbox
            SET status = N'DeadLettered', last_error = @Error
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount, Error = SqlErrors.Truncate(error) }, cancellationToken: ct));
    }

    /// <summary>Called inside the domain transaction, so "processed" commits atomically with the business change.</summary>
    internal static async Task MarkProcessedAsync(SqlConnection connection, SqlTransaction tx, InboxMessage message, CancellationToken ct)
    {
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.inbox
            SET status = N'Processed', processed_at = SYSUTCDATETIME(), last_error = NULL
            WHERE id = @Id AND attempt_count = @AttemptCount AND status = N'Processing';
            """,
            new { message.Id, message.AttemptCount }, tx, cancellationToken: ct));

        if (updated != 1)
        {
            throw new LeaseLostException(message.Id);
        }
    }

    public async Task<IReadOnlyList<DeadLetter>> ListDeadLettersAsync(int take, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<DeadLetter>(new CommandDefinition(
            """
            SELECT TOP (@Take) id AS Id, source_system AS SourceSystem, message_type AS MessageType,
                   correlation_id AS CorrelationId, attempt_count AS AttemptCount, last_error AS LastError, received_at AS ReceivedAt
            FROM dbo.inbox
            WHERE status = N'DeadLettered'
            ORDER BY received_at DESC;
            """,
            new { Take = take }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Puts a dead letter back in the queue with a fresh attempt budget. False if it is not dead-lettered.</summary>
    public async Task<bool> ReplayAsync(long id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.inbox
            SET status = N'Received', attempt_count = 0, next_attempt_at = SYSUTCDATETIME(), last_error = NULL
            WHERE id = @Id AND status = N'DeadLettered';
            """,
            new { Id = id }, cancellationToken: ct));
        return updated == 1;
    }

    private sealed record StoredKey(string MessageType, byte[] PayloadHash, long InboxId);
}
