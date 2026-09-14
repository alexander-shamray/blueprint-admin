using Microsoft.Extensions.Options;

namespace Admin.Host.Config;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (IOptions<AdminOptions> options, RepoPaths paths) =>
        {
            AdminOptions o = options.Value;

            return TypedResults.Ok(new ConfigView(
                paths.BackendDir,
                paths.FrontendDir,
                paths.ComposeFile,
                o.FakePlatform,
                new UrlsView(o.GatewayUrl, o.CatalogUrl, o.OrderingUrl, o.BffUrl, o.KeycloakUrl, o.GrafanaUrl, o.ClientUrl)));
        });

        return app;
    }
}

public sealed record ConfigView(string BackendDir, string FrontendDir, string ComposeFile, bool FakePlatform, UrlsView Urls);

public sealed record UrlsView(string Gateway, string Catalog, string Ordering, string Bff, string Keycloak, string Grafana, string Client);
