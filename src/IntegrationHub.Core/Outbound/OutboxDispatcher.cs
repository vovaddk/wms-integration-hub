using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Processing;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Core.Outbound;

public sealed class OutboxOptions
{
    public List<Subscriber> Subscribers { get; init; } = [];
}

public sealed class Subscriber
{
    public string Name { get; init; } = string.Empty;

    public Uri? Url { get; init; }

    public List<string> Events { get; init; } = [];
}

/// <summary>
/// Publishes outbox events to subscriber webhooks. At-least-once: an event is marked dispatched only after
/// every matching subscriber returned 2xx, so a crash or a failing subscriber causes re-delivery.
/// Subscribers must de-duplicate by the X-Event-Id header.
/// </summary>
public sealed class OutboxDispatcher(
    IOutboxStore outbox,
    HttpClient http,
    OutboxOptions options,
    RetryPolicy retryPolicy,
    ILogger<OutboxDispatcher> logger)
{
    public async Task<int> DispatchBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        var batch = await outbox.ClaimBatchAsync(batchSize, lease, ct);
        foreach (var message in batch)
        {
            await DispatchAsync(message, ct);
        }

        return batch.Count;
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        using var scope = logger.BeginScope(
            "CorrelationId:{CorrelationId} OutboxId:{OutboxId} EventId:{EventId}", message.CorrelationId, message.Id, message.EventId);

        try
        {
            var envelope = new
            {
                eventId = message.EventId,
                eventType = message.EventType,
                occurredAt = DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc),
                data = JsonSerializer.Deserialize<JsonElement>(message.Payload),
            };

            foreach (var subscriber in options.Subscribers.Where(s => s.Events.Contains(message.EventType)))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, subscriber.Url) { Content = JsonContent.Create(envelope) };
                request.Headers.Add("X-Correlation-Id", message.CorrelationId);
                request.Headers.Add("X-Event-Id", message.EventId.ToString());
                request.Headers.Add("X-Event-Type", message.EventType);

                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                logger.LogInformation("Delivered {EventType} to subscriber {Subscriber}", message.EventType, subscriber.Name);
            }

            await outbox.MarkDispatchedAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (retryPolicy.ShouldDeadLetter(message.AttemptCount))
            {
                logger.LogError(ex, "Delivery attempt {Attempt} failed, dead-lettering {EventType}", message.AttemptCount, message.EventType);
                await outbox.DeadLetterAsync(message, ex.Message, ct);
                return;
            }

            var delay = retryPolicy.NextDelay(message.AttemptCount, Random.Shared.NextDouble());
            logger.LogWarning(ex, "Delivery attempt {Attempt} failed, retrying in {Delay}", message.AttemptCount, delay);
            await outbox.ScheduleRetryAsync(message, delay, ex.Message, ct);
        }
    }
}
