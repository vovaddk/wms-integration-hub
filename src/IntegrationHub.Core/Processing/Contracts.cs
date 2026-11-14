using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Messaging;

namespace IntegrationHub.Core.Processing;

public interface IInboxStore
{
    /// <summary>Atomically claims up to <paramref name="batchSize"/> due rows and leases them for <paramref name="lease"/>.</summary>
    Task<IReadOnlyList<InboxMessage>> ClaimBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct);

    Task ScheduleRetryAsync(InboxMessage message, TimeSpan delay, string error, CancellationToken ct);

    Task DeadLetterAsync(InboxMessage message, string error, CancellationToken ct);
}

/// <summary>
/// Applies a canonical message to the WMS. Each call is ONE transaction: domain change + outbox rows +
/// marking the inbox row processed. Returns false when the message was a business duplicate (no changes made).
/// </summary>
public interface IWmsWriter
{
    Task<bool> AcceptOrderAsync(InboxMessage message, CanonicalOrder order, CancellationToken ct);

    Task<bool> ReceiveAsnAsync(InboxMessage message, CanonicalAsn asn, CancellationToken ct);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct);

    Task MarkDispatchedAsync(OutboxMessage message, CancellationToken ct);

    Task ScheduleRetryAsync(OutboxMessage message, TimeSpan delay, string error, CancellationToken ct);

    Task DeadLetterAsync(OutboxMessage message, string error, CancellationToken ct);
}

/// <summary>
/// An order or ASN references a SKU the WMS does not know yet. Usually master data simply has not arrived
/// yet (ordering between channels is not guaranteed), so this is treated as transient and retried.
/// </summary>
public sealed class UnknownSkuException(string sku) : Exception($"Unknown SKU '{sku}'. Product master data may not have been synced yet.");

/// <summary>The lease on an inbox row expired and another worker took it over; this worker's transaction is rolled back.</summary>
public sealed class LeaseLostException(long inboxId) : Exception($"Lease on inbox message {inboxId} was lost.");
