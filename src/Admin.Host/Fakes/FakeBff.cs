using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// The BFF itself, for a request sent to <see cref="AdminOptions.BffUrl"/> rather than through the gateway. Recorded from
/// the live service on 2026-10-18 (run-locally.md, "Call the BFF directly (no gateway)"; #136): the BFF validates the
/// bearer token itself, so a request with none is a 401 carrying <c>WWW-Authenticate: Bearer</c>; a path it does not
/// serve is a 404 problem; and an order read answers the body the gateway's <c>/bff/v1</c> route forwards byte for byte.
/// The gateway's <c>web-bff</c> route only strips <c>/bff</c> (<see cref="Api.GatewayRoutes"/>), so each <c>/v1</c> path
/// is answered as <see cref="FakeGateway"/> answers its <c>/bff</c> twin. Every answer carries the BFF's own
/// <c>X-Correlation-Id</c>, the caller's when it sent one, and every problem body a <c>correlationId</c> of that value.
/// Left out of the recording: a problem's <c>instance</c> and <c>traceId</c> members and its RFC 9110 section anchor,
/// which the fakes' shared problem shape omits on every host, and the gateway path's <c>Vary</c> header, which the
/// direct one did not carry anyway.
/// </summary>
internal static class FakeBff
{
    private const string BffPrefix = "/bff";

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, FakeOrders orders, AdminOptions options, CancellationToken cancellationToken)
    {
        string correlationId = request.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? sent) && sent.FirstOrDefault() is { Length: > 0 } id
            ? id
            : Guid.NewGuid().ToString("N");

        // The BFF maps its liveness and startup probes anonymous, as it does readiness, which every fake host already answers.
        HttpResponseMessage response = request.RequestUri!.AbsolutePath is "/health/live" or "/health/startup"
            ? FakeHttp.Json(HttpStatusCode.OK, """{"status":"Healthy"}""")
            : FakeGateway.Permissions(request) is null
            ? Unauthorized()
            : request.RequestUri!.AbsolutePath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                ? AsGatewayTwin(request, orders, options)
                : FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found");

        response.Headers.Remove("X-Correlation-Id");
        response.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

        return await WithCorrelationId(response, correlationId, cancellationToken);
    }

    private static HttpResponseMessage Unauthorized()
    {
        HttpResponseMessage response = FakeHttp.Problem(HttpStatusCode.Unauthorized, "Unauthorized");
        response.Headers.WwwAuthenticate.ParseAdd("Bearer");

        return response;
    }

    /// <summary>The gateway's answer for the same path under <c>/bff</c>; the request is put back as it came.</summary>
    private static HttpResponseMessage AsGatewayTwin(HttpRequestMessage request, FakeOrders orders, AdminOptions options)
    {
        Uri direct = request.RequestUri!;
        request.RequestUri = new UriBuilder(direct) { Path = BffPrefix + direct.AbsolutePath }.Uri;

        try
        {
            return FakeGateway.Send(request, orders, options);
        }
        finally
        {
            request.RequestUri = direct;
        }
    }

    private static async Task<HttpResponseMessage> WithCorrelationId(HttpResponseMessage response, string correlationId, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != MediaTypeNames.Application.ProblemJson
            || JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) is not JsonObject problem)
        {
            return response;
        }

        problem["correlationId"] = correlationId;
        response.Content = new StringContent(problem.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.ProblemJson);

        return response;
    }
}
