using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Api;
using Admin.Host.Config;
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

    /// <summary>
    /// Catalog's rule errors as <c>ToHttpResult</c> writes them: <c>ErrorType.Rule</c> is a 422 with the error's code
    /// and description (blueprint-backend <c>ResultExtensions</c>, <c>ProductErrors</c>).
    /// </summary>
    private static HttpResponseMessage Rule(string code, string detail) =>
        FakeHttp.Json(HttpStatusCode.UnprocessableEntity, JsonSerializer.Serialize(new
        {
            type = "https://tools.ietf.org/html/rfc4918#section-11.2",
            title = "Unprocessable Entity",
            status = 422,
            code,
            detail,
        }));

    /// <summary>The content types the BFF signs an upload for, and the extension each key carries (<c>ProductImageKeys</c>).</summary>
    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
    };

    private const long MaxImageBytes = 5 * 1024 * 1024;

    [GeneratedRegex("^uploads/(?<seller>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.(png|jpg|webp)$")]
    private static partial Regex UploadKeyShape();

    [GeneratedRegex("^/api/v1/catalog/products/(?<id>[0-9a-fA-F-]{36})$")]
    private static partial Regex ProductReadPath();

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

    [GeneratedRegex("^/api/v1/privacy/erasure-requests(?:/(?<id>[0-9a-fA-F-]{36})(?<action>/reissue)?)?$")]
    private static partial Regex ErasurePath();

    private const string ErasureNotFound = """
        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"code":"erasure_request.not_found","detail":"No erasure request under that identifier."}
        """;

    public static HttpResponseMessage Send(HttpRequestMessage request, FakeOrders orders, AdminOptions? options = null)
    {
        options ??= new AdminOptions();
        // YARP and ASP.NET routing ignore case; every id is read as a Guid, so lower case is its canonical form.
        string path = request.RequestUri!.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        string[]? permissions = Permissions(request);
        Match orderRead = OrderReadPath().Match(path);
        Match productRead = ProductReadPath().Match(path);

        HttpResponseMessage response = EdgePolicy(GatewayRoutes.PolicyFor(request.Method.Method, path), permissions) ?? (request.Method.Method, path) switch
        {
            ("GET", "/api/v1/catalog/products") => FakeHttp.Json(HttpStatusCode.OK, Products),
            ("GET", _) when productRead.Success => orders.ProductSummary(productRead.Groups["id"].Value) is { } summary
                ? FakeHttp.Json(HttpStatusCode.OK, summary)
                : FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
            ("POST", "/api/v1/catalog/products") => Authorize(permissions, "catalog:write", () => Published(orders, request, options.ImageEdgeUrl)),
            ("POST", "/bff/v1/product-images") => Authorize(permissions, "catalog:write", () => ImageUpload(orders, request, options.ObjectStoreUrl)),
            ("GET", "/bff/v1/saved-address") => Authorize(permissions, null, () => orders.Address(Seller(request)) is { } saved
                ? FakeHttp.Json(HttpStatusCode.OK, saved)
                : FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found")),
            ("PUT", "/bff/v1/saved-address") => Authorize(permissions, null, () => SavedAddress(orders, request)),
            ("POST", "/api/v1/orders") => Authorize(permissions, "orders:write", () => Placed(orders, request)),
            ("POST", var p) when CancelPath().Match(p) is { Success: true } cancel => Authorize(permissions, "orders:cancel", () => Cancelled(orders, cancel.Groups["id"].Value)),
            ("POST", "/bff/v1/checkout/quote") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, Quote)),
            ("GET", "/bff/v1/orders") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, orders.Page())),
            ("GET", _) when orderRead.Success => Authorize(permissions, null, () => OrderRead(orders, orderRead.Groups["id"].Value)),
            (_, var p) when p.StartsWith("/api/v1/inventory/", StringComparison.Ordinal) => Authorize(permissions, "inventory:admin", () => Inventory(orders, request, p)),
            (_, var p) when p.StartsWith("/api/v1/payments/", StringComparison.Ordinal) => Authorize(permissions, "payments:admin", () => Payments(request.Method.Method, p)),
            (_, var p) when p.StartsWith("/api/v1/privacy/", StringComparison.Ordinal) => Authorize(permissions, "privacy:erase", () => Privacy(orders.Erasures, request, p)),
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
    private static HttpResponseMessage Inventory(FakeOrders orders, HttpRequestMessage request, string path)
    {
        string method = request.Method.Method;
        Match stock = StockPath().Match(path);
        Match reservation = ReservationPath().Match(path);

        return (method, stock.Success, reservation.Success) switch
        {
            ("PUT", true, _) => Stocked(orders, stock.Groups["id"].Value, request),
            ("GET", true, _) when stock.Groups["id"].Value == StockedProductId => FakeHttp.Json(HttpStatusCode.OK, Stock),
            ("GET", _, true) when reservation.Groups["action"].Value == "" && reservation.Groups["id"].Value == PlacedOrderId => FakeHttp.Json(HttpStatusCode.OK, Reservation),
            ("GET", _, true) when reservation.Groups["action"].Value == "" && orders.ReservedProduct(reservation.Groups["id"].Value) is { } product =>
                FakeHttp.Json(HttpStatusCode.OK, Reservation.Replace(PlacedOrderId, reservation.Groups["id"].Value, StringComparison.Ordinal).Replace(StockedProductId, product, StringComparison.Ordinal)),
            ("POST", _, true) when reservation.Groups["action"].Value != "" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
        };
    }

    /// <summary>
    /// Privacy's three routes behind <c>privacy:erase</c>. A subject id that is no Guid, or is empty, is the validator's
    /// 400; a request id the fake never raised is <c>erasure_request.not_found</c>, and a reissue of a closed one is
    /// <c>erasure_request.closed</c>, the 404 and 422 that <c>ErasureRequestErrors</c> words (blueprint-backend
    /// <c>Privacy.Application/ErasureRequests/ErasureRequestErrors.cs</c>).
    /// </summary>
    private static HttpResponseMessage Privacy(FakeErasures erasures, HttpRequestMessage request, string path)
    {
        Match route = ErasurePath().Match(path);

        if (!route.Success)
        {
            return FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found");
        }

        string method = request.Method.Method;
        bool hasId = Guid.TryParse(route.Groups["id"].Value, out Guid id);
        bool reissue = route.Groups["action"].Value != "";

        return (method, hasId, reissue) switch
        {
            ("POST", false, _) when route.Groups["id"].Value == "" =>
                Body(request)?.Get("subjectId") is { ValueKind: JsonValueKind.String } subject && Guid.TryParse(subject.GetString(), out Guid subjectId) && subjectId != Guid.Empty
                    ? FakeHttp.Json(HttpStatusCode.OK, $"\"{erasures.Raise(subjectId)}\"")
                    : FakeHttp.Json(HttpStatusCode.BadRequest, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"SubjectId":["'Subject Id' must not be empty."]}}"""),
            ("GET", true, false) => erasures.Read(id) is { } view
                ? FakeHttp.Json(HttpStatusCode.OK, view)
                : FakeHttp.Json(HttpStatusCode.NotFound, ErasureNotFound),
            ("POST", true, true) => erasures.Reissue(id) switch
            {
                Reissued.Asked => new HttpResponseMessage(HttpStatusCode.NoContent),
                Reissued.Closed => Rule("erasure_request.closed", "The request has closed, so it no longer holds the subject to ask about."),
                _ => FakeHttp.Json(HttpStatusCode.NotFound, ErasureNotFound),
            },
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

    /// <summary>The amount the body publishes at, which the fake keeps so an order for it can end as the amount scripts (<see cref="FakeOrders.DeclinedMinorUnits"/>).</summary>
    private static HttpResponseMessage Published(FakeOrders orders, HttpRequestMessage request, string imageEdgeUrl)
    {
        JsonElement? body = Body(request);
        decimal amount = body?.Get("amount") is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out decimal read) ? read : 0m;
        string name = body?.Get("name") is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } given ? given : "Walnut desk";

        if (body?.Get("imageKey") is not { ValueKind: JsonValueKind.String } image)
        {
            return FakeHttp.Json(HttpStatusCode.OK, $"\"{orders.Publish(amount, name)}\"");
        }

        // The key must be one this seller's upload returned (§10.8): the BFF's shape, under the caller's own prefix.
        string key = image.GetString()!;
        Match shape = UploadKeyShape().Match(key);

        if (!shape.Success || shape.Groups["seller"].Value != Seller(request))
        {
            return Rule("product.image_key_invalid", "The image key is not one this seller's upload returned.");
        }

        if (orders.Stored(key) is not { } isImage)
        {
            return Rule("product.image_not_uploaded", "No image has been uploaded under that key.");
        }

        if (!isImage)
        {
            return Rule("product.image_not_an_image", "The uploaded file is not the image its content type declares.");
        }

        // Derived from the upload's key, so a retried publish copies to the same place (§10.8).
        string productKey = "products/" + key["uploads/".Length..];
        orders.ServeImage(productKey);

        return FakeHttp.Json(HttpStatusCode.OK, $"\"{orders.Publish(amount, name, $"{imageEdgeUrl.TrimEnd('/')}/{productKey}")}\"");
    }

    /// <summary>
    /// §10.8's upload endpoint: a pre-signed <c>PUT</c> for one key the server chose, under the caller's own prefix. The
    /// signature is a placeholder the fake store looks for; the headers are the two it signs.
    /// </summary>
    private static HttpResponseMessage ImageUpload(FakeOrders orders, HttpRequestMessage request, string objectStoreUrl)
    {
        JsonElement? body = Body(request);
        string? contentType = body?.Get("contentType") is { ValueKind: JsonValueKind.String } type ? type.GetString() : null;
        long? size = body?.Get("sizeBytes") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out long read) ? read : null;

        if (contentType is null || !ImageExtensions.TryGetValue(contentType, out string? extension) || size is not (> 0 and <= MaxImageBytes))
        {
            return FakeHttp.Json(HttpStatusCode.BadRequest, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400}""");
        }

        string key = $"uploads/{Seller(request)}/{Guid.CreateVersion7():D}.{extension}";

        return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            uploadUrl = $"{objectStoreUrl.TrimEnd('/')}/{FakeObjectStore.Bucket}/{key}?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Expires=300&X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost&X-Amz-Signature=fake",
            headers = new Dictionary<string, string> { ["Content-Type"] = contentType, ["Content-Length"] = size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            key,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }));
    }

    /// <summary>§10.9's saved address: the caller's own, trimmed, the country upper case, and refused where a required line is empty or the country is not two letters.</summary>
    private static HttpResponseMessage SavedAddress(FakeOrders orders, HttpRequestMessage request)
    {
        JsonElement? body = Body(request);
        string? Line(string name) => body?.Get(name) is { ValueKind: JsonValueKind.String } v ? v.GetString()?.Trim() : null;

        string? line1 = Line("line1");
        string? line2 = Line("line2");
        string? city = Line("city");
        string? postalCode = Line("postalCode");
        string? country = Line("country");

        if (string.IsNullOrEmpty(line1) || string.IsNullOrEmpty(city) || string.IsNullOrEmpty(postalCode)
            || country is not { Length: 2 } || !country.All(char.IsAsciiLetter))
        {
            return FakeHttp.Json(HttpStatusCode.BadRequest, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400}""");
        }

        string saved = JsonSerializer.Serialize(new
        {
            line1,
            line2 = string.IsNullOrEmpty(line2) ? null : line2,
            city,
            postalCode,
            country = country.ToUpperInvariant(),
        });
        orders.SaveAddress(Seller(request), saved);

        return FakeHttp.Json(HttpStatusCode.OK, saved);
    }

    /// <summary>
    /// The caller's id as the fake gives one: the token carries no <c>sub</c>, so a stable Guid is made of the username,
    /// and an unreadable token is the zero Guid. The real subject is the realm's; a seller prefix only has to be the caller's own.
    /// </summary>
    private static string Seller(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is { } token)
        {
            try
            {
                JsonElement claims = JwtPayload.Decode(token);

                if (claims.ValueKind == JsonValueKind.Object
                    && claims.TryGetProperty("preferred_username", out JsonElement user)
                    && user.ValueKind == JsonValueKind.String)
                {
                    return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(user.GetString()!)).AsSpan(0, 16)).ToString("D");
                }
            }
            catch (FormatException)
            {
            }
        }

        return Guid.Empty.ToString("D");
    }

    private static HttpResponseMessage Stocked(FakeOrders orders, string productId, HttpRequestMessage request)
    {
        if (Body(request)?.Get("onHand") is { ValueKind: JsonValueKind.Number } onHand && onHand.TryGetInt32(out int count))
        {
            orders.Stock(productId, count);
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Placed(FakeOrders orders, HttpRequestMessage request)
    {
        string? product = null;

        if (Body(request)?.Get("items") is { ValueKind: JsonValueKind.Array } items && items.GetArrayLength() > 0)
        {
            product = items[0].Get("productId") is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;
        }

        // run-locally.md: the carrier's answer is scripted by the delivery postal code, the one field of the order it sees.
        string? postalCode = Body(request)?.Get("shippingAddress")?.Get("postalCode") is { ValueKind: JsonValueKind.String } code ? code.GetString() : null;

        return FakeHttp.Json(HttpStatusCode.OK, $"\"{orders.Place(product, postalCode)}\"");
    }

    /// <summary>
    /// The request's JSON object, or null where there is none or it is not one. The body is a string already in memory
    /// on this path, so the read does not wait on anything.
    /// </summary>
    private static JsonElement? Body(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : null;

    private static HttpResponseMessage Cancelled(FakeOrders orders, string orderId)
    {
        return orders.Cancel(orderId) == CancelOutcome.AlreadyShipped
            ? FakeHttp.Json(HttpStatusCode.UnprocessableEntity, AlreadyShipped)
            : new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    /// <summary>The platform's refusal of a cancel after despatch, as run-locally.md ("After despatch there is nothing to compensate") observed it live.</summary>
    private const string AlreadyShipped = """
        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Entity","status":422,"code":"order.already_shipped","detail":"An order that has already shipped cannot be cancelled; raise a return instead."}
        """;

    // The route policy comes first on the platform, whatever the destination goes on to serve (GatewayRoutes owns the
    // table), so a path the fake does not model still answers 401 with no token. Null admits the call.
    private static HttpResponseMessage? EdgePolicy(string policy, string[]? permissions) =>
        permissions is null && policy != "anonymous" && policy != GatewayRoutes.NoRoute ? FakeHttp.Problem(HttpStatusCode.Unauthorized, "Unauthorized")
        : permissions is not null && policy.Contains(':', StringComparison.Ordinal) && !permissions.Contains(policy) ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Forbidden")
        : null;

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
