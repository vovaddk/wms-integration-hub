using IntegrationHub.Api;
using IntegrationHub.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIntegrationHubData();
builder.Services.AddOptions<InboundOptions>().BindConfiguration(InboundOptions.Section);
builder.Services.AddSingleton<FakeSubscriberLog>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<SqlHealthCheck>("sql");

var app = builder.Build();

// Only the API migrates; workers start after it (see docker-compose depends_on). DbUp has no cross-process lock.
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    DatabaseMigrator.Migrate(app.Services.GetRequiredService<SqlConnectionFactory>().ConnectionString);
}

app.UseExceptionHandler();
app.UseCorrelationId();

app.MapHealthChecks("/health");
app.MapInboundEndpoints();
app.MapAdminEndpoints();
if (app.Configuration.GetValue("FakeSubscriber:Enabled", false))
{
    app.MapFakeSubscriber();
}

app.Run();
