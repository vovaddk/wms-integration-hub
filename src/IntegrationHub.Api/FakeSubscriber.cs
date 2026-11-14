using System.Collections.Concurrent;
using System.Text.Json;

namespace IntegrationHub.Api;

public sealed record ReceivedEvent(string Subscriber, string EventId, string EventType, string CorrelationId, DateTimeOffset ReceivedAt, JsonElement Body);

/// <summary>In-memory log of webhook deliveries, so the outbound flow can be demoed without a real subscriber.</summary>
public sealed class FakeSubscriberLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<ReceivedEvent> _events = new();

    public void Add(ReceivedEvent received)
    {
        _events.Enqueue(received);
        while (_events.Count > Capacity && _events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<ReceivedEvent> For(string subscriber) =>
        _events.Where(e => string.Equals(e.Subscriber, subscriber, StringComparison.OrdinalIgnoreCase)).ToList();
}

public static class FakeSubscriber
{
    public static void MapFakeSubscriber(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fake-subscriber/{name}", (string name, JsonElement body, HttpContext http, FakeSubscriberLog log, ILogger<FakeSubscriberLog> logger) =>
        {
            var received = new ReceivedEvent(
                name,
                http.Request.Headers["X-Event-Id"].ToString(),
                http.Request.Headers["X-Event-Type"].ToString(),
                CorrelationId.Get(http),
                DateTimeOffset.UtcNow,
                body);
            log.Add(received);
            logger.LogInformation("Fake subscriber {Subscriber} received {EventType} {EventId}", name, received.EventType, received.EventId);
            return Results.Ok();
        });

        app.MapGet("/fake-subscriber/{name}", (string name, FakeSubscriberLog log) => log.For(name));
    }
}
