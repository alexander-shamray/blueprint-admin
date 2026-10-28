using System.Net;
using System.Net.Mime;
using System.Text;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// Every outbound HTTP call in FakePlatform mode. Readiness and Grafana's health answer 200 on any
/// host; the realm token endpoint, the four OpenAPI documents, the gateway, the payment simulator's
/// request log, the mail sink and Grafana's datasource proxies are recordings (FakeKeycloak, FakeOpenApi, FakeGateway,
/// FakeMailSink, FakeGrafana), and the BFF's order read is <see cref="FakeOrders"/>, through the gateway or, sent to the BFF's own
/// port, through <see cref="FakeBff"/>. Hosts are told apart by the configured URLs. While the fake stack is on the stress
/// override, the URL of the service it takes the host port from (<see cref="FakePlatformScripts.UnpublishedOnOverride"/>)
/// refuses the connection, as nothing listens there on that profile.
/// </summary>
public sealed class FakePlatformHandler(AdminOptions options, FakeOrders orders) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string path = uri.AbsolutePath;

        if (orders.Stack.StressOverride
            && PlatformServices.WithUrl.Any(s => s.ComposeService == FakePlatformScripts.UnpublishedOnOverride && Is(uri, s.Url!(options))))
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.");
        }

        HttpResponseMessage response =
            path == "/health/ready" ? FakeHttp.Json(HttpStatusCode.OK, """{"status":"Healthy"}""")
            : path == "/api/health" ? FakeHttp.Json(HttpStatusCode.OK, """{"database":"ok","version":"fake"}""")
            : Is(uri, options.ObjectStoreUrl) ? await FakeObjectStore.SendAsync(request, orders, cancellationToken)
            : Is(uri, options.ImageEdgeUrl) ? FakeObjectStore.Edge(request, orders)
            : Is(uri, options.KeycloakUrl) && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) ? await FakeKeycloak.TokenAsync(request, cancellationToken)
            : path == "/openapi/v1.json" && PlatformServices.WithOpenApi.FirstOrDefault(s => Is(uri, s.Url!(options))) is { } documented
                ? FakeOpenApi.Document(request, documented.Name)
            // WireMock.Net's request log, as the simulator answered it with nothing yet asked of it.
            : Is(uri, options.PaymentSimulatorUrl) && path == "/__admin/requests" ? FakeHttp.Json(HttpStatusCode.OK, "[]")
            // The carrier simulator is the same WireMock.Net image, answering the same way before Shipping despatches.
            : Is(uri, options.CarrierSimulatorUrl) && path == "/__admin/requests" ? FakeHttp.Json(HttpStatusCode.OK, "[]")
            : Is(uri, options.CarrierSimulatorUrl) && path == "/__admin/requests/find" ? FakeCarrier.Find(request, orders)
            : Is(uri, options.MailSinkUrl) ? FakeMailSink.Send(request, orders)
            : Is(uri, options.GrafanaUrl) && path == "/api/datasources" ? FakeGrafana.Datasources()
            : Is(uri, options.GrafanaUrl) && path.Contains("/loki/api/v1/query_range", StringComparison.Ordinal) ? FakeGrafana.Loki(request, orders)
            : Is(uri, options.GrafanaUrl) && path.Contains("/api/traces/", StringComparison.Ordinal) ? FakeGrafana.Tempo(request)
            : Is(uri, options.GrafanaUrl) && path.EndsWith("/api/v1/query", StringComparison.Ordinal) ? FakeGrafana.Prometheus(request, orders.Stack)
            : Is(uri, options.GrafanaUrl) && path.EndsWith("/api/v1/rules", StringComparison.Ordinal) ? FakeGrafana.Rules(orders.Stack)
            : Is(uri, options.GatewayUrl) ? FakeGateway.Send(request, orders, options)
            : Is(uri, options.BffUrl) ? await FakeBff.SendAsync(request, orders, options, cancellationToken)
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

    /// <summary>
    /// A problem body recorded or copied whole from the platform, typed as the platform's services type every problem
    /// (Common.Web's problem-details defaults), so a client that reads the type sees what the stack sends.
    /// </summary>
    public static HttpResponseMessage RecordedProblem(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.ProblemJson) };

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
