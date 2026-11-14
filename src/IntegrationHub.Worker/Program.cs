using IntegrationHub.Core.Mapping;
using IntegrationHub.Core.Outbound;
using IntegrationHub.Core.Processing;
using IntegrationHub.Data;
using IntegrationHub.Worker;
using IntegrationHub.Worker.FileDrop;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

builder.Services.AddIntegrationHubData();
builder.Services.AddSingleton<IMessageMapper, ErpMessageMapper>();
builder.Services.AddSingleton<IMessageMapper, ShopMessageMapper>();
builder.Services.AddSingleton<IMessageMapper, FileDropMessageMapper>();
builder.Services.AddSingleton<MapperRegistry>();

builder.Services.AddSingleton(config.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddSingleton(config.GetSection("Retry").Get<RetryPolicy>() ?? new RetryPolicy());
builder.Services.AddSingleton(config.GetSection("Outbox").Get<OutboxOptions>() ?? new OutboxOptions());
var fileDrop = config.GetSection("FileDrop").Get<FileDropOptions>() ?? new FileDropOptions();
builder.Services.AddSingleton(new FileDropOptions
{
    Path = Path.GetFullPath(fileDrop.Path, builder.Environment.ContentRootPath),
    PollInterval = fileDrop.PollInterval,
});

builder.Services.AddTransient<InboxProcessor>();
builder.Services.AddTransient<CsvFileImporter>();

// Short-term resilience for a single delivery (seconds). Long-term retries (minutes) are the outbox's job.
builder.Services.AddHttpClient<OutboxDispatcher>()
    .AddResilienceHandler("subscribers", pipeline =>
    {
        pipeline.AddTimeout(TimeSpan.FromSeconds(30));
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(500),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(5));
    })
    .SelectPipelineByAuthority(); // one circuit per subscriber host: one dead subscriber must not block the others

builder.Services.AddHostedService<InboxProcessingService>();
builder.Services.AddHostedService<OutboxDispatchService>();
builder.Services.AddHostedService<FileDropService>();

builder.Build().Run();
