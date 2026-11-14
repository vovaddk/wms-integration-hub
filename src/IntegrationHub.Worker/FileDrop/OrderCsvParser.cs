using System.Globalization;
using FluentValidation;
using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Mapping;

namespace IntegrationHub.Worker.FileDrop;

public sealed record ParsedOrder(CanonicalOrder Order, IReadOnlyList<int> Lines);

/// <summary>
/// orders*.csv: one row per order line - order_no,order_date,customer,sku,quantity,unit.
/// An order is imported all-or-nothing: if any of its lines is invalid, the whole order is skipped.
/// </summary>
public static class OrderCsvParser
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly string[] RequiredColumns = ["order_no", "order_date", "sku", "quantity", "unit"];
    private static readonly string[] OptionalColumns = ["customer"];
    private static readonly OrderRowValidator Validator = new();

    public static (IReadOnlyList<ParsedOrder> Orders, ImportReport Report) Parse(TextReader reader)
    {
        var report = new ImportReport();
        var validRows = new List<(int Line, OrderCsvRow Row)>();
        var rejectedOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (line, csvRow) in CsvRows.Read(reader, RequiredColumns, OptionalColumns, report))
        {
            var row = new OrderCsvRow
            {
                OrderNo = csvRow["order_no"],
                OrderDate = csvRow["order_date"],
                Customer = csvRow["customer"],
                Sku = csvRow["sku"],
                Quantity = csvRow["quantity"],
                Unit = csvRow["unit"],
            };

            var result = Validator.Validate(row);
            if (result.IsValid)
            {
                validRows.Add((line, row));
                continue;
            }

            report.AddErrors(line, result);
            if (!string.IsNullOrWhiteSpace(row.OrderNo))
            {
                rejectedOrders.Add(row.OrderNo);
            }
        }

        var orders = new List<ParsedOrder>();
        foreach (var group in validRows.GroupBy(r => r.Row.OrderNo!, StringComparer.OrdinalIgnoreCase))
        {
            if (rejectedOrders.Contains(group.Key))
            {
                report.Errors.AddRange(group.Select(r => new RowError(r.Line, $"Order '{group.Key}' skipped: another line of this order is invalid.")));
                continue;
            }

            var header = group.First().Row;
            var lines = group
                .Select(r =>
                {
                    CsvRows.TryParseDecimal(r.Row.Quantity, out var quantity);
                    return new CanonicalLine(r.Row.Sku!, UnitOfMeasure.ToBaseQuantity(quantity, r.Row.Unit));
                })
                .ToList();

            var orderDate = DateOnly.ParseExact(header.OrderDate!, DateFormat, CultureInfo.InvariantCulture);
            orders.Add(new ParsedOrder(
                new CanonicalOrder(group.Key, new DateTimeOffset(orderDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), NullIfEmpty(header.Customer), lines),
                group.Select(r => r.Line).ToList()));
        }

        report.Errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        return (orders, report);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public sealed class OrderCsvRow
    {
        public string? OrderNo { get; set; }

        public string? OrderDate { get; set; }

        public string? Customer { get; set; }

        public string? Sku { get; set; }

        public string? Quantity { get; set; }

        public string? Unit { get; set; }
    }

    private sealed class OrderRowValidator : AbstractValidator<OrderCsvRow>
    {
        public OrderRowValidator()
        {
            RuleFor(r => r.OrderNo).NotEmpty().MaximumLength(64).WithName("order_no");
            RuleFor(r => r.OrderDate)
                .Must(d => DateOnly.TryParseExact(d, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .WithMessage($"order_date must be a date in format {DateFormat}, got '{{PropertyValue}}'.");
            RuleFor(r => r.Sku).NotEmpty().MaximumLength(64).WithName("sku");
            RuleFor(r => r).Custom((row, context) =>
            {
                if (!CsvRows.TryParseDecimal(row.Quantity, out var quantity))
                {
                    context.AddFailure("quantity", $"quantity must be a number, got '{row.Quantity}'.");
                }
                else if (!UnitOfMeasure.TryToBaseQuantity(quantity, row.Unit, out _, out var error))
                {
                    context.AddFailure("quantity", error);
                }
            });
        }
    }
}
