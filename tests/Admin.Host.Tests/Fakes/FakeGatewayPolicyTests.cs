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

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeOrders orders;

    public FakeGatewayPolicyTests() => orders = new(time);

    [Fact]
    public async Task A_cancel_of_an_order_the_carrier_collected_is_the_422_the_platform_gave()
    {
        using HttpRequestMessage place = new(HttpMethod.Post, Origin + "/api/v1/orders")
        {
            Content = new StringContent("""{"items":[{"productId":"0199a1b2-0000-7000-8000-000000000001","quantity":1}],"shippingAddress":{"postalCode":"SIM-TRANSIT"}}"""),
        };
        place.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt("""["orders:write"]"""));
        using HttpResponseMessage placed = FakeGateway.Send(place, orders);
        string id = (await placed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim('"');
        time.Advance(FakeOrders.Step * 3);

        using HttpRequestMessage cancel = new(HttpMethod.Post, $"{Origin}/api/v1/orders/{id}/cancel")
        {
            Content = new StringContent("{}"),
        };
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt("""["orders:cancel"]"""));
        using HttpResponseMessage response = FakeGateway.Send(cancel, orders);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("order.already_shipped");
    }

    private (HttpStatusCode Status, string Body) ReadReservation(string orderId)
    {
        using HttpRequestMessage read = new(HttpMethod.Get, $"{Origin}/api/v1/inventory/reservations/{orderId}");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt("""["inventory:admin"]"""));
        using HttpResponseMessage response = FakeGateway.Send(read, orders);

        return (response.StatusCode, response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());
    }

    private HttpStatusCode ReadSaga(string orderId, string? permissions)
    {
        using HttpRequestMessage read = new(HttpMethod.Get, $"{Origin}/api/v1/orders/{orderId}/saga");

        if (permissions is not null)
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(permissions));
        }

        using HttpResponseMessage response = FakeGateway.Send(read, orders);

        return response.StatusCode;
    }

    [Fact]
    public void The_sagas_read_wants_the_staff_permission_and_answers_404_for_an_order_with_no_row()
    {
        string id = orders.Place();

        ReadSaga(id, null).ShouldBe(HttpStatusCode.Unauthorized);
        ReadSaga(id, """["orders:write","orders:cancel"]""").ShouldBe(HttpStatusCode.Forbidden);
        ReadSaga(id, """["orders:saga-read"]""").ShouldBe(HttpStatusCode.OK);
        ReadSaga("0199a1b2-0000-7000-8000-0000000000ff", """["orders:saga-read"]""").ShouldBe(HttpStatusCode.NotFound);

        time.Advance(FakeOrders.Step * 3);

        ReadSaga(id, """["orders:saga-read"]""").ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void A_reservation_read_of_an_order_the_fake_placed_answers_reserved_for_its_product_and_that_order()
    {
        string product = orders.Publish(10.09m);
        orders.Stock(product, 10);
        orders.Place();
        string id = orders.Place(product);

        (HttpStatusCode status, string body) = ReadReservation(id);

        status.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain($"\"orderId\":\"{id}\"");
        body.ShouldContain("\"status\":\"Reserved\"");
        body.ShouldContain($"\"productId\":\"{product}\"");
    }

    [Fact]
    public void A_reservation_read_of_an_order_whose_stock_was_refused_or_that_was_never_placed_is_a_404()
    {
        string product = orders.Publish(19.99m);
        orders.Stock(product, 0);
        orders.Place();
        string refused = orders.Place(product);

        ReadReservation(refused).Status.ShouldBe(HttpStatusCode.NotFound);
        ReadReservation("0199a1b2-0000-7000-8000-0000000000ff").Status.ShouldBe(HttpStatusCode.NotFound);
    }

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
