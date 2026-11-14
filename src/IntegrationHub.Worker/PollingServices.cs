using IntegrationHub.Core.Outbound;
using IntegrationHub.Core.Processing;
using IntegrationHub.Worker.FileDrop;

namespace IntegrationHub.Worker;

public sealed class WorkerOptions
{
    public int BatchSize { get; init; } = 20;

    /// <summary>How long a claimed row belongs to this worker before another worker may take it over.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Runs <see cref="RunOnceAsync"/> in a loop; sleeps only when there was nothing to do.</summary>
public abstract class PollingService(IServiceScopeFactory scopes, ILogger logger, TimeSpan idleDelay) : BackgroundService
{
    /// <returns>True if work was done (poll again immediately), false if idle.</returns>
    protected abstract Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                // A scope per iteration: typed HttpClients get fresh handlers from the factory (DNS changes are picked up).
                await using var scope = scopes.CreateAsyncScope();
                didWork = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. the database is down. Keep the loop alive; the next iteration retries.
                logger.LogError(ex, "{Service} iteration failed", GetType().Name);
            }

            if (!didWork)
            {
                try
                {
                    await Task.Delay(idleDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

public sealed class InboxProcessingService(IServiceScopeFactory scopes, WorkerOptions options, ILogger<InboxProcessingService> logger)
    : PollingService(scopes, logger, options.IdleDelay)
{
    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        await services.GetRequiredService<InboxProcessor>().ProcessBatchAsync(options.BatchSize, options.Lease, ct) > 0;
}

public sealed class OutboxDispatchService(IServiceScopeFactory scopes, WorkerOptions options, ILogger<OutboxDispatchService> logger)
    : PollingService(scopes, logger, options.IdleDelay)
{
    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken ct) =>
        await services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(options.BatchSize, options.Lease, ct) > 0;
}

public sealed class FileDropService(IServiceScopeFactory scopes, FileDropOptions options, ILogger<FileDropService> logger)
    : PollingService(scopes, logger, options.PollInterval)
{
    protected override async Task<bool> RunOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        // Polling instead of FileSystemWatcher: watcher events are lost on buffer overflow and on network shares.
        Directory.CreateDirectory(options.Path);
        var files = Directory.GetFiles(options.Path, "*.csv");
        var importer = services.GetRequiredService<CsvFileImporter>();
        foreach (var file in files)
        {
            await importer.ImportAsync(file, ct);
        }

        return false; // always wait between scans
    }
}
