using Admin.Host.Config;

namespace Admin.Host.Api;

/// <summary>
/// Operations no OpenAPI document describes (spec §2.3): the Web BFF's quote, which has no
/// document (blueprint-backend <c>src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs</c>), every
/// host's readiness check (<c>Common.Web.HealthCheckExtensions</c>), called on the host's own port,
/// and the payment simulator's request log, the one view of what Payments asked the provider for
/// (blueprint-backend <c>deploy/compose/psp-simulator/README.md</c>).
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
        Health("inventory", options.InventoryUrl),
        Health("payments", options.PaymentsUrl),
        new ApiOperation(
            "simulator:Requests",
            "simulator",
            "Requests",
            "GET",
            Join(options.PaymentSimulatorUrl, "/__admin/requests"),
            [],
            [],
            null,
            false,
            GatewayRoutes.Direct,
            true),
    ];

    private static ApiOperation Health(string host, string baseUrl) =>
        new($"health:{host}", "health", $"{host} ready", "GET", Join(baseUrl, "/health/ready"), [], [], null, false, GatewayRoutes.Direct, true);

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
