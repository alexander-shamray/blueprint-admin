using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Admin.Host.Fakes;

/// <summary>
/// Catalog's and Ordering's <c>/openapi/v1.json</c>, which need a bearer token as the real ones do. From its
/// second fetch on, Catalog's document has moved: <c>ProductSummaryDto</c> gains <c>quantityAvailable</c>, the
/// field the reference client's listing gained, so the API screen has a change to report against the
/// baseline its first fetch took (spec §5.7).
/// </summary>
internal static class FakeOpenApi
{
    /// <summary>
    /// Where FakePlatform keeps its baselines: fresh per process, like the fetch count, so the first fetch of the
    /// process is always the one that takes the baseline and a later run never inherits an accepted one.
    /// </summary>
    public static readonly string BaselineDirectory = Path.Combine(Path.GetTempPath(), $"blueprint-admin-fake-{Guid.NewGuid():N}");

    private static int catalogFetches;

    public static HttpResponseMessage Document(HttpRequestMessage request, string service)
    {
        // Authentication schemes ignore case (RFC 9110 §11.1), as the services' JWT bearer handler does.
        if (!string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"openapi-{service}.json")!;
        using StreamReader reader = new(stream);
        string document = reader.ReadToEnd();

        if (service == "catalog" && Interlocked.Increment(ref catalogFetches) > 1)
        {
            JsonNode moved = JsonNode.Parse(document)!;
            moved["components"]!["schemas"]!["ProductSummaryDto"]!["properties"]!.AsObject()["quantityAvailable"] =
                new JsonObject { ["type"] = "integer", ["format"] = "int32" };
            document = moved.ToJsonString();
        }

        return FakeHttp.Json(HttpStatusCode.OK, document);
    }
}
