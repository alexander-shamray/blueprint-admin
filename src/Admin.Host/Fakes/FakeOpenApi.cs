using System.Net;
using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>Catalog's and Ordering's <c>/openapi/v1.json</c>, which need a bearer token as the real ones do.</summary>
internal static class FakeOpenApi
{
    public static HttpResponseMessage Document(HttpRequestMessage request, string service)
    {
        // Authentication schemes ignore case (RFC 9110 §11.1), as the services' JWT bearer handler does.
        if (!string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"openapi-{service}.json")!;
        using StreamReader reader = new(stream);

        return FakeHttp.Json(HttpStatusCode.OK, reader.ReadToEnd());
    }
}
