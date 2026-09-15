using System.Net;
using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>Catalog's and Ordering's <c>/openapi/v1.json</c>, which need a bearer token as the real ones do.</summary>
internal static class FakeOpenApi
{
    public static HttpResponseMessage Document(HttpRequestMessage request, string service)
    {
        if (request.Headers.Authorization?.Scheme != "Bearer")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"openapi-{service}.json")!;
        using StreamReader reader = new(stream);

        return FakeHttp.Json(HttpStatusCode.OK, reader.ReadToEnd());
    }
}
