using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Api;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class ExampleBuilderTests
{
    private static string Build(string schema, string schemas = "{}")
    {
        using JsonDocument s = JsonDocument.Parse(schema);
        using JsonDocument c = JsonDocument.Parse(schemas);

        return ExampleBuilder.Build(s.RootElement, c.RootElement)?.ToJsonString() ?? "null";
    }

    [Theory]
    [InlineData("""{"type":"string"}""", "\"string\"")]
    [InlineData("""{"type":"string","format":"uuid"}""", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("""{"type":"string","format":"date-time"}""", "\"2026-01-01T00:00:00Z\"")]
    [InlineData("""{"type":["null","string"]}""", "\"string\"")]
    [InlineData("""{"type":["number","string"],"format":"double"}""", "0")]
    [InlineData("""{"type":["null","integer","string"]}""", "0")]
    [InlineData("""{"type":"boolean"}""", "false")]
    [InlineData("""{"type":"string","enum":["customer_request","out_of_stock"]}""", "\"customer_request\"")]
    [InlineData("""{"type":"integer","default":20}""", "20")]
    [InlineData("""{}""", "null")]
    public void Scalars_take_a_placeholder_of_their_type(string schema, string expected)
    {
        Build(schema).ShouldBe(expected);
    }

    [Fact]
    public void Objects_and_arrays_follow_references_in_property_order()
    {
        const string Schemas = """
        {
          "Order": { "type": "object", "properties": { "id": { "type": "string", "format": "uuid" }, "items": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } } } },
          "Item": { "type": "object", "properties": { "quantity": { "type": ["integer","string"] } } }
        }
        """;

        Build("""{"$ref":"#/components/schemas/Order"}""", Schemas)
            .ShouldBe("""{"id":"00000000-0000-0000-0000-000000000000","items":[{"quantity":0}]}""");
    }

    [Fact]
    public void A_reference_cycle_stops_at_null_instead_of_recursing()
    {
        const string Schemas = """{ "Node": { "type": "object", "properties": { "next": { "$ref": "#/components/schemas/Node" } } } }""";

        Build("""{"$ref":"#/components/schemas/Node"}""", Schemas).ShouldBe("""{"next":null}""");
    }

    [Fact]
    public void OneOf_with_a_null_branch_takes_the_other_branch()
    {
        Build("""{"oneOf":[{"type":"null"},{"type":"object","properties":{"a":{"type":"boolean"}}}]}""").ShouldBe("""{"a":false}""");
    }

    [Fact]
    public void AllOf_merges_the_properties_of_every_part()
    {
        Build("""{"allOf":[{"type":"object","properties":{"a":{"type":"boolean"}}},{"type":"object","properties":{"b":{"type":"string"}}}]}""")
            .ShouldBe("""{"a":false,"b":"string"}""");
    }

    [Fact]
    public void An_unresolvable_reference_is_null()
    {
        Build("""{"$ref":"#/components/schemas/Missing"}""").ShouldBe("null");

        using JsonDocument schema = JsonDocument.Parse("""{"$ref":"#/components/schemas/X"}""");
        JsonNode? withoutComponents = ExampleBuilder.Build(schema.RootElement, default);
        withoutComponents.ShouldBeNull();
    }
}
