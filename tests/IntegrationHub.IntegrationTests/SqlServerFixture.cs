using Dapper;
using DbUp;
using IntegrationHub.Data;
using IntegrationHub.IntegrationTests;
using Microsoft.Data.SqlClient;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

// Tests share one database and claim "any due row", so they must not run concurrently with each other.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace IntegrationHub.IntegrationTests;

/// <summary>Creates a uniquely named database for this test run, migrates it with the real DbUp scripts, drops it afterwards.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public SqlServerFixture()
    {
        var server = Environment.GetEnvironmentVariable("WMS_TEST_SQL")
                     ?? @"Server=localhost\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True";
        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = $"WmsHubTest_{Guid.NewGuid():N}" }.ConnectionString;
        Db = new SqlConnectionFactory(ConnectionString);
    }

    public string ConnectionString { get; }

    public SqlConnectionFactory Db { get; }

    public ValueTask InitializeAsync()
    {
        DatabaseMigrator.Migrate(ConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        DropDatabase.For.SqlDatabase(ConnectionString);
        return ValueTask.CompletedTask;
    }

    /// <summary>Empties every table (children first) so each test starts from a clean state.</summary>
    public Task ResetAsync() => ExecuteAsync(
        """
        DELETE dbo.order_lines; DELETE dbo.orders; DELETE dbo.receipts; DELETE dbo.stock_movements;
        DELETE dbo.idempotency_keys; DELETE dbo.outbox; DELETE dbo.inbox; DELETE dbo.products;
        """);

    public async Task ExecuteAsync(string sql, object? param = null)
    {
        await using var connection = await Db.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var connection = await Db.OpenAsync(CancellationToken.None);
        return (await connection.ExecuteScalarAsync<T>(sql, param))!;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var connection = await Db.OpenAsync(CancellationToken.None);
        return (await connection.QueryAsync<T>(sql, param)).AsList();
    }
}
