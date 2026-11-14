namespace IntegrationHub.Core.Canonical;

/// <summary>Order in the WMS-internal (canonical) shape. Quantities are always in base unit (EA, pieces).</summary>
public sealed record CanonicalOrder(
    string ExternalOrderId,
    DateTimeOffset OrderedAt,
    string? CustomerName,
    IReadOnlyList<CanonicalLine> Lines);

/// <summary>Advance shipping notice: goods announced by a supplier for receiving.</summary>
public sealed record CanonicalAsn(
    string AsnNumber,
    DateOnly ExpectedOn,
    IReadOnlyList<CanonicalLine> Lines);

public sealed record CanonicalLine(string Sku, int Quantity);

/// <summary>Product master data row (bulk sync via usp_UpsertProducts).</summary>
public sealed record ProductRecord(string Sku, string Name, string Uom, decimal? WeightKg, string? Barcode);
