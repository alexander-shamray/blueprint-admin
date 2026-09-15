namespace Admin.Host.Api;

/// <summary>
/// The gateway's edge policy per route. Owner: blueprint-backend
/// <c>src/Gateway/Gateway.Api/appsettings.json</c>, <c>ReverseProxy:Routes</c> (catalog-public,
/// catalog-write, ordering, inventory-admin, web-bff). Update this table when a route changes there.
/// </summary>
public static class GatewayRoutes
{
    public const string NoRoute = "no gateway route";

    /// <summary>A call straight to a host's own port, such as <c>/health/ready</c>, which is anonymous on every host.</summary>
    public const string Direct = "direct, anonymous";

    // Method null is "any method". Prefixes end in '/' so /api/v1/catalogue does not match /api/v1/catalog.
    private static readonly (string? Method, string Prefix, string Policy)[] Routes =
    [
        ("GET", "/api/v1/catalog/", "anonymous"),
        ("POST", "/api/v1/catalog/", "authenticated"),
        (null, "/api/v1/orders/", "authenticated"),
        (null, "/api/v1/inventory/", "inventory:admin"),
        (null, "/bff/", "authenticated"),
    ];

    public static string PolicyFor(string method, string gatewayPath)
    {
        string path = gatewayPath.EndsWith('/') ? gatewayPath : gatewayPath + "/";

        foreach ((string? routeMethod, string prefix, string policy) in Routes)
        {
            if ((routeMethod is null || routeMethod == method) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return policy;
            }
        }

        return NoRoute;
    }
}
