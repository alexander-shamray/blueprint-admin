using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Tests.Identity;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>
/// A request sent to Admin:BffUrl answers as the live BFF did on 2026-10-18 (run-locally.md, "Call the BFF directly (no
/// gateway)"; #136): its own auth first, then its routes, which are the gateway's <c>/bff</c> ones without the prefix.
/// </summary>
public sealed class FakeBffTests : IDisposable
{
    private readonly AdminOptions options = new();
    private readonly FakeOrders orders = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));
    private readonly HttpClient client;

    public FakeBffTests() => client = new HttpClient(new FakePlatformHandler(options, orders));

    public void Dispose() => client.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> GetAsync(string url, bool withToken, string? correlationId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (withToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenServiceTests.Jwt("""{"permission":["orders:write"]}"""));
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return await client.SendAsync(request, Token);
    }

    private string Bff(string path) => options.BffUrl.TrimEnd('/') + path;

    private string PlacedOrder()
    {
        using HttpRequestMessage place = new(HttpMethod.Post, options.GatewayUrl.TrimEnd('/') + "/api/v1/orders")
        {
            Content = new StringContent("""{"items":[{"productId":"0199a1b2-0000-7000-8000-000000000001","quantity":1}]}"""),
        };
        place.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenServiceTests.Jwt("""{"permission":["orders:write"]}"""));

        return FakeGateway.Send(place, orders).Content.ReadAsStringAsync(Token).GetAwaiter().GetResult().Trim('"');
    }

    [Fact]
    public async Task An_order_read_with_no_token_is_the_bffs_own_401_with_a_bearer_challenge_and_its_correlation_id()
    {
        using HttpResponseMessage response = await GetAsync(Bff($"/v1/orders/{PlacedOrder()}"), withToken: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        string correlationId = response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem();
        correlationId.ShouldMatch("^[0-9a-f]{32}$");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Unauthorized");
        problem.RootElement.GetProperty("correlationId").GetString().ShouldBe(correlationId);
    }

    [Fact]
    public async Task A_path_the_bff_does_not_serve_is_a_404_problem_not_the_generic_fake_200()
    {
        using HttpResponseMessage response = await GetAsync(Bff("/v1/no-such-path"), withToken: true, correlationId: "direct-1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.WwwAuthenticate.ShouldBeEmpty();
        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["direct-1"]);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        problem.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
        problem.RootElement.GetProperty("correlationId").GetString().ShouldBe("direct-1");
    }

    [Fact]
    public async Task An_order_read_with_a_token_is_the_body_the_gateway_forwards_from_bff_v1()
    {
        string orderId = PlacedOrder();

        using HttpResponseMessage direct = await GetAsync(Bff($"/v1/orders/{orderId}"), withToken: true);
        using HttpResponseMessage viaGateway = await GetAsync(options.GatewayUrl.TrimEnd('/') + $"/bff/v1/orders/{orderId}", withToken: true);

        direct.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await direct.Content.ReadAsStringAsync(Token)).ShouldBe(await viaGateway.Content.ReadAsStringAsync(Token));
        direct.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem().ShouldNotBeNullOrEmpty();
    }

    // The BFF's own order-not-found is a problem like any other, with a correlationId.
    [Fact]
    public async Task An_order_the_bff_holds_no_row_for_is_its_problem_with_the_correlation_id()
    {
        using HttpResponseMessage response = await GetAsync(Bff($"/v1/orders/{Guid.Empty}"), withToken: true, correlationId: "direct-2");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("order.not_found");
        problem.RootElement.GetProperty("correlationId").GetString().ShouldBe("direct-2");
    }

    // Common.Web maps all three probes AllowAnonymous on every host, the BFF included.
    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    [InlineData("/health/startup")]
    public async Task Each_probe_on_the_bffs_port_answers_with_no_token(string probe)
    {
        using HttpResponseMessage response = await GetAsync(Bff(probe), withToken: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
