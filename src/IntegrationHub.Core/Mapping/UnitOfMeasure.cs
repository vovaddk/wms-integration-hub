namespace IntegrationHub.Core.Mapping;

/// <summary>Normalizes the units different systems use ("EA", "pcs", "DZ") to the WMS base unit: pieces.</summary>
public static class UnitOfMeasure
{
    public const string Base = "EA";

    private static readonly Dictionary<string, int> PiecesPerUnit = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EA"] = 1,
        ["PCS"] = 1,
        ["PC"] = 1,
        ["DZ"] = 12,
    };

    public static bool IsKnown(string? unit) => unit is not null && PiecesPerUnit.ContainsKey(unit.Trim());

    public static bool TryToBaseQuantity(decimal quantity, string? unit, out int pieces, out string error)
    {
        pieces = 0;
        if (string.IsNullOrWhiteSpace(unit) || !PiecesPerUnit.TryGetValue(unit.Trim(), out var factor))
        {
            error = $"Unknown unit of measure '{unit}'.";
            return false;
        }

        var total = quantity * factor;
        if (total <= 0 || total > int.MaxValue)
        {
            error = $"Quantity {quantity} {unit} is out of range.";
            return false;
        }

        if (total != decimal.Truncate(total))
        {
            error = $"Quantity {quantity} {unit} is not a whole number of pieces.";
            return false;
        }

        pieces = (int)total;
        error = string.Empty;
        return true;
    }

    public static int ToBaseQuantity(decimal quantity, string? unit) =>
        TryToBaseQuantity(quantity, unit, out var pieces, out var error) ? pieces : throw new MappingException(error);
}
