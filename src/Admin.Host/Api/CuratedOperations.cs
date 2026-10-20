using Admin.Host.Config;

namespace Admin.Host.Api;

/// <summary>
/// Operations no OpenAPI document describes (spec §2.3): the Web BFF's quote and its buyer order
/// reads, which have no document (blueprint-backend <c>src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs</c>,
/// <c>OrderEndpoints.cs</c>), every
/// host's readiness check (<c>Common.Web.HealthCheckExtensions</c>), called on the host's own port,
/// the product-image upload and the saved delivery address, which are the BFF's too
/// (<c>ProductImageEndpoints.cs</c>, <c>SavedAddressEndpoints.cs</c>), the stored image read back through
/// <c>image-edge</c> (blueprint-backend <c>deploy/compose/object-store/edge.conf</c>), and the payment simulator's request log, the one view of what Payments asked the provider for
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
        new ApiOperation(
            "bff:ListOrders",
            "bff",
            "ListOrders",
            "GET",
            Join(options.GatewayUrl, "/bff/v1/orders"),
            [],
            [new ApiParameter("cursor", false, "string"), new ApiParameter("limit", false, "integer")],
            null,
            false,
            GatewayRoutes.PolicyFor("GET", "/bff/v1/orders"),
            true),
        new ApiOperation(
            "bff:GetOrder",
            "bff",
            "GetOrder",
            "GET",
            Join(options.GatewayUrl, "/bff/v1/orders/{id}"),
            [new ApiParameter("id", true, "string")],
            [],
            null,
            false,
            GatewayRoutes.PolicyFor("GET", "/bff/v1/orders/{id}"),
            true),
        new ApiOperation(
            "bff:IssueProductImageUpload",
            "bff",
            "IssueProductImageUpload",
            "POST",
            Join(options.GatewayUrl, "/bff/v1/product-images"),
            [],
            [],
            RunLocallyExamples.ProductImageUpload,
            false,
            GatewayRoutes.PolicyFor("POST", "/bff/v1/product-images"),
            true),
        new ApiOperation(
            "bff:GetSavedAddress",
            "bff",
            "GetSavedAddress",
            "GET",
            Join(options.GatewayUrl, "/bff/v1/saved-address"),
            [],
            [],
            null,
            false,
            GatewayRoutes.PolicyFor("GET", "/bff/v1/saved-address"),
            true),
        new ApiOperation(
            "bff:PutSavedAddress",
            "bff",
            "PutSavedAddress",
            "PUT",
            Join(options.GatewayUrl, "/bff/v1/saved-address"),
            [],
            [],
            RunLocallyExamples.SavedAddress,
            false,
            GatewayRoutes.PolicyFor("PUT", "/bff/v1/saved-address"),
            true),
        new ApiOperation(
            "edge:GetProductImage",
            "edge",
            "GetProductImage",
            "GET",
            Join(options.ImageEdgeUrl, "/products/{seller}/{file}"),
            [new ApiParameter("seller", true, "string"), new ApiParameter("file", true, "string")],
            [],
            null,
            false,
            GatewayRoutes.Direct,
            true),
        .. PlatformServices.WithUrl.Select(s => Health(s.Name, s.Url!(options))),
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
        new ApiOperation(
            "carrier:Requests",
            "carrier",
            "Requests",
            "GET",
            Join(options.CarrierSimulatorUrl, "/__admin/requests"),
            [],
            [],
            null,
            false,
            GatewayRoutes.Direct,
            true),

        // The same log, filtered by the simulator itself: the whole of it grows without bound (nearly a megabyte after a day
        // of orders, against the proxy's one-megabyte cap), so the Scenario's carrier step asks for one code's requests.
        new ApiOperation(
            "carrier:FindBookings",
            "carrier",
            "FindBookings",
            "POST",
            Join(options.CarrierSimulatorUrl, "/__admin/requests/find"),
            [],
            [],
            RunLocallyExamples.CarrierBookings,
            false,
            GatewayRoutes.Direct,
            true),
        new ApiOperation(
            "carrier:FindFeedPolls",
            "carrier",
            "FindFeedPolls",
            "POST",
            Join(options.CarrierSimulatorUrl, "/__admin/requests/find"),
            [new ApiParameter("orderId", true, "string")], // not in the URL: the screen fills the body's {orderId} (request-builder.ts)
            [],
            RunLocallyExamples.CarrierFeedPolls,
            false,
            GatewayRoutes.Direct,
            true),
    ];

    private static ApiOperation Health(string host, string baseUrl) =>
        new($"health:{host}", "health", $"{host} ready", "GET", Join(baseUrl, "/health/ready"), [], [], null, false, GatewayRoutes.Direct, true);

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
