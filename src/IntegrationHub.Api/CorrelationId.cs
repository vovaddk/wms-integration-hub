namespace IntegrationHub.Api;

/// <summary>
/// Takes X-Correlation-Id from the caller (or creates one), echoes it in the response and opens a logging
/// scope so every log line of the request carries it. The id is then stored on the inbox row and travels on
/// to the outbox and the outbound webhook call.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[HeaderName].ToString();
            var correlationId = IsSafe(incoming) ? incoming : Guid.NewGuid().ToString("N");
            context.Items[HeaderName] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(CorrelationId));
            using (logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
            {
                await next(context);
            }
        });

    public static string Get(HttpContext context) => (string)context.Items[HeaderName]!;

    // Caller-supplied values end up in logs and the database: accept only short, plain tokens.
    private static bool IsSafe(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
