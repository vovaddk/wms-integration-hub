using DbUp;

namespace IntegrationHub.Data;

/// <summary>Applies the numbered scripts embedded under Scripts/ (each exactly once, journaled by DbUp).</summary>
public static class DatabaseMigrator
{
    public static void Migrate(string connectionString)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var result = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.Contains(".Scripts.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException($"Database migration failed on script '{result.ErrorScript?.Name}'.", result.Error);
        }
    }
}
