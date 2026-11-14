using IntegrationHub.Core.Canonical;
using IntegrationHub.Worker.FileDrop;

namespace IntegrationHub.UnitTests;

public class ProductCsvParserTests
{
    [Fact]
    public void Imports_good_rows_and_reports_bad_rows_with_file_line_numbers()
    {
        const string csv = """
            sku,name,uom,weight_kg,barcode
            SKU-1,Pallet wrap,EA,1.250,4006381333931
            ,Missing sku,EA,,
            SKU-3,Bad unit,BOX,,
            SKU-4,Bad weight,pcs,heavy,
            SKU-1,Duplicate,EA,,
            SKU-6,Label printer,pcs,,
            """;

        var (products, report) = ProductCsvParser.Parse(new StringReader(csv));

        products.ShouldBe([
            new ProductRecord("SKU-1", "Pallet wrap", "EA", 1.250m, "4006381333931"),
            new ProductRecord("SKU-6", "Label printer", "PCS", null, null),
        ]);
        report.TotalRows.ShouldBe(6);
        report.FileError.ShouldBeNull();
        report.Errors.Select(e => e.Line).ShouldBe([3, 4, 5, 6]);
        report.Errors[0].Message.ShouldContain("sku");
        report.Errors[1].Message.ShouldContain("BOX");
        report.Errors[2].Message.ShouldContain("weight_kg");
        report.Errors[3].Message.ShouldContain("first seen on line 2");
    }

    [Fact]
    public void Optional_columns_may_be_absent()
    {
        var (products, report) = ProductCsvParser.Parse(new StringReader("sku,name,uom\nA,Thing,EA\n"));

        report.Errors.ShouldBeEmpty();
        products.ShouldHaveSingleItem().ShouldBe(new ProductRecord("A", "Thing", "EA", null, null));
    }

    [Fact]
    public void Missing_required_column_fails_the_whole_file()
    {
        var (products, report) = ProductCsvParser.Parse(new StringReader("sku,title\nA,Thing\n"));

        products.ShouldBeEmpty();
        report.FileError.ShouldNotBeNull().ShouldContain("name");
    }

    [Fact]
    public void Empty_file_is_a_file_error() =>
        ProductCsvParser.Parse(new StringReader(string.Empty)).Report.FileError.ShouldBe("File is empty.");
}

public class OrderCsvParserTests
{
    [Fact]
    public void Groups_lines_into_orders_and_skips_orders_with_any_invalid_line()
    {
        const string csv = """
            order_no,order_date,customer,sku,quantity,unit
            CSV-1,2026-11-14,ACME,SKU-1,2,EA
            CSV-1,2026-11-14,ACME,SKU-2,1,DZ
            CSV-2,2026-11-14,Globex,SKU-1,1,EA
            CSV-2,14.11.2026,Globex,SKU-2,abc,EA
            CSV-3,2026-11-15,,SKU-1,0.5,EA
            """;

        var (orders, report) = OrderCsvParser.Parse(new StringReader(csv));

        var order = orders.ShouldHaveSingleItem();
        order.Order.ExternalOrderId.ShouldBe("CSV-1");
        order.Order.Lines.ShouldBe([new CanonicalLine("SKU-1", 2), new CanonicalLine("SKU-2", 12)]);
        order.Lines.ShouldBe([2, 3]);

        report.TotalRows.ShouldBe(5);
        report.Errors.Select(e => e.Line).ShouldBe([4, 5, 5, 6]);
        report.Errors[0].Message.ShouldContain("another line of this order is invalid");
        report.Errors.ShouldContain(e => e.Line == 5 && e.Message.Contains("order_date"));
        report.Errors.ShouldContain(e => e.Line == 5 && e.Message.Contains("quantity must be a number"));
        report.Errors[3].Message.ShouldContain("whole number");
    }
}
