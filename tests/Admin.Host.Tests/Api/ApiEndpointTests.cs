using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class ApiEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly HttpClient client = factory.CreateClient();

    private async Task<JsonElement> ProxyAsync(object request)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/proxy", request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task Operations_list_the_fake_documents_rebased_onto_the_gateway_and_the_curated_calls()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/catalog/operations", Token);

        view.GetProperty("sources").EnumerateArray().ShouldAllBe(s => s.GetProperty("available").GetBoolean());
        view.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ShouldBe(
        [
            "catalog:PublishProduct", "catalog:GetProducts", "ordering:PlaceOrder", "ordering:CancelOrder",
            "bff:Quote", "health:gateway", "health:catalog", "health:ordering", "health:bff",
        ]);
        JsonElement publish = view.GetProperty("operations")[0];
        publish.GetProperty("url").GetString().ShouldBe("http://localhost:5000/api/v1/catalog/products/");
        publish.GetProperty("edgePolicy").GetString().ShouldBe("authenticated");
        publish.GetProperty("hasCommandId").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reload_answers_the_same_catalog()
    {
        HttpResponseMessage response = await client.PostAsync("/api/catalog/reload", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("operations").GetArrayLength().ShouldBe(9);
    }

    [Fact]
    public async Task An_anonymous_product_listing_comes_back_with_its_status_body_and_correlation_id()
    {
        JsonElement result = await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/catalog/products/", correlationId = "e2e-list-1" });

        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBe(200);
        result.GetProperty("body").GetString()!.ShouldContain("Walnut desk");
        result.GetProperty("correlationId").GetString().ShouldBe("e2e-list-1");
        result.GetProperty("headers").GetProperty("X-Correlation-Id")[0].GetString().ShouldBe("e2e-list-1");
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("browser", 403)]
    [InlineData("demo", 200)]
    public async Task Publishing_passes_the_platforms_status_through_for_each_identity(string? username, int status)
    {
        JsonElement result = await ProxyAsync(new
        {
            method = "POST",
            url = "http://localhost:5000/api/v1/catalog/products/",
            body = """{"commandId":"0199a1b2-0000-7000-8000-00000000abcd","name":"Walnut desk","amount":19.99,"currency":"EUR"}""",
            identity = new { username },
        });

        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBe(status);
    }

    [Fact]
    public async Task A_wrong_password_is_a_token_rejection()
    {
        JsonElement result = await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/orders", identity = new { username = "demo", password = "nope" } });

        result.GetProperty("outcome").GetString().ShouldBe("tokenRejected");
        result.GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Fact]
    public async Task A_url_off_the_surfaces_is_a_problem_and_not_sent()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/proxy", new { method = "GET", url = "http://localhost:8080/admin" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Request not sent");
        problem.GetProperty("detail").GetString()!.ShouldContain("is not one of the configured API surfaces");
    }

    [Fact]
    public async Task Cancel_and_quote_and_inventory_answer_as_the_gateway_would()
    {
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/orders/0199a1b2-0000-7000-8000-000000000002/cancel", body = """{"reason":"customer_request"}""", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(204);
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/bff/v1/checkout/quote", body = "{}", identity = new { username = "browser" } }))
            .GetProperty("body").GetString()!.ShouldContain("\"total\"");
        (await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/inventory/items", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(502);
    }
}
