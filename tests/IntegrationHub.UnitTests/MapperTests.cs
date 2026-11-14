using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Mapping;

namespace IntegrationHub.UnitTests;

public class ErpMessageMapperTests
{
    private readonly ErpMessageMapper _mapper = new();

    [Fact]
    public void MapOrder_maps_fields_dates_and_units_to_canonical_model()
    {
        const string payload = """
            {"DocNo":"SO-1001","DocDate":"14.11.2026","Customer":{"Name":" ACME GmbH "},
             "Lines":[{"ItemNo":"SKU-1","Qty":5,"Uom":"EA"},{"ItemNo":"SKU-2","Qty":2,"Uom":"DZ"}]}
            """;

        var order = _mapper.MapOrder(payload);

        order.ExternalOrderId.ShouldBe("SO-1001");
        order.OrderedAt.ShouldBe(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        order.CustomerName.ShouldBe("ACME GmbH");
        order.Lines.ShouldBe([new CanonicalLine("SKU-1", 5), new CanonicalLine("SKU-2", 24)]);
    }

    [Fact]
    public void MapAsn_maps_expected_date()
    {
        var asn = _mapper.MapAsn("""{"AsnNo":"ASN-7","ExpectedDate":"01.12.2026","Lines":[{"ItemNo":"SKU-1","Qty":10,"Uom":"EA"}]}""");

        asn.AsnNumber.ShouldBe("ASN-7");
        asn.ExpectedOn.ShouldBe(new DateOnly(2026, 12, 1));
        asn.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(10);
    }

    [Theory]
    [InlineData("""{"DocDate":"14.11.2026","Lines":[{"ItemNo":"A","Qty":1,"Uom":"EA"}]}""", "DocNo")]
    [InlineData("""{"DocNo":"X","DocDate":"2026-11-14","Lines":[{"ItemNo":"A","Qty":1,"Uom":"EA"}]}""", "DocDate")]
    [InlineData("""{"DocNo":"X","DocDate":"14.11.2026","Lines":[]}""", "Lines")]
    [InlineData("""{"DocNo":"X","DocDate":"14.11.2026","Lines":[{"ItemNo":"A","Qty":1,"Uom":"BOX"}]}""", "BOX")]
    [InlineData("""{"DocNo":"X","DocDate":"14.11.2026","Lines":[{"ItemNo":"A","Qty":1.5,"Uom":"EA"}]}""", "whole number")]
    [InlineData("""not json""", "expected format")]
    public void MapOrder_rejects_invalid_payloads_with_a_permanent_error(string payload, string expectedInMessage)
    {
        var ex = Should.Throw<MappingException>(() => _mapper.MapOrder(payload));
        ex.Message.ShouldContain(expectedInMessage);
    }
}

public class ShopMessageMapperTests
{
    private readonly ShopMessageMapper _mapper = new();

    [Fact]
    public void MapOrder_maps_snake_case_fields_and_pcs_unit()
    {
        const string payload = """
            {"order_id":"WEB-555","created_at":"2026-11-14T10:15:00+02:00","customer_name":"Jane Doe",
             "items":[{"sku":"SKU-1","quantity":3,"unit":"pcs"},{"sku":"SKU-2","quantity":1}]}
            """;

        var order = _mapper.MapOrder(payload);

        order.ExternalOrderId.ShouldBe("WEB-555");
        order.OrderedAt.ShouldBe(new DateTimeOffset(2026, 11, 14, 8, 15, 0, TimeSpan.Zero));
        order.CustomerName.ShouldBe("Jane Doe");
        order.Lines.ShouldBe([new CanonicalLine("SKU-1", 3), new CanonicalLine("SKU-2", 1)]);
    }

    [Fact]
    public void MapOrder_rejects_bad_timestamp()
    {
        Should.Throw<MappingException>(() => _mapper.MapOrder("""{"order_id":"1","created_at":"yesterday","items":[{"sku":"A","quantity":1}]}"""))
            .Message.ShouldContain("created_at");
    }

    [Fact]
    public void MapAsn_is_not_supported_for_the_shop() =>
        Should.Throw<MappingException>(() => _mapper.MapAsn("{}"));
}

public class MapperRegistryTests
{
    [Fact]
    public void Get_picks_mapper_by_source_case_insensitively_and_rejects_unknown_sources()
    {
        var registry = new MapperRegistry([new ErpMessageMapper(), new ShopMessageMapper()]);

        registry.Get("ERP").ShouldBeOfType<ErpMessageMapper>();
        registry.Get("shop").ShouldBeOfType<ShopMessageMapper>();
        Should.Throw<MappingException>(() => registry.Get("crm"));
    }

    [Fact]
    public void FileDrop_mapper_round_trips_canonical_json()
    {
        var order = new FileDropMessageMapper().MapOrder("""{"externalOrderId":"F-1","orderedAt":"2026-11-14T00:00:00Z","lines":[{"sku":"A","quantity":2}]}""");

        order.ExternalOrderId.ShouldBe("F-1");
        order.Lines.ShouldHaveSingleItem().ShouldBe(new CanonicalLine("A", 2));
    }
}
