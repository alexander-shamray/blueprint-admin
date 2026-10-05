using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Identity;

namespace Admin.Host.Fakes;

/// <summary>
/// The gateway and the services behind it in FakePlatform mode, for the calls run-locally.md makes.
/// Authorization follows the real layering: the gateway's route policy (401 with no token), then
/// the service's permission (403 without it). Bodies are shaped like the platform's DTOs.
/// </summary>
internal static partial class FakeGateway
{
    public const string PublishedProductId = "0199a1b2-0000-7000-8000-000000000001";
    public const string PlacedOrderId = "0199a1b2-0000-7000-8000-000000000002";

    private const string Products = """
        {"items":[{"productId":"0199a1b2-0000-7000-8000-00000000000a","name":"Walnut desk","thumbnailUrl":null,"amount":19.99,"currency":"EUR","publishedAt":"2026-09-15T08:00:00+00:00"},{"productId":"0199a1b2-0000-7000-8000-00000000000b","name":"Oak shelf","thumbnailUrl":null,"amount":49.5,"currency":"EUR","publishedAt":"2026-09-15T08:01:00+00:00"}],"nextCursor":null}
        """;

    private const string Quote = """
        {"currency":"EUR","lines":[{"productId":"0199a1b2-0000-7000-8000-00000000000a","name":"Walnut desk","amount":19.99,"quantity":1,"lineTotal":19.99}],"total":19.99,"unpriced":[]}
        """;

    /// <summary>
    /// The listing's first product, stocked: StockDto's shape as Inventory answered GET stock on 2026-10-03
    /// after a PUT of onHand 10.
    /// </summary>
    private const string Stock = $$"""
        {"productId":"{{StockedProductId}}","available":10,"reserved":0,"updatedAt":"2026-10-03T16:32:05.7380524+00:00"}
        """;

    /// <summary>
    /// The placed order's reservation and payment, recorded from the live services on 2026-10-03 after the
    /// saga ran an order through, with the fake's ids in place of the recorded ones. The reference is the
    /// simulator's <c>psp_</c> plus blueprint-backend's <c>AuthorisationRequest.IdempotencyKey</c>, which names
    /// the step and the order; the amount is a SQL decimal, so it keeps its four places.
    /// </summary>
    private const string Reservation = $$"""
        {"orderId":"{{PlacedOrderId}}","status":"Reserved","lines":[{"productId":"{{StockedProductId}}","quantity":1}],"updatedAt":"2026-10-03T18:07:47.4619358+00:00"}
        """;

    private const string Payment = $$"""
        {"orderId":"{{PlacedOrderId}}","order":{"placedAt":"2026-10-03T18:07:45.8122024+00:00","cancelledAt":null},"intent":{"status":"Authorised","reference":"psp_authorise:{{PlacedOrderId}}","amount":19.9900,"currency":"EUR","declineReason":null,"createdAt":"2026-10-03T18:07:48.8202263+00:00"},"refund":null}
        """;

    internal const string StockedProductId = "0199a1b2-0000-7000-8000-00000000000a";

    /// <summary>
    /// The BFF's answer to an order it holds no row for, as blueprint-backend <c>OrderReadErrors.NotFound</c> words it
    /// and <c>tests/Web.Bff.Tests/OrderEndpointTests.cs</c> reads it: status, code and detail.
    /// </summary>
    private const string OrderNotFound = """
        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"code":"order.not_found","detail":"No order with that id."}
        """;

    [GeneratedRegex("^/api/v1/orders/(?<id>[0-9a-fA-F-]{36})/cancel$")]
    private static partial Regex CancelPath();

    // The BFF maps `/{id:guid}`, which takes any form Guid.TryParse reads; a segment that is not one matches no route.
    [GeneratedRegex("^/bff/v1/orders/(?<id>[^/]+)$")]
    private static partial Regex OrderReadPath();

    [GeneratedRegex("^/api/v1/inventory/stock/(?<id>[0-9a-fA-F-]{36})$")]
    private static partial Regex StockPath();

    [GeneratedRegex("^/api/v1/inventory/reservations/(?<id>[0-9a-fA-F-]{36})(?<action>/release|/reinstate)?$")]
    private static partial Regex ReservationPath();

    [GeneratedRegex("^/api/v1/payments/(?<id>[0-9a-fA-F-]{36})$")]
    private static partial Regex PaymentPath();

