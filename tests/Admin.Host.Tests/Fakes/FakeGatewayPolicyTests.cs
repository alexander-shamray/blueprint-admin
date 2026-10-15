using System.Net;
using System.Net.Http.Headers;
using Admin.Host.Fakes;
using Admin.Host.Tests.Identity;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>
/// The fake gateway applies the route policy before the destination matches its own routes, as the platform does, so
/// a path the fake does not model answers what the gateway would.
/// </summary>
public sealed class FakeGatewayPolicyTests
{
    private const string Origin = "http://gateway.test";

    private readonly FakeOrders orders = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("DELETE", "/bff/v1/orders/0199a1b2-0000-7000-8000-000000000002")]
    [InlineData("GET", "/bff/v1/orders/0199a1b2-0000-7000-8000-000000000002/x")]
    [InlineData("GET", "/bff/v1/nothing")]
    [InlineData("GET", "/api/v1/orders/nothing")]
    [InlineData("GET", "/api/v1/catalog/products/mine")]
    [InlineData("GET", "/api/v1/privacy/anything")]
    [InlineData("GET", "/BFF/v1/orders")]
    public void A_request_with_no_token_to_a_guarded_path_the_fake_does_not_serve_is_401(string method, string path) =>
        Send(method, path, token: null).ShouldBe(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("DELETE", "/bff/v1/orders/0199a1b2-0000-7000-8000-000000000002")]
    [InlineData("GET", "/bff/v1/orders/0199a1b2-0000-7000-8000-000000000002/x")]
    [InlineData("GET", "/bff/v1/nothing")]
    [InlineData("GET", "/api/v1/orders/nothing")]
    public void The_same_request_with_a_token_is_the_404_of_a_path_with_no_route(string method, string path) =>
        Send(method, path, Jwt("[]")).ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public void A_permission_policy_refuses_a_token_without_the_permission_before_the_route_is_matched() =>
        Send("GET", "/api/v1/privacy/anything", Jwt("""["catalog:write"]""")).ShouldBe(HttpStatusCode.Forbidden);

    [Fact]
    public void An_anonymous_route_answers_with_no_token() =>
        Send("GET", "/api/v1/catalog/products", token: null).ShouldBe(HttpStatusCode.OK);

    [Fact]
    public void A_path_in_upper_case_reaches_the_arm_it_names()
    {
        string path = $"/BFF/V1/ORDERS/{orders.Place().ToUpperInvariant()}";

        Send("GET", path, Jwt("[]")).ShouldBe(HttpStatusCode.OK);
        Send("POST", "/API/V1/ORDERS", Jwt("""["orders:write"]""")).ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"items":[{"productId":123,"quantity":1}]}""")]
    [InlineData("""{"items":[{"productId":{},"quantity":1}]}""")]
    [InlineData("""{"items":"x"}""")]
    [InlineData("""{"items":[1]}""")]
    [InlineData("""{"items":[]}""")]
    [InlineData("not json")]
    public async Task An_order_body_the_script_cannot_read_still_places_an_unscripted_order(string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Origin + "/api/v1/orders")
        {
            Content = new StringContent(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt("""["orders:write"]"""));

        using HttpResponseMessage response = FakeGateway.Send(request, orders);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe($"\"{FakeGateway.PlacedOrderId}\"");
    }

    private static string Jwt(string permissions) => TokenServiceTests.Jwt($$"""{"permission":{{permissions}}}""");

    private HttpStatusCode Send(string method, string path, string? token)
    {
        using HttpRequestMessage request = new(new HttpMethod(method), Origin + path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = FakeGateway.Send(request, orders);

        return response.StatusCode;
    }
}
