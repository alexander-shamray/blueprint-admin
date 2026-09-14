namespace Admin.Host.Security;

/// <summary>
/// Refuses <c>/api</c> requests that a page on another site made the browser send.
/// Binding loopback and setting no CORS headers (spec §8) only stops such a page
/// reading responses: a plain <c>&lt;form method=post&gt;</c> is a simple request
/// and still reaches a body-less endpoint such as <c>POST /api/stack/backend/up</c>.
/// So a request whose <c>Origin</c> names anything but a loopback host, or whose
/// <c>Sec-Fetch-Site</c> is <c>cross-site</c>, is a 403 before any endpoint runs.
/// A request with neither header (curl, tests) passes, and so does the dev server
/// on 5301, which proxies with a loopback Origin. DNS rebinding is the other half
/// and is closed by host filtering, pinned to <see cref="LoopbackHosts"/> in Program.cs.
/// </summary>
public static class LoopbackOriginGuard
{
    public static readonly IReadOnlyList<string> LoopbackHosts = ["127.0.0.1", "localhost", "[::1]"];

    public static IApplicationBuilder UseLoopbackOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && IsCrossSite(context.Request))
            {
                await TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Cross-site request refused",
                    detail: "The console accepts requests from loopback origins only.").ExecuteAsync(context);

                return;
            }

            await next(context);
        });

    private static bool IsCrossSite(HttpRequest request)
    {
        if (string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string origin = request.Headers.Origin.ToString();

        return origin.Length > 0 && !IsLoopbackOrigin(origin);
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
