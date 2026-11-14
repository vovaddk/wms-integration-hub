using System.Text;
using System.Text.Json;
using IntegrationHub.Core.Idempotency;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Security;
using IntegrationHub.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Api;

public sealed class InboundOptions
{
    public const string Section = "Inbound";

    /// <summary>Known source systems (lower-case keys). An empty HmacSecret disables signature checks for that source.</summary>
    public Dictionary<string, SourceOptions> Sources { get; init; } = [];
}

public sealed class SourceOptions
{
    public string? HmacSecret { get; init; }
}

public sealed record IntakeResponse(long InboxId);

public static class InboundEndpoints
{
    public const string SourceHeader = "X-Source-System";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string SignatureHeader = "X-Signature";
    private const int MaxBodyBytes = 1024 * 1024;

    public static void MapInboundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inbound").WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        group.MapPost("/orders", (HttpContext http, InboxRepository inbox, IOptions<InboundOptions> options, ILogger<InboxRepository> logger, CancellationToken ct) =>
            ReceiveAsync(MessageTypes.Order, http, inbox, options.Value, logger, ct));
        group.MapPost("/asns", (HttpContext http, InboxRepository inbox, IOptions<InboundOptions> options, ILogger<InboxRepository> logger, CancellationToken ct) =>
            ReceiveAsync(MessageTypes.Asn, http, inbox, options.Value, logger, ct));
    }

    private static async Task<IResult> ReceiveAsync(
        string messageType, HttpContext http, InboxRepository inbox, InboundOptions options, ILogger logger, CancellationToken ct)
    {
        var request = http.Request;
        var source = request.Headers[SourceHeader].ToString().Trim().ToLowerInvariant();
        if (!options.Sources.TryGetValue(source, out var sourceOptions))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown or missing {SourceHeader} header.");
        }

        var idempotencyKey = request.Headers[IdempotencyHeader].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 100)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"{IdempotencyHeader} header is required (max 100 characters).");
        }

        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }

        // Authenticate before looking at the content.
        if (!string.IsNullOrEmpty(sourceOptions.HmacSecret) &&
            !HmacSignature.IsValid(body, request.Headers[SignatureHeader].ToString(), sourceOptions.HmacSecret))
        {
            logger.LogWarning("Rejected {MessageType} from {SourceSystem}: invalid signature", messageType, source);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: $"Missing or invalid {SignatureHeader}.");
        }

        if (!IsJson(body))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON document.");
        }

        // Stored raw: the inbox is the source of truth, so a mapper fix + replay can reprocess the original payload.
        var result = await inbox.ReceiveAsync(
            new InboundMessage(source, messageType, Encoding.UTF8.GetString(body), IdempotencyDecision.HashPayload(body), idempotencyKey, CorrelationId.Get(http)),
            ct);

        logger.LogInformation("Intake {Outcome}: {MessageType} from {SourceSystem}, key {IdempotencyKey}, inbox {InboxId}",
            result.Outcome, messageType, source, idempotencyKey, result.InboxId);

        switch (result.Outcome)
        {
            case IntakeOutcome.Accepted:
                return Results.Json(new IntakeResponse(result.InboxId), statusCode: StatusCodes.Status202Accepted);
            case IntakeOutcome.Replayed:
                http.Response.Headers["Idempotent-Replayed"] = "true";
                return Results.Ok(new IntakeResponse(result.InboxId));
            default:
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: $"{IdempotencyHeader} '{idempotencyKey}' was already used with a different payload.");
        }
    }

    private static bool IsJson(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
