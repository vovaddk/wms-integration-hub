using FluentValidation;
using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Mapping;

namespace IntegrationHub.Worker.FileDrop;

/// <summary>products*.csv: sku,name,uom,weight_kg,barcode (weight_kg and barcode optional).</summary>
public static class ProductCsvParser
{
    private static readonly string[] RequiredColumns = ["sku", "name", "uom"];
    private static readonly string[] OptionalColumns = ["weight_kg", "barcode"];
    private static readonly ProductRowValidator Validator = new();

    public static (IReadOnlyList<ProductRecord> Products, ImportReport Report) Parse(TextReader reader)
    {
        var report = new ImportReport();
        var products = new List<ProductRecord>();
        var firstLineBySku = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (line, csvRow) in CsvRows.Read(reader, RequiredColumns, OptionalColumns, report))
        {
            var row = new ProductCsvRow
            {
                Sku = csvRow["sku"],
                Name = csvRow["name"],
                Uom = csvRow["uom"],
                WeightKg = csvRow["weight_kg"],
                Barcode = csvRow["barcode"],
            };

            var result = Validator.Validate(row);
            if (!result.IsValid)
            {
                report.AddErrors(line, result);
                continue;
            }

            var sku = row.Sku!;
            if (firstLineBySku.TryGetValue(sku, out var firstLine))
            {
                report.Errors.Add(new RowError(line, $"Duplicate sku '{sku}' (first seen on line {firstLine})."));
                continue;
            }

            firstLineBySku[sku] = line;
            CsvRows.TryParseDecimal(row.WeightKg, out var weight);
            products.Add(new ProductRecord(
                sku,
                row.Name!,
                row.Uom!.ToUpperInvariant(),
                string.IsNullOrEmpty(row.WeightKg) ? null : weight,
                string.IsNullOrEmpty(row.Barcode) ? null : row.Barcode));
        }

        return (products, report);
    }

    public sealed class ProductCsvRow
    {
        public string? Sku { get; set; }

        public string? Name { get; set; }

        public string? Uom { get; set; }

        public string? WeightKg { get; set; }

        public string? Barcode { get; set; }
    }

    private sealed class ProductRowValidator : AbstractValidator<ProductCsvRow>
    {
        public ProductRowValidator()
        {
            RuleFor(r => r.Sku).NotEmpty().MaximumLength(64).WithName("sku");
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200).WithName("name");
            RuleFor(r => r.Uom).Must(UnitOfMeasure.IsKnown).WithName("uom").WithMessage("Unknown unit of measure '{PropertyValue}'.");
            RuleFor(r => r.WeightKg)
                .Must(w => CsvRows.TryParseDecimal(w, out var kg) && kg is >= 0 and < 1_000_000)
                .When(r => !string.IsNullOrEmpty(r.WeightKg))
                .WithMessage("weight_kg must be a non-negative number, got '{PropertyValue}'.");
            RuleFor(r => r.Barcode).MaximumLength(32).WithName("barcode");
        }
    }
}
