namespace Admin.Host.Security;

/// <summary>
/// Forbids any page, on any site, from framing the console. <see cref="LoopbackOriginGuard"/> judges a request by the
/// site that made it, but a page that frames the console makes no cross-site request of its own: the SPA's calls from inside
/// the frame are same-origin, so a decoy over the operator's click would reach a one-click action (spec §8). The headers go
/// on every response the application produces, the static files, the SPA fallback and an error included, because a frame
/// loads whichever it is given. Host filtering answers a foreign Host name with an empty 400 before this runs; that is no
/// page to put a decoy over.
/// <c>X-Content-Type-Options</c> rides along so a served file is only what its type says.
/// The SPA fallback is a short-circuited endpoint, which runs inside routing and so before this middleware: it sets the same
/// headers itself, through <see cref="Apply"/> from its <c>OnPrepareResponse</c>.
/// </summary>
public static class FramingProtection
{
    public static IApplicationBuilder UseFramingProtection(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                Apply(context.Response);

                return Task.CompletedTask;
            });

            return next(context);
        });

    public static void Apply(HttpResponse response)
    {
        IHeaderDictionary headers = response.Headers;
        headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
    }
}
