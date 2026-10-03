using Admin.Host.Identity;

namespace Admin.Host.Config;

/// <summary>
/// Everything the console needs to know about the workstation, bound from the
/// <c>Admin</c> section of appsettings, from <c>BLUEPRINT_Admin__Key</c>
/// environment variables and from <c>--Admin:Key=value</c> on the command line.
/// Relative <see cref="BackendDir"/>/<see cref="FrontendDir"/> resolve against
/// the repository root, located by walking up from the executable to
/// <c>BlueprintAdmin.slnx</c> (see <c>Config/RepoRoot.cs</c>), independent of
/// the process's working directory — or, when that root is a linked git
/// worktree, against the main checkout it belongs to.
/// </summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    public string BackendDir { get; set; } = "../blueprint-backend";

    public string FrontendDir { get; set; } = "../blueprint-frontend";

    public string ComposeFile { get; set; } = "deploy/compose/docker-compose.yml";

    /// <summary>
    /// What the console keeps between runs: each service's accepted OpenAPI document. Relative, it resolves
    /// against the main checkout, as the clones do, so worktrees share it. The default sits under
    /// <c>/artifacts/</c>, which .gitignore already excludes. FakePlatform ignores it and keeps a per-process
    /// store of its own (<c>FakeOpenApi.Baselines</c>), so a fake document never becomes a real service's baseline.
    /// </summary>
    public string DataDir { get; set; } = "artifacts/admin";

    public int Port { get; set; } = 5300;

    public string GatewayUrl { get; set; } = "http://localhost:5000";

    public string CatalogUrl { get; set; } = "http://localhost:5102";

    public string OrderingUrl { get; set; } = "http://localhost:5101";

    public string BffUrl { get; set; } = "http://localhost:5200";

    public string InventoryUrl { get; set; } = "http://localhost:5103";

    public string PaymentsUrl { get; set; } = "http://localhost:5104";

    /// <summary>
    /// The payment provider simulator Payments authorises against. Its request log is the only by-hand view of
    /// what Payments asked for, and a decline is caused through it by the order's amount (run-locally.md).
    /// </summary>
    public string PaymentSimulatorUrl { get; set; } = "http://localhost:5190";

    public string KeycloakUrl { get; set; } = "http://localhost:8080";

    public string GrafanaUrl { get; set; } = "http://localhost:3000";

    public string ClientUrl { get; set; } = "http://localhost:5173";

    public string Realm { get; set; } = "commerce";

    public string ClientId { get; set; } = "web-app";

    /// <summary>The realm users offered by the identity picker; empty means demo/demo and browser/browser (see <c>Identity/RealmUser.cs</c>).</summary>
    public List<RealmUser> Users { get; set; } = [];

    /// <summary>
    /// Where the built SPA lives, resolved against the repository root (not
    /// the process's working directory or content root, which are wrong when
    /// Playwright starts the host from <c>src/Admin.Web</c>). Angular's
    /// <c>outputPath</c> writes there; when the directory does not exist yet
    /// (no build has run), static files and the SPA fallback are skipped and
    /// the API is unaffected.
    /// </summary>
    public string WebRoot { get; set; } = "src/Admin.Host/wwwroot";

    /// <summary>
    /// Swap every child process and every outbound HTTP call for recorded
    /// fakes, so the console runs with no Docker and no clones. This is how
    /// the SPA is developed and how CI runs the Playwright smoke.
    /// </summary>
    public bool FakePlatform { get; set; }
}
