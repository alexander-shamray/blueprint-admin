using System.Text.Json;
using Admin.Host.Api;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class OpenApiReaderTests
{
    internal static JsonDocument Fixture(string name)
    {
        using Stream stream = typeof(AdminOptions).Assembly.GetManifestResourceStream(name)!;

        return JsonDocument.Parse(stream);
    }

    private static IReadOnlyList<ApiOperation> Read(string source, string fixture, string gateway = "http://localhost:5000")
    {
        using JsonDocument document = Fixture(fixture);

        return OpenApiReader.Read(source, document.RootElement, gateway);
    }

    [Fact]
    public void Catalog_operations_are_rebased_onto_the_gateway_with_their_edge_policy()
    {
        IReadOnlyList<ApiOperation> operations = Read("catalog", "openapi-catalog.json");

        operations.Select(o => (o.Id, o.Method, o.Url, o.EdgePolicy)).ShouldBe(
        [
            ("catalog:PublishProduct", "POST", "http://localhost:5000/api/v1/catalog/products/", "authenticated"),
            ("catalog:GetProducts", "GET", "http://localhost:5000/api/v1/catalog/products/", "anonymous"),
        ]);
        operations.ShouldAllBe(o => o.Source == "catalog" && o.Available);
    }

    [Fact]
    public void Query_and_path_parameters_are_listed_with_their_type()
    {
        ApiOperation list = Read("catalog", "openapi-catalog.json").Single(o => o.Name == "GetProducts");
        list.QueryParameters.ShouldBe([new ApiParameter("cursor", false, "string"), new ApiParameter("limit", false, "integer")]);
        list.PathParameters.ShouldBeEmpty();
        list.ExampleBody.ShouldBeNull();
        list.HasCommandId.ShouldBeFalse();

        ApiOperation cancel = Read("ordering", "openapi-ordering.json").Single(o => o.Name == "CancelOrder");
        cancel.Url.ShouldBe("http://localhost:5000/api/v1/orders/{id}/cancel");
        cancel.PathParameters.ShouldBe([new ApiParameter("id", true, "string")]);
    }

    [Fact]
    public void Run_locally_bodies_replace_the_synthesized_example_where_one_exists()
    {
        ApiOperation publish = Read("catalog", "openapi-catalog.json").Single(o => o.Name == "PublishProduct");

        JsonDocument.Parse(publish.ExampleBody!).RootElement.GetProperty("name").GetString().ShouldBe("Walnut desk");
        publish.HasCommandId.ShouldBeTrue();

        ApiOperation cancel = Read("ordering", "openapi-ordering.json").Single(o => o.Name == "CancelOrder");
        JsonDocument.Parse(cancel.ExampleBody!).RootElement.GetProperty("reason").GetString().ShouldBe("customer_request");
        cancel.HasCommandId.ShouldBeFalse();
    }

    [Fact]
    public void An_operation_without_a_run_locally_body_gets_a_synthesized_example()
    {
        const string Document = """
        { "paths": { "/v1/things/": { "put": { "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "commandId": { "type": "string", "format": "uuid" }, "count": { "type": ["integer","string"] } } } } } } } } } }
        """;
        using JsonDocument document = JsonDocument.Parse(Document);

        ApiOperation put = OpenApiReader.Read("catalog", document.RootElement, "http://localhost:5000/").ShouldHaveSingleItem();

        put.Name.ShouldBe("PUT /v1/things/");
        put.Id.ShouldBe("catalog:PUT /v1/things/");
        put.Url.ShouldBe("http://localhost:5000/api/v1/things/");
        put.EdgePolicy.ShouldBe(GatewayRoutes.NoRoute);
        JsonDocument.Parse(put.ExampleBody!).RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        put.HasCommandId.ShouldBeTrue();
    }

    [Fact]
    public void A_document_without_paths_has_no_operations()
    {
        using JsonDocument document = JsonDocument.Parse("""{"openapi":"3.1.1"}""");

        OpenApiReader.Read("catalog", document.RootElement, "http://localhost:5000").ShouldBeEmpty();
    }

    [Fact]
    public void Curated_operations_are_the_bff_quote_and_every_hosts_readiness()
    {
        IReadOnlyList<ApiOperation> curated = CuratedOperations.All(new AdminOptions());

        curated.Select(o => (o.Id, o.Method, o.Url, o.EdgePolicy)).ShouldBe(
        [
            ("bff:Quote", "POST", "http://localhost:5000/bff/v1/checkout/quote", "authenticated"),
            ("health:gateway", "GET", "http://localhost:5000/health/ready", GatewayRoutes.Direct),
            ("health:catalog", "GET", "http://localhost:5102/health/ready", GatewayRoutes.Direct),
            ("health:ordering", "GET", "http://localhost:5101/health/ready", GatewayRoutes.Direct),
            ("health:bff", "GET", "http://localhost:5200/health/ready", GatewayRoutes.Direct),
        ]);
        JsonDocument.Parse(curated[0].ExampleBody!).RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
    }
}
