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
    /// The Privacy service (<c>privacy-api</c> in blueprint-backend's <c>deploy/compose/services/privacy.yml</c>), which raises an
    /// erasure request and asks every personal-data holder to answer it. Reached through the gateway's <c>/api/v1/privacy/</c>
    /// route; this URL serves its readiness probe and its OpenAPI document.
    /// </summary>
    public string PrivacyUrl { get; set; } = "http://localhost:5105";

    /// <summary>
    /// The payment provider simulator Payments authorises against. Its request log is the only by-hand view of
    /// what Payments asked for, and a decline is caused through it by the order's amount (run-locally.md).
    /// </summary>
    public string PaymentSimulatorUrl { get; set; } = "http://localhost:5190";

    /// <summary>
    /// The carrier simulator Shipping despatches through. Shipping is a worker with no API of its own, so this
    /// request log is the only by-hand view of what Shipping asked a carrier for (run-locally.md).
    /// </summary>
    public string CarrierSimulatorUrl { get; set; } = "http://localhost:5191";

    /// <summary>
    /// Where blueprint-backend's <c>docs/runbooks/</c> are read: the Broker screen links a parked queue to the runbook
    /// that owns it (<c>Broker/ParkedQueues.cs</c>). A link the browser follows, not a request the host makes.
    /// </summary>
    public string BackendRunbooksUrl { get; set; } = "https://github.com/alexander-shamray/blueprint-backend/blob/main/docs/runbooks";

    /// <summary>
    /// The <c>sa</c> password the Compose model defaults (<c>SQL_PASSWORD</c> in blueprint-backend's
    /// <c>deploy/compose/infrastructure.yml</c>), used for the connections the BFF projection rebuild hands
    /// <c>tools/bff-replay</c> (run-locally.md). A development default of the local stack, the stated local
    /// exception to holding no credential beside the realm passwords; a stack started with another
    /// <c>SQL_PASSWORD</c> sets this to match.
    /// </summary>
    public string SqlPassword { get; set; } = "Local_Dev_Pa55w0rd!";

    /// <summary>The BFF's own broker account, as <c>deploy/compose/services/web-bff.yml</c> gives it, reached on the published port by its literal loopback address.</summary>
    public string BffBrokerConnection { get; set; } = "amqp://bff-svc:local-dev-bff@127.0.0.1:5672";

    /// <summary>
    /// The S3 endpoint of the local object store (SeaweedFS, <c>object-store</c> in blueprint-backend's
    /// <c>deploy/compose/infrastructure.yml</c>), which a pre-signed product-image <c>PUT</c> addresses. The console probes it and
    /// sends the one upload run-locally.md makes by hand; it reads nothing else from it (ADR-106).
    /// </summary>
    public string ObjectStoreUrl { get; set; } = "http://127.0.0.1:8333";

    /// <summary>
    /// The public layer in front of the store (<c>image-edge</c>, nginx), which serves <c>products/</c> to a read alone and
    /// sets <c>nosniff</c> (§10.8). A product's <c>thumbnailUrl</c> points here, so the API screen and the Scenario read the
    /// stored image back through it, as a buyer's browser does.
    /// </summary>
    public string ImageEdgeUrl { get; set; } = "http://127.0.0.1:9000";

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

    /// <summary>
    /// Write the upstream answers <c>Fakes/FixtureRecordings.cs</c> names into <see cref="RecordDir"/> as the real
    /// platform gives them, scrubbed first. A developer's act
    /// against a loopback stack; refused together with <see cref="FakePlatform"/>, whose answers are the fixtures.
    /// </summary>
    public bool Record { get; set; }

    /// <summary>Where <see cref="Record"/> writes, resolved against the repository root as <see cref="WebRoot"/> is.</summary>
    public string RecordDir { get; set; } = "src/Admin.Host/Fakes/fixtures";
}
