using System.Globalization;
using System.Text.Json;
using IntegrationHub.Core.Canonical;

namespace IntegrationHub.Core.Mapping;

/// <summary>
/// E-commerce shop format: snake_case fields, ISO-8601 timestamps with offset, unit "pcs".
/// Example: {"order_id":"WEB-555","created_at":"2026-11-14T10:15:00+02:00","items":[{"sku":"SKU-1","quantity":3,"unit":"pcs"}]}.
/// The shop never sends ASNs.
/// </summary>
public sealed class ShopMessageMapper : IMessageMapper
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string SourceSystem => "shop";

    public CanonicalOrder MapOrder(string payload)
    {
        var doc = MappingJson.Deserialize<ShopOrder>(payload, Json);
        var createdAt = MappingJson.Required(doc.CreatedAt, "created_at");
        if (!DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var orderedAt))
        {
            throw new MappingException($"Field 'created_at' must be an ISO-8601 timestamp, got '{createdAt}'.");
        }

        var lines = MappingJson.NotEmpty(doc.Items, "items")
            .Select(i => new CanonicalLine(MappingJson.Required(i.Sku, "sku"), UnitOfMeasure.ToBaseQuantity(i.Quantity, i.Unit ?? "pcs")))
            .ToList();

        return new CanonicalOrder(MappingJson.Required(doc.OrderId, "order_id"), orderedAt, doc.CustomerName?.Trim(), lines);
    }

    public CanonicalAsn MapAsn(string payload) =>
        throw new MappingException("The shop does not send ASNs.");

    private sealed record ShopOrder(string? OrderId, string? CreatedAt, string? CustomerName, List<ShopItem>? Items);

    private sealed record ShopItem(string? Sku, decimal Quantity, string? Unit);
}
