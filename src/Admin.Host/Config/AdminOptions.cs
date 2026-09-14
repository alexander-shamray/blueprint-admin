namespace Admin.Host.Config;

/// <summary>
/// Everything the console needs to know about the workstation, bound from the
/// <c>Admin</c> section of appsettings, from <c>BLUEPRINT_Admin__Key</c>
/// environment variables and from <c>--Admin:Key=value</c> on the command line.
/// Relative directories resolve against the working directory the host was
/// started from, so the README says to start it from the repository root.
/// </summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string BackendDir { get; set; } = "../blueprint-backend";

    public string FrontendDir { get; set; } = "../blueprint-frontend";

    public string ComposeFile { get; set; } = "deploy/compose/docker-compose.yml";

    public int Port { get; set; } = 5300;

    public string GatewayUrl { get; set; } = "http://localhost:5000";

    public string CatalogUrl { get; set; } = "http://localhost:5102";

    public string OrderingUrl { get; set; } = "http://localhost:5101";

    public string BffUrl { get; set; } = "http://localhost:5200";

    public string KeycloakUrl { get; set; } = "http://localhost:8080";

    public string GrafanaUrl { get; set; } = "http://localhost:3000";

    public string ClientUrl { get; set; } = "http://localhost:5173";

    public string Realm { get; set; } = "commerce";

    public string ClientId { get; set; } = "web-app";

    /// <summary>
    /// Swap every child process and every outbound HTTP call for recorded
    /// fakes, so the console runs with no Docker and no clones. This is how
    /// the SPA is developed and how CI runs the Playwright smoke.
    /// </summary>
    public bool FakePlatform { get; set; }
}
