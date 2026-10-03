using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Admin.Host.Api;

namespace Admin.Host.Fakes;

/// <summary>
/// Catalog's and Ordering's <c>/openapi/v1.json</c>, which need a bearer token as the real ones do. Once Catalog has
/// a baseline, its document has moved: <c>ProductSummaryDto</c> gains <c>quantityAvailable</c>, the field the
/// reference client's listing gained, so the API screen has a change to report (spec §5.7). Keyed on the
/// baseline rather than on a fetch count, so two hosts fetching at once can only ever baseline the recorded
/// document.
/// </summary>
internal static class FakeOpenApi
{
    /// <summary>
    /// FakePlatform's baselines, one store for every fake host in the process and fresh per process, so a later
    /// run never inherits an accepted baseline and a fake document never becomes a real service's.
    /// </summary>
    public static readonly OpenApiBaselines Baselines =
        new(Path.Combine(Path.GetTempPath(), $"blueprint-admin-fake-{Guid.NewGuid():N}"), TimeProvider.System);

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

        if (service == "catalog" && Baselines.Has(service))
        {
            JsonNode moved = JsonNode.Parse(document)!;
            moved["components"]!["schemas"]!["ProductSummaryDto"]!["properties"]!.AsObject()["quantityAvailable"] =
                new JsonObject { ["type"] = "integer", ["format"] = "int32" };
            document = moved.ToJsonString();
        }

        return FakeHttp.Json(HttpStatusCode.OK, document);
    }
}
