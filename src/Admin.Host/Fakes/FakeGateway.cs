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

    [GeneratedRegex("^/api/v1/orders/[0-9a-fA-F-]{36}/cancel$")]
    private static partial Regex CancelPath();

    public static HttpResponseMessage Send(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        string[]? permissions = Permissions(request);

        HttpResponseMessage response = (request.Method.Method, path) switch
        {
            ("GET", "/api/v1/catalog/products") => FakeHttp.Json(HttpStatusCode.OK, Products),
            ("POST", "/api/v1/catalog/products") => Authorize(permissions, "catalog:write", () => FakeHttp.Json(HttpStatusCode.OK, $"\"{PublishedProductId}\"")),
            ("POST", "/api/v1/orders") => Authorize(permissions, "orders:write", () => FakeHttp.Json(HttpStatusCode.OK, $"\"{PlacedOrderId}\"")),
            ("POST", var p) when CancelPath().IsMatch(p) => Authorize(permissions, "orders:cancel", () => new HttpResponseMessage(HttpStatusCode.NoContent)),
            ("POST", "/bff/v1/checkout/quote") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, Quote)),
            (_, var p) when p.StartsWith("/api/v1/inventory", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
        };

        if (request.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? correlation))
        {
            response.Headers.TryAddWithoutValidation("X-Correlation-Id", correlation);
        }

        return response;
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
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: string token })
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
