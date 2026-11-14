using System.Data;
using Dapper;
using IntegrationHub.Core.Canonical;

namespace IntegrationHub.Data;

public sealed record UpsertResult(int Inserted, int Updated, int Unchanged);

public sealed class ProductRepository(SqlConnectionFactory db)
{
    /// <summary>Bulk master-data sync: one round trip, one stored procedure call, a table-valued parameter.</summary>
    public async Task<UpsertResult> UpsertAsync(IReadOnlyCollection<ProductRecord> products, CancellationToken ct)
    {
        using var table = new DataTable();
        table.Columns.Add("sku", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("uom", typeof(string));
        table.Columns.Add("weight_kg", typeof(decimal));
        table.Columns.Add("barcode", typeof(string));
        foreach (var p in products)
        {
            table.Rows.Add(p.Sku, p.Name, p.Uom, (object?)p.WeightKg ?? DBNull.Value, (object?)p.Barcode ?? DBNull.Value);
        }

        await using var connection = await db.OpenAsync(ct);
        return await connection.QuerySingleAsync<UpsertResult>(new CommandDefinition(
            "dbo.usp_UpsertProducts",
            new { Products = table.AsTableValuedParameter("dbo.ProductTableType") },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }
}
