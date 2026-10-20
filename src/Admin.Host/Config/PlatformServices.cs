namespace Admin.Host.Config;

/// <summary>
/// One service of the platform, with every part the console hangs from it. <see cref="Url"/> is null for a worker
/// with no API of its own; <see cref="GatewayPrefix"/> is the row of <c>GatewayRoutes.Routes</c> that reaches it, null
/// for a service the gateway does not front; <see cref="BackendNamespace"/> is the root namespace its queues'
/// backend constants live under (<c>PlatformQueues.Table</c>); <see cref="SpanService"/> is the <c>service.name</c>
/// its spans carry, set only for the one the span table tells apart by it.
/// </summary>
public sealed record PlatformService(
    string Name,
    string ComposeService,
    Func<AdminOptions, string>? Url,
    bool OpenApi,
    string? GatewayPrefix,
    string BackendNamespace,
    string? SpanService = null);

/// <summary>
/// The services of the platform, in the order the screens list them. The proxy's allow-list, the readiness probe,
/// the health operations, the API catalogue, the fakes' OpenAPI documents and recordings, the Compose, gateway-route
/// and queue drift checks and the span table's service name all read this list; the fakes' workstation recordings and the Scenario lease's Compose names still name services by hand
/// (tests/Admin.Host.Tests/Drift/PlatformServiceDriftTests.cs fails a record missing a part). A service added to the
/// console is one record here plus its fixtures. The URL it selects is still an <see cref="AdminOptions"/> property,
/// because that is what <c>Admin:*</c> configuration binds.
/// </summary>
public static class PlatformServices
{
    public static readonly PlatformService Shipping = new(
        "shipping",
        "shipping-worker",
        null,
        false,
        null,
        "Shipping",
        SpanService: "Shipping.Worker");

    public static readonly PlatformService[] All =
    [
        new("gateway", "gateway", o => o.GatewayUrl, false, null, "Gateway"),
        new("catalog", "catalog-api", o => o.CatalogUrl, true, "/api/v1/catalog/", "Catalog"),
        new("ordering", "ordering-api", o => o.OrderingUrl, true, "/api/v1/orders/", "Ordering"),
        new("bff", "web-bff", o => o.BffUrl, false, "/bff/v1/", "Web.Bff"),
        new("inventory", "inventory-api", o => o.InventoryUrl, true, "/api/v1/inventory/", "Inventory"),
        new("payments", "payments-api", o => o.PaymentsUrl, true, "/api/v1/payments/", "Payments"),
        new("privacy", "privacy-api", o => o.PrivacyUrl, true, "/api/v1/privacy/", "Privacy"),
        Shipping,
        new("notifications", "notifications-worker", null, false, null, "Notifications"),
    ];

    /// <summary>The services with an HTTP surface of their own, which the probe, the health operations and the proxy reach.</summary>
    public static IEnumerable<PlatformService> WithUrl => All.Where(s => s.Url is not null);

    /// <summary>The services whose OpenAPI document the API catalogue reads.</summary>
    public static IEnumerable<PlatformService> WithOpenApi => All.Where(s => s.OpenApi);
}
