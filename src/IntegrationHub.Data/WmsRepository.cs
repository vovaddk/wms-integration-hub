using System.Text.Json;
using Dapper;
using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Processing;
using Microsoft.Data.SqlClient;

namespace IntegrationHub.Data;

/// <summary>
/// Applies canonical messages to the WMS tables. Transactional outbox: the domain change, its outbox events
/// and the inbox status change commit (or roll back) together in one local SQL transaction - no 2PC needed.
/// </summary>
public sealed class WmsRepository(SqlConnectionFactory db) : IWmsWriter
{
    public async Task<bool> AcceptOrderAsync(InboxMessage message, CanonicalOrder order, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        // Business-level dedupe: the same order re-sent under a new idempotency key is still the same order.
        var orderId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            INSERT dbo.orders (source_system, external_order_id, ordered_at, customer_name, inbox_id, correlation_id)
            OUTPUT inserted.id
            SELECT @SourceSystem, @ExternalOrderId, @OrderedAt, @CustomerName, @InboxId, @CorrelationId
            WHERE NOT EXISTS (SELECT 1 FROM dbo.orders WITH (UPDLOCK, HOLDLOCK)
                              WHERE source_system = @SourceSystem AND external_order_id = @ExternalOrderId);
            """,
            new
            {
                message.SourceSystem,
                order.ExternalOrderId,
                order.OrderedAt,
                order.CustomerName,
                InboxId = message.Id,
                message.CorrelationId,
            },
            tx, cancellationToken: ct));

        if (orderId is null)
        {
            await InboxRepository.MarkProcessedAsync(connection, tx, message, ct);
            await tx.CommitAsync(ct);
            return false;
        }

        var reference = $"order:{message.SourceSystem}:{order.ExternalOrderId}";
        var events = new List<(string Type, object Payload)>();
        foreach (var (sku, quantity) in AggregateBySku(order.Lines))
        {
            var onHand = await ApplyStockChangeAsync(connection, tx, sku, -quantity, "ALLOCATION", reference, ct);
            events.Add((EventTypes.StockLevelChanged, new { sku, delta = -quantity, onHand, reason = "ALLOCATION", reference }));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.order_lines (order_id, line_no, sku, quantity) VALUES (@OrderId, @LineNo, @Sku, @Quantity);",
            order.Lines.Select((l, i) => new { OrderId = orderId, LineNo = i + 1, l.Sku, l.Quantity }),
            tx, cancellationToken: ct));

        events.Insert(0, (EventTypes.OrderAccepted, new
        {
            orderId,
            sourceSystem = message.SourceSystem,
            externalOrderId = order.ExternalOrderId,
            orderedAt = order.OrderedAt,
            lines = order.Lines,
        }));

        await WriteOutboxAsync(connection, tx, message.CorrelationId, events, ct);
        await InboxRepository.MarkProcessedAsync(connection, tx, message, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Simplification: an ASN is treated as physically received when it is processed.</summary>
    public async Task<bool> ReceiveAsnAsync(InboxMessage message, CanonicalAsn asn, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var receiptId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            INSERT dbo.receipts (source_system, asn_number, expected_on, inbox_id, correlation_id)
            OUTPUT inserted.id
            SELECT @SourceSystem, @AsnNumber, @ExpectedOn, @InboxId, @CorrelationId
            WHERE NOT EXISTS (SELECT 1 FROM dbo.receipts WITH (UPDLOCK, HOLDLOCK)
                              WHERE source_system = @SourceSystem AND asn_number = @AsnNumber);
            """,
            new
            {
                message.SourceSystem,
                asn.AsnNumber,
                ExpectedOn = asn.ExpectedOn.ToDateTime(TimeOnly.MinValue),
                InboxId = message.Id,
                message.CorrelationId,
            },
            tx, cancellationToken: ct));

        var applied = receiptId is not null;
        if (applied)
        {
            var reference = $"asn:{message.SourceSystem}:{asn.AsnNumber}";
            var events = new List<(string Type, object Payload)>();
            foreach (var (sku, quantity) in AggregateBySku(asn.Lines))
            {
                var onHand = await ApplyStockChangeAsync(connection, tx, sku, quantity, "RECEIPT", reference, ct);
                events.Add((EventTypes.StockLevelChanged, new { sku, delta = quantity, onHand, reason = "RECEIPT", reference }));
            }

            await WriteOutboxAsync(connection, tx, message.CorrelationId, events, ct);
        }

        await InboxRepository.MarkProcessedAsync(connection, tx, message, ct);
        await tx.CommitAsync(ct);
        return applied;
    }

    /// <summary>Sorted by SKU so concurrent transactions lock product rows in the same order (no deadlocks).</summary>
    private static IEnumerable<(string Sku, int Quantity)> AggregateBySku(IEnumerable<CanonicalLine> lines) =>
        lines.GroupBy(l => l.Sku, StringComparer.OrdinalIgnoreCase)
             .Select(g => (g.Key, g.Sum(l => l.Quantity)))
             .OrderBy(x => x.Key, StringComparer.Ordinal);

    private static async Task<int> ApplyStockChangeAsync(
        SqlConnection connection, SqlTransaction tx, string sku, int delta, string movementType, string reference, CancellationToken ct)
    {
        var onHand = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            """
            UPDATE dbo.products
            SET on_hand = on_hand + @Delta, updated_at = SYSUTCDATETIME()
            OUTPUT inserted.on_hand
            WHERE sku = @Sku;
            """,
            new { Sku = sku, Delta = delta }, tx, cancellationToken: ct));

        if (onHand is null)
        {
            throw new UnknownSkuException(sku);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.stock_movements (sku, quantity, movement_type, reference) VALUES (@Sku, @Delta, @MovementType, @Reference);",
            new { Sku = sku, Delta = delta, MovementType = movementType, Reference = reference }, tx, cancellationToken: ct));

        return onHand.Value;
    }

    private static Task WriteOutboxAsync(
        SqlConnection connection, SqlTransaction tx, string correlationId, IEnumerable<(string Type, object Payload)> events, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.outbox (event_type, payload, correlation_id) VALUES (@EventType, @Payload, @CorrelationId);",
            events.Select(e => new
            {
                EventType = e.Type,
                Payload = JsonSerializer.Serialize(e.Payload, JsonSerializerOptions.Web),
                CorrelationId = correlationId,
            }),
            tx, cancellationToken: ct));
}
