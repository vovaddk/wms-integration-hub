using IntegrationHub.Data;

namespace IntegrationHub.Api;

// shortcut: admin endpoints have no authentication; put them behind an auth policy / internal network before real use.
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin");

        admin.MapGet("/dead-letters", (InboxRepository inbox, int? take, CancellationToken ct) =>
            inbox.ListDeadLettersAsync(Math.Clamp(take ?? 50, 1, 500), ct));

        admin.MapPost("/dead-letters/{id:long}/replay", async (long id, InboxRepository inbox, ILogger<InboxRepository> logger, CancellationToken ct) =>
        {
            if (!await inbox.ReplayAsync(id, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Inbox message {id} is not dead-lettered.");
            }

            logger.LogInformation("Dead letter {InboxId} replayed by admin", id);
            return Results.Accepted();
        });

        admin.MapGet("/stats", async (DateOnly? from, DateOnly? to, ReportRepository reports, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var (start, end) = (from ?? today.AddDays(-7), to ?? today);
            return start > end
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "'from' must not be after 'to'.")
                : Results.Ok(await reports.GetIntegrationStatsAsync(start, end, ct));
        });

        admin.MapGet("/reports/stock-turnover", async (DateOnly from, DateOnly to, string? sku, ReportRepository reports, CancellationToken ct) =>
            from > to
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "'from' must not be after 'to'.")
                : Results.Ok(await reports.GetStockTurnoverAsync(from, to, sku, ct)));
    }
}
