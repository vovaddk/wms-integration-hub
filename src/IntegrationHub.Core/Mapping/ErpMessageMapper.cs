using System.Globalization;
using System.Text.Json;
using IntegrationHub.Core.Canonical;

namespace IntegrationHub.Core.Mapping;

/// <summary>
/// ERP format: PascalCase fields, European dates ("dd.MM.yyyy", no time zone, taken as UTC),
/// units "EA"/"DZ". Example: {"DocNo":"SO-1001","DocDate":"14.11.2026","Lines":[{"ItemNo":"SKU-1","Qty":2,"Uom":"DZ"}]}.
/// </summary>
public sealed class ErpMessageMapper : IMessageMapper
{
    private const string DateFormat = "dd.MM.yyyy";
    private static readonly JsonSerializerOptions Json = new();

    public string SourceSystem => "erp";

    public CanonicalOrder MapOrder(string payload)
    {
        var doc = MappingJson.Deserialize<ErpOrder>(payload, Json);
        return new CanonicalOrder(
            MappingJson.Required(doc.DocNo, "DocNo"),
            new DateTimeOffset(ParseDate(doc.DocDate, "DocDate").ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            doc.Customer?.Name?.Trim(),
            MapLines(doc.Lines));
    }

    public CanonicalAsn MapAsn(string payload)
    {
        var doc = MappingJson.Deserialize<ErpAsn>(payload, Json);
        return new CanonicalAsn(
            MappingJson.Required(doc.AsnNo, "AsnNo"),
            ParseDate(doc.ExpectedDate, "ExpectedDate"),
            MapLines(doc.Lines));
    }

    private static List<CanonicalLine> MapLines(IReadOnlyList<ErpLine>? lines) =>
        MappingJson.NotEmpty(lines, "Lines")
            .Select(l => new CanonicalLine(MappingJson.Required(l.ItemNo, "ItemNo"), UnitOfMeasure.ToBaseQuantity(l.Qty, l.Uom)))
            .ToList();

    private static DateOnly ParseDate(string? value, string field) =>
        DateOnly.TryParseExact(MappingJson.Required(value, field), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new MappingException($"Field '{field}' must be a date in format {DateFormat}, got '{value}'.");

    private sealed record ErpOrder(string? DocNo, string? DocDate, ErpCustomer? Customer, List<ErpLine>? Lines);

    private sealed record ErpCustomer(string? Name);

    private sealed record ErpAsn(string? AsnNo, string? ExpectedDate, List<ErpLine>? Lines);

    private sealed record ErpLine(string? ItemNo, decimal Qty, string? Uom);
}
