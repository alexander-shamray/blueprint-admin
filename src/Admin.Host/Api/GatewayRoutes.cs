namespace Admin.Host.Api;

/// <summary>
/// The gateway's edge policy per route. Owner: blueprint-backend
/// <c>src/Gateway/Gateway.Api/appsettings.json</c>, <c>ReverseProxy:Routes</c>. The drift gate reads
/// that file against this table, both ways (tests/Admin.Host.Tests/Drift/GatewayDriftTests.cs).
/// </summary>
public static class GatewayRoutes
{
    public const string NoRoute = "no gateway route";

    /// <summary>A call straight to a host's own port, such as <c>/health/ready</c>, which is anonymous on every host.</summary>
    public const string Direct = "direct, anonymous";

    // Method null is "any method". A prefix ends in '/' (a catch-all route) so /api/v1/catalogue does not match
    // /api/v1/catalog; a path with no trailing '/' is a route with no catch-all and matches only itself. The first
    // row that matches wins, so an exact path sits above the prefix it would otherwise fall under.
    internal static readonly (string? Method, string Prefix, string Policy)[] Routes =
    [
        ("GET", "/api/v1/catalog/products/mine", "authenticated"),
        ("GET", "/api/v1/catalog/", "anonymous"),
        ("POST", "/api/v1/catalog/", "authenticated"),
        ("PUT", "/api/v1/catalog/", "authenticated"),
        (null, "/api/v1/orders/", "authenticated"),
        (null, "/api/v1/inventory/", "inventory:admin"),
        (null, "/api/v1/payments/", "payments:admin"),
        (null, "/api/v1/privacy/", "privacy:erase"),
        (null, "/bff/v1/", "authenticated"),
    ];

    /// <summary>The row a gateway route copies: the path up to its catch-all, or the whole path when it has none.</summary>
    internal static string PrefixOf(string routePath)
    {
        int catchAll = routePath.IndexOf('{', StringComparison.Ordinal);

        return catchAll < 0 ? routePath : routePath[..catchAll];
    }

    public static string PolicyFor(string method, string gatewayPath)
    {
        string path = gatewayPath.EndsWith('/') ? gatewayPath : gatewayPath + "/";

        foreach ((string? routeMethod, string prefix, string policy) in Routes)
        {
            bool exact = !prefix.EndsWith('/');
            bool matches = exact
                ? string.Equals(path, prefix + "/", StringComparison.OrdinalIgnoreCase)
                : path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            if ((routeMethod is null || routeMethod == method) && matches)
            {
                return policy;
            }
        }

        return NoRoute;
    }
}
