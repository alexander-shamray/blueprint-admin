using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Api;
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
    public async Task The_fake_catalog_document_moves_after_its_first_fetch_and_accepting_it_clears_the_change()
    {
        // FakeOpenApi serves the recorded document until Catalog has a baseline, and the moved one after; the
        // baseline is process-wide, so whichever host fetched first, it holds the recorded document.
        await client.GetFromJsonAsync<JsonElement>("/api/catalog/operations", Token);
        JsonElement reloaded = await (await client.PostAsync("/api/catalog/reload", null, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);
        JsonElement changes = Source(reloaded, "catalog").GetProperty("changes");

        changes.GetProperty("fieldsAdded").EnumerateArray().Select(f => f.GetString()).ShouldContain("PublishProductCommand.description");
        Source(reloaded, "ordering").GetProperty("changes").GetProperty("fieldsAdded").GetArrayLength().ShouldBe(0);

        HttpResponseMessage accepted = await client.PostAsync("/api/catalog/baseline/catalog", null, Token);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement after = await accepted.Content.ReadFromJsonAsync<JsonElement>(Token);
        Source(after, "catalog").GetProperty("changes").GetProperty("fieldsAdded").GetArrayLength().ShouldBe(0);

        (await client.PostAsync("/api/catalog/baseline/shipping", null, Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static JsonElement Source(JsonElement view, string name) =>
        view.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Operations_list_the_fake_documents_rebased_onto_the_gateway_and_the_curated_calls()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/catalog/operations", Token);

        view.GetProperty("sources").EnumerateArray().ShouldAllBe(s => s.GetProperty("available").GetBoolean());
        view.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ShouldBe(
        [
            "catalog:PublishProduct", "catalog:GetProducts", "ordering:PlaceOrder", "ordering:CancelOrder",
            "inventory:SetOnHand", "inventory:GetStock", "inventory:GetReservation", "inventory:ReleaseReservation",
            "inventory:ReinstateReservation", "payments:GetPayment",
            "bff:Quote", "bff:ListOrders", "bff:GetOrder", "health:gateway", "health:catalog", "health:ordering", "health:bff", "health:inventory",
            "health:payments", "simulator:Requests", "carrier:Requests",
        ]);
        JsonElement publish = view.GetProperty("operations")[0];
        publish.GetProperty("url").GetString().ShouldBe("http://localhost:5000/api/v1/catalog/products");
        publish.GetProperty("edgePolicy").GetString().ShouldBe("authenticated");
        publish.GetProperty("hasCommandId").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reload_answers_the_same_catalog()
    {
        HttpResponseMessage response = await client.PostAsync("/api/catalog/reload", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("operations").GetArrayLength().ShouldBe(21);
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

    [Theory]
    [InlineData("POST", "http://localhost:5000/api/v1/catalog/products/")]
    [InlineData("GET", "http://localhost:5102/openapi/v1.json")]
    public async Task A_pasted_token_with_a_lower_case_bearer_scheme_is_accepted_as_the_platform_would(string method, string url)
    {
        JsonElement result = await ProxyAsync(new
        {
            method,
            url,
            headers = new Dictionary<string, string> { ["authorization"] = $"bearer {Identity.TokenServiceTests.Jwt("""{"permission":["catalog:write"]}""")}" },
            body = method == "POST" ? """{"commandId":"0199a1b2-0000-7000-8000-00000000abcd","name":"Walnut desk","amount":19.99,"currency":"EUR"}""" : null,
        });

        result.GetProperty("status").GetInt32().ShouldBe(200);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("""{"permission":"catalog:write"}""")]
    [InlineData("""{"permission":[1]}""")]
    public async Task A_pasted_token_with_unexpected_claim_shapes_is_refused_not_a_host_error(string payload)
    {
        JsonElement result = await ProxyAsync(new
        {
            method = "POST",
            url = "http://localhost:5000/api/v1/catalog/products/",
            headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Identity.TokenServiceTests.Jwt(payload)}" },
            body = """{"commandId":"0199a1b2-0000-7000-8000-00000000abcd","name":"Walnut desk","amount":19.99,"currency":"EUR"}""",
        });

        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBeOneOf(401, 403);
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
    public async Task Cancel_and_quote_answer_as_the_gateway_would()
    {
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/orders/0199a1b2-0000-7000-8000-000000000002/cancel", body = """{"reason":"customer_request"}""", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(204);
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/bff/v1/checkout/quote", body = "{}", identity = new { username = "browser" } }))
            .GetProperty("body").GetString()!.ShouldContain("\"total\"");
    }

    [Fact]
    public async Task The_bff_order_read_needs_a_token_and_follows_the_order_the_fake_placed()
    {
        const string reads = "http://localhost:5000/bff/v1/orders/";

        (await ProxyAsync(new { method = "GET", url = reads + "0199a1b2-0000-7000-8000-0000000000ff" })).GetProperty("status").GetInt32().ShouldBe(401);
        JsonElement unknown = await ProxyAsync(new { method = "GET", url = reads + "0199a1b2-0000-7000-8000-0000000000ff", identity = new { username = "browser" } });
        unknown.GetProperty("status").GetInt32().ShouldBe(404);
        unknown.GetProperty("body").GetString()!.ShouldContain("\"order.not_found\"");

        // Not a Guid: the BFF's route constraint matches nothing, so the answer is no route's, not the order read's.
        JsonElement unrouted = await ProxyAsync(new { method = "GET", url = reads + "not-an-order", identity = new { username = "demo" } });
        unrouted.GetProperty("status").GetInt32().ShouldBe(404);
        unrouted.GetProperty("body").GetString()!.ShouldNotContain("order.not_found");

        // The gateway's policy comes before the BFF's route, so with no token even that is a 401.
        (await ProxyAsync(new { method = "GET", url = reads + "not-an-order" })).GetProperty("status").GetInt32().ShouldBe(401);

        JsonElement placed = await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/orders", body = RunLocallyExamples.For("PlaceOrder"), identity = new { username = "demo" } });
        placed.GetProperty("status").GetInt32().ShouldBe(200);
        string orderId = JsonSerializer.Deserialize<string>(placed.GetProperty("body").GetString()!)!;
        JsonElement detail = await ProxyAsync(new { method = "GET", url = reads + orderId, identity = new { username = "demo" } });

        detail.GetProperty("status").GetInt32().ShouldBe(200);
        using JsonDocument order = JsonDocument.Parse(detail.GetProperty("body").GetString()!);
        order.RootElement.GetProperty("orderId").GetString().ShouldBe(orderId);
        order.RootElement.GetProperty("timeline").GetProperty("placed").ValueKind.ShouldBe(JsonValueKind.String);

        // The route takes any form Guid.TryParse reads, and the BFF writes the id back in its own form.
        foreach (string written in new[] { orderId.Replace("-", "", StringComparison.Ordinal), orderId.ToUpperInvariant(), $"%7B{orderId}%7D" })
        {
            JsonElement other = await ProxyAsync(new { method = "GET", url = reads + written, identity = new { username = "demo" } });
            other.GetProperty("status").GetInt32().ShouldBe(200, written);
            using JsonDocument same = JsonDocument.Parse(other.GetProperty("body").GetString()!);
            same.RootElement.GetProperty("orderId").GetString().ShouldBe(orderId, written);
        }

        // Thirty-six characters of hex and hyphens that are no Guid are no route either.
        JsonElement shaped = await ProxyAsync(new { method = "GET", url = reads + "0199a1b2000070008000000000000002----", identity = new { username = "demo" } });
        shaped.GetProperty("status").GetInt32().ShouldBe(404);
        shaped.GetProperty("body").GetString()!.ShouldNotContain("order.not_found");
    }

    [Fact]
    public async Task The_bff_order_list_needs_a_token_and_lists_an_order_the_fake_placed()
    {
        const string list = "http://localhost:5000/bff/v1/orders";

        (await ProxyAsync(new { method = "GET", url = list })).GetProperty("status").GetInt32().ShouldBe(401);

        JsonElement placed = await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/orders", body = RunLocallyExamples.For("PlaceOrder"), identity = new { username = "demo" } });
        string orderId = JsonSerializer.Deserialize<string>(placed.GetProperty("body").GetString()!)!;
        JsonElement page = await ProxyAsync(new { method = "GET", url = list, identity = new { username = "demo" } });

        page.GetProperty("status").GetInt32().ShouldBe(200);
        using JsonDocument orders = JsonDocument.Parse(page.GetProperty("body").GetString()!);
        orders.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orderId").GetString()).ShouldContain(orderId);
        orders.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(null, "/api/v1/inventory/stock/0199a1b2-0000-7000-8000-00000000000a", 401)]
    [InlineData("browser", "/api/v1/inventory/stock/0199a1b2-0000-7000-8000-00000000000a", 403)]
    [InlineData("demo", "/api/v1/inventory/stock/0199a1b2-0000-7000-8000-00000000000a", 200)]
    [InlineData("demo", "/api/v1/inventory/stock/0199a1b2-0000-7000-8000-0000000000ff", 404)]
    [InlineData("demo", "/api/v1/inventory/reservations/0199a1b2-0000-7000-8000-000000000002", 200)]
    [InlineData(null, "/api/v1/payments/0199a1b2-0000-7000-8000-000000000002", 401)]
    [InlineData("browser", "/api/v1/payments/0199a1b2-0000-7000-8000-000000000002", 403)]
    [InlineData("demo", "/api/v1/payments/0199a1b2-0000-7000-8000-000000000002", 200)]
    [InlineData("demo", "/api/v1/payments/0199a1b2-0000-7000-8000-0000000000ff", 404)]
    public async Task Inventory_and_payments_answer_behind_their_admin_policies_as_the_live_services_did(string? username, string path, int status)
    {
        (await ProxyAsync(new { method = "GET", url = $"http://localhost:5000{path}", identity = new { username } }))
            .GetProperty("status").GetInt32().ShouldBe(status);
    }

    [Fact]
    public async Task A_stock_set_and_a_reservation_release_answer_no_content_as_the_live_write_did()
    {
        (await ProxyAsync(new { method = "PUT", url = "http://localhost:5000/api/v1/inventory/stock/0199a1b2-0000-7000-8000-00000000000a", body = """{"onHand":10}""", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(204);
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/inventory/reservations/0199a1b2-0000-7000-8000-000000000002/release", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(204);
    }

    [Fact]
    public async Task The_placed_orders_payment_is_authorised_with_the_simulators_reference()
    {
        JsonElement result = await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/payments/0199a1b2-0000-7000-8000-000000000002", identity = new { username = "demo" } });

        using JsonDocument body = JsonDocument.Parse(result.GetProperty("body").GetString()!);
        body.RootElement.GetProperty("intent").GetProperty("status").GetString().ShouldBe("Authorised");
        body.RootElement.GetProperty("intent").GetProperty("reference").GetString()
            .ShouldBe("psp_authorise:0199a1b2-0000-7000-8000-000000000002");
    }
}
