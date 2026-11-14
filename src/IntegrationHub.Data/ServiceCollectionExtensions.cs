using IntegrationHub.Core.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationHub.Data;

public static class ServiceCollectionExtensions
{
    public const string ConnectionStringName = "WmsIntegrationHub";

    public static IServiceCollection AddIntegrationHubData(this IServiceCollection services)
    {
        // Resolved lazily so test hosts can override configuration after Program.cs has run.
        services.AddSingleton(sp => new SqlConnectionFactory(
            sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.")));

        services.AddSingleton<InboxRepository>();
        services.AddSingleton<IInboxStore>(sp => sp.GetRequiredService<InboxRepository>());
        services.AddSingleton<OutboxRepository>();
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<OutboxRepository>());
        services.AddSingleton<WmsRepository>();
        services.AddSingleton<IWmsWriter>(sp => sp.GetRequiredService<WmsRepository>());
        services.AddSingleton<ProductRepository>();
        services.AddSingleton<ReportRepository>();
        return services;
    }
}
