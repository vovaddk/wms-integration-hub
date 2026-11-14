using Microsoft.Data.SqlClient;

namespace IntegrationHub.Data;

public sealed class SqlConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal static class SqlErrors
{
    /// <summary>2627 = unique/PK constraint, 2601 = unique index.</summary>
    public static bool IsUniqueViolation(SqlException ex) => ex.Number is 2627 or 2601;

    /// <summary>Error texts can be long; the column is nvarchar(2000).</summary>
    public static string Truncate(string error) => error.Length <= 2000 ? error : error[..2000];
}
