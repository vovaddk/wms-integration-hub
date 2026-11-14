using System.Text;
using System.Text.Json;
using IntegrationHub.Core.Idempotency;
using IntegrationHub.Core.Mapping;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Data;

namespace IntegrationHub.Worker.FileDrop;

public sealed class FileDropOptions
{
    public string Path { get; init; } = "filedrop";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Imports one dropped CSV file and moves it to processed/ (at least one row imported) or failed/,
/// with a &lt;file&gt;.report.json listing every rejected row by line number.
/// </summary>
public sealed class CsvFileImporter(
    ProductRepository products, InboxRepository inbox, FileDropOptions options, ILogger<CsvFileImporter> logger)
{
    // Relaxed escaping: the report is a file for humans, never embedded in HTML.
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerOptions.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task ImportAsync(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        var correlationId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope("CorrelationId:{CorrelationId} File:{File}", correlationId, fileName);

        ImportReport report;
        try
        {
            // Exclusive open: if the sender is still writing the file, skip it until the next scan.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            report = await ImportAsync(fileName, reader, correlationId, ct);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "File {File} is not readable yet, retrying on next scan", fileName);
            return;
        }

        // Any other exception (e.g. database down) propagates and the file stays where it is: re-importing it
        // later is safe because every order goes through the idempotency keys and products are upserted.
        var targetDir = Path.Combine(options.Path, report.ImportedRows > 0 ? "processed" : "failed");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{fileName}");
        File.Move(path, target);
        await File.WriteAllTextAsync(target + ".report.json", JsonSerializer.Serialize(report, ReportJson), ct);

        logger.LogInformation(
            "Imported {File}: {Imported}/{Total} rows, {Errors} row errors, file error: {FileError}. Moved to {Target}",
            fileName, report.ImportedRows, report.TotalRows, report.Errors.Count, report.FileError ?? "none", target);
    }

    private async Task<ImportReport> ImportAsync(string fileName, TextReader reader, string correlationId, CancellationToken ct)
    {
        if (fileName.StartsWith("products", StringComparison.OrdinalIgnoreCase))
        {
            var (items, report) = ProductCsvParser.Parse(reader);
            if (items.Count > 0)
            {
                var result = await products.UpsertAsync(items, ct);
                report.ImportedRows = items.Count;
                report.Summary = $"inserted {result.Inserted}, updated {result.Updated}, unchanged {result.Unchanged}";
            }

            return report;
        }

        if (fileName.StartsWith("orders", StringComparison.OrdinalIgnoreCase))
        {
            var (orders, report) = OrderCsvParser.Parse(reader);
            foreach (var parsed in orders)
            {
                var payload = JsonSerializer.Serialize(parsed.Order, JsonSerializerOptions.Web);
                var result = await inbox.ReceiveAsync(
                    new InboundMessage(
                        FileDropMessageMapper.Source,
                        MessageTypes.Order,
                        payload,
                        IdempotencyDecision.HashPayload(Encoding.UTF8.GetBytes(payload)),
                        $"order:{parsed.Order.ExternalOrderId}",
                        correlationId),
                    ct);

                if (result.Outcome == IntakeOutcome.Conflict)
                {
                    report.Errors.AddRange(parsed.Lines.Select(line =>
                        new RowError(line, $"Order '{parsed.Order.ExternalOrderId}' was already imported with different content.")));
                    continue;
                }

                report.ImportedRows += parsed.Lines.Count;
            }

            report.Summary = $"{orders.Count} order(s) staged in the inbox";
            return report;
        }

        return new ImportReport { FileError = "Unknown file type: the name must start with 'products' or 'orders'." };
    }
}
