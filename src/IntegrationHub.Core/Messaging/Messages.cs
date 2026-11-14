namespace IntegrationHub.Core.Messaging;

public static class MessageTypes
{
    public const string Order = "order";
    public const string Asn = "asn";
}

public static class EventTypes
{
    public const string OrderAccepted = "OrderAccepted";
    public const string StockLevelChanged = "StockLevelChanged";
}

/// <summary>A message as it arrives on an inbound channel, before it is staged in the inbox.</summary>
public sealed record InboundMessage(
    string SourceSystem,
    string MessageType,
    string Payload,
    byte[] PayloadHash,
    string IdempotencyKey,
    string CorrelationId);

/// <summary>A claimed inbox row. <see cref="AttemptCount"/> doubles as a fencing token for the lease.</summary>
public sealed record InboxMessage(
    long Id,
    string SourceSystem,
    string MessageType,
    string Payload,
    string CorrelationId,
    int AttemptCount);

/// <summary>A claimed outbox row waiting to be published to subscribers.</summary>
public sealed record OutboxMessage(
    long Id,
    Guid EventId,
    string EventType,
    string Payload,
    string CorrelationId,
    int AttemptCount,
    DateTime CreatedAt);