    public static HttpResponseMessage Send(HttpRequestMessage request, FakeOrders orders)
    {
        string path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        string[]? permissions = Permissions(request);
        Match orderRead = OrderReadPath().Match(path);

        HttpResponseMessage response = (request.Method.Method, path) switch
        {
            ("GET", "/api/v1/catalog/products") => FakeHttp.Json(HttpStatusCode.OK, Products),
            ("POST", "/api/v1/catalog/products") => Authorize(permissions, "catalog:write", () => FakeHttp.Json(HttpStatusCode.OK, $"\"{PublishedProductId}\"")),
            ("POST", "/api/v1/orders") => Authorize(permissions, "orders:write", () => Placed(orders)),
            ("POST", var p) when CancelPath().Match(p) is { Success: true } cancel => Authorize(permissions, "orders:cancel", () => Cancelled(orders, cancel.Groups["id"].Value)),
            ("POST", "/bff/v1/checkout/quote") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, Quote)),
            ("GET", "/bff/v1/orders") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, orders.Page())),
            ("GET", _) when orderRead.Success => Authorize(permissions, null, () => OrderRead(orders, orderRead.Groups["id"].Value)),
            (_, var p) when p.StartsWith("/api/v1/inventory/", StringComparison.Ordinal) => Authorize(permissions, "inventory:admin", () => Inventory(request.Method.Method, p)),
            (_, var p) when p.StartsWith("/api/v1/payments/", StringComparison.Ordinal) => Authorize(permissions, "payments:admin", () => Payments(request.Method.Method, p)),
            _ => FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
        };

        if (request.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? correlation))
        {
            response.Headers.TryAddWithoutValidation("X-Correlation-Id", correlation);
        }

        return response;
    }

    /// <summary>
    /// Inventory behind its route. A write answers 204 for any id, as the live PUT did although the document says
    /// 200: both end in the same ToHttpResult. A read of an id the fake does not hold answers the 404 the live
    /// service gave.
    /// </summary>
    private static HttpResponseMessage Inventory(string method, string path)
    {
        Match stock = StockPath().Match(path);
        Match reservation = ReservationPath().Match(path);

        return (method, stock.Success, reservation.Success) switch
        {
            ("PUT", true, _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("GET", true, _) when stock.Groups["id"].Value == StockedProductId => FakeHttp.Json(HttpStatusCode.OK, Stock),
            ("GET", _, true) when reservation.Groups["action"].Value == "" && reservation.Groups["id"].Value == PlacedOrderId => FakeHttp.Json(HttpStatusCode.OK, Reservation),
            ("POST", _, true) when reservation.Groups["action"].Value != "" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
        };
    }

    private static HttpResponseMessage Payments(string method, string path)
    {
        Match payment = PaymentPath().Match(path);

        return method == "GET" && payment.Success && payment.Groups["id"].Value == PlacedOrderId
            ? FakeHttp.Json(HttpStatusCode.OK, Payment)
            : FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found");
    }

    /// <summary>
    /// The gateway's policy is passed first, so the route is the BFF's to match: a segment that is no Guid gets the
    /// bare 404 of a path with no route, and one that is gets the order as the BFF writes its id, in lower case.
    /// </summary>
    private static HttpResponseMessage OrderRead(FakeOrders orders, string segment) =>
        !Guid.TryParse(Uri.UnescapeDataString(segment), out Guid id) ? FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found")
        : orders.Detail(id.ToString("D")) is { } detail ? FakeHttp.Json(HttpStatusCode.OK, detail)
        : FakeHttp.Json(HttpStatusCode.NotFound, OrderNotFound);

    private static HttpResponseMessage Placed(FakeOrders orders) => FakeHttp.Json(HttpStatusCode.OK, $"\"{orders.Place()}\"");

    private static HttpResponseMessage Cancelled(FakeOrders orders, string orderId)
    {
        orders.Cancel(orderId);

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Authorize(string[]? permissions, string? required, Func<HttpResponseMessage> allowed) =>
        permissions is null ? FakeHttp.Problem(HttpStatusCode.Unauthorized, "Unauthorized")
        : required is not null && !permissions.Contains(required) ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Forbidden")
        : allowed();

    /// <summary>
    /// The token's <c>permission</c> claim; null when there is no readable bearer token. A pasted token's
    /// payload may be any JSON: one that is not an object is unreadable, and a claim that is not an array
    /// of strings grants nothing.
    /// </summary>
    private static string[]? Permissions(HttpRequestMessage request)
    {
        // Authentication schemes ignore case (RFC 9110 §11.1), as the gateway's JWT bearer handler does.
        if (request.Headers.Authorization is not { Scheme: string scheme, Parameter: string token }
            || !scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        JsonElement claims;

        try
        {
            claims = JwtPayload.Decode(token);
        }
        catch (FormatException)
        {
            return null;
        }

        if (claims.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return claims.TryGetProperty("permission", out JsonElement granted) && granted.ValueKind == JsonValueKind.Array
            ? [.. granted.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)]
            : [];
    }
}
