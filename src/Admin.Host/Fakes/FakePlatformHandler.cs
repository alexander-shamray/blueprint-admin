using System.Net;
using System.Net.Mime;
using System.Text;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// Every outbound HTTP call in FakePlatform mode. Readiness and Grafana's health answer 200 on any
/// host; the realm token endpoint, the four OpenAPI documents, the gateway, the payment simulator's
/// request log and Grafana's datasource proxies are recordings (FakeKeycloak, FakeOpenApi, FakeGateway,
/// FakeGrafana), and the BFF's order read is <see cref="FakeOrders"/>. Hosts are told apart by the configured URLs.
/// </summary>
public sealed class FakePlatformHandler(AdminOptions options, FakeOrders orders) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string path = uri.AbsolutePath;

        HttpResponseMessage response =
            path == "/health/ready" ? FakeHttp.Json(HttpStatusCode.OK, """{"status":"Healthy"}""")
            : path == "/api/health" ? FakeHttp.Json(HttpStatusCode.OK, """{"database":"ok","version":"fake"}""")
            : Is(uri, options.KeycloakUrl) && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) ? await FakeKeycloak.TokenAsync(request, cancellationToken)
            : path == "/openapi/v1.json" && Is(uri, options.CatalogUrl) ? FakeOpenApi.Document(request, "catalog")
            : path == "/openapi/v1.json" && Is(uri, options.OrderingUrl) ? FakeOpenApi.Document(request, "ordering")
            : path == "/openapi/v1.json" && Is(uri, options.InventoryUrl) ? FakeOpenApi.Document(request, "inventory")
            : path == "/openapi/v1.json" && Is(uri, options.PaymentsUrl) ? FakeOpenApi.Document(request, "payments")
            // WireMock.Net's request log, as the simulator answered it with nothing yet asked of it.
            : Is(uri, options.PaymentSimulatorUrl) && path == "/__admin/requests" ? FakeHttp.Json(HttpStatusCode.OK, "[]")
            // The carrier simulator is the same WireMock.Net image, answering the same way before Shipping despatches.
            : Is(uri, options.CarrierSimulatorUrl) && path == "/__admin/requests" ? FakeHttp.Json(HttpStatusCode.OK, "[]")
            : Is(uri, options.GrafanaUrl) && path == "/api/datasources" ? FakeGrafana.Datasources()
            : Is(uri, options.GrafanaUrl) && path.Contains("/loki/api/v1/query_range", StringComparison.Ordinal) ? FakeGrafana.Loki(request)
            : Is(uri, options.GrafanaUrl) && path.Contains("/api/traces/", StringComparison.Ordinal) ? FakeGrafana.Tempo(request)
            : Is(uri, options.GrafanaUrl) && path.EndsWith("/api/v1/query", StringComparison.Ordinal) ? FakeGrafana.Prometheus(request)
            : Is(uri, options.GatewayUrl) ? FakeGateway.Send(request, orders)
            : FakeHttp.Json(HttpStatusCode.OK, """{"fake":true}""");

        response.RequestMessage = request;

        return response;
    }

    /// <summary>Whether <paramref name="uri"/> is on the host a configured URL names; the recorder tells hosts apart the same way.</summary>
    internal static bool Is(Uri uri, string configured) =>
        Uri.TryCreate(configured, UriKind.Absolute, out Uri? baseUri)
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}

internal static class FakeHttp
{
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    /// <summary>The shape <c>UseStatusCodePages</c> gives a bare 401/403/404 on the platform's hosts.</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string title) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"https://tools.ietf.org/html/rfc9110","title":"{{title}}","status":{{(int)status}}}""",
                Encoding.UTF8,
                MediaTypeNames.Application.ProblemJson),
        };
}
