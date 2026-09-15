using Admin.Host.Config;

namespace Admin.Host.Api;

/// <summary>
/// Operations no OpenAPI document describes (spec §2.3): the Web BFF's quote, which has no
/// document (blueprint-backend <c>src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs</c>), and every
/// host's readiness check (<c>Common.Web.HealthCheckExtensions</c>), called on the host's own port.
/// </summary>
public static class CuratedOperations
{
    public static IReadOnlyList<ApiOperation> All(AdminOptions options) =>
    [
        new ApiOperation(
            "bff:Quote",
            "bff",
            "Quote",
            "POST",
            Join(options.GatewayUrl, "/bff/v1/checkout/quote"),
            [],
            [],
            RunLocallyExamples.Quote,
            false,
            GatewayRoutes.PolicyFor("POST", "/bff/v1/checkout/quote"),
            true),
        Health("gateway", options.GatewayUrl),
        Health("catalog", options.CatalogUrl),
        Health("ordering", options.OrderingUrl),
        Health("bff", options.BffUrl),
    ];

    private static ApiOperation Health(string host, string baseUrl) =>
        new($"health:{host}", "health", $"{host} ready", "GET", Join(baseUrl, "/health/ready"), [], [], null, false, GatewayRoutes.Direct, true);

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
