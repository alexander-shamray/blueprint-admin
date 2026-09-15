using System.Text.Json;
using Admin.Host.Broker;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class RabbitCtlJsonTests
{
    private sealed record Row(string Name, string Type);

    [Fact]
    public void A_streamed_array_with_comma_led_rows_parses_to_every_row()
    {
        IReadOnlyList<Row> rows = RabbitCtlJson.Parse<Row>(
        [
            "[",
            """{"name":"amq.topic","type":"topic"}""",
            """,{"name":"","type":"direct"}""",
            "]",
        ]);

        rows.ShouldBe([new Row("amq.topic", "topic"), new Row("", "direct")]);
    }

    [Fact]
    public void An_empty_listing_parses_to_no_rows()
    {
        RabbitCtlJson.Parse<Row>(["[", "]"]).ShouldBeEmpty();
    }

    [Fact]
    public void Lines_before_the_array_are_not_part_of_it()
    {
        IReadOnlyList<Row> rows = RabbitCtlJson.Parse<Row>(["Listing exchanges for vhost / ...", "[", """{"name":"amq.fanout","type":"fanout"}""", "]"]);

        rows.Single().Name.ShouldBe("amq.fanout");
    }

    [Fact]
    public void Output_with_no_array_is_a_json_exception()
    {
        Should.Throw<JsonException>(() => RabbitCtlJson.Parse<Row>(["Error: unable to perform an operation on node"]));
    }

    [Fact]
    public void A_row_missing_name_is_a_json_exception()
    {
        Should.Throw<JsonException>(() => RabbitCtlJson.Parse<Row>(["[", """{"type":"topic"}""", "]"]));
    }

    [Fact]
    public void A_row_with_a_null_name_is_a_json_exception()
    {
        Should.Throw<JsonException>(() => RabbitCtlJson.Parse<Row>(["[", """{"name":null,"type":"topic"}""", "]"]));
    }

    [Fact]
    public void A_literal_null_row_is_a_json_exception()
    {
        Should.Throw<JsonException>(() => RabbitCtlJson.Parse<Row>(["[", "null", "]"]));
    }
}
