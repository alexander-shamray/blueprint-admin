using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The gate's own coverage, and it needs no clone: its subject is what the gate is looking at, not
/// what it found. Every source file that cites blueprint-backend is either a copied fact with a drift
/// check here or is listed as citing without copying, with the reason. A new citation with neither
/// fails the build, which is how a copied fact stops arriving unchecked.
/// </summary>
public sealed class DriftCoverageTests
{
    private const string Citation = "blueprint-backend";

    /// <summary>Each file that copies a backend fact, and the drift check that reads it there.</summary>
    private static readonly Dictionary<string, Type> Checked = new(StringComparer.Ordinal)
    {
        ["src/Admin.Host/Api/CorrelationId.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Api/CuratedOperations.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Fakes/FakeGateway.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Api/GatewayRoutes.cs"] = typeof(GatewayDriftTests),
        ["src/Admin.Host/Broker/BrokerService.cs"] = typeof(ComposeDriftTests),
        ["src/Admin.Host/Broker/PlatformQueues.cs"] = typeof(QueueDriftTests),
        ["src/Admin.Host/Config/AdminOptions.cs"] = typeof(ComposeDriftTests),
        ["src/Admin.Host/Fakes/FakeKeycloak.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Identity/IdentityEndpoints.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Identity/RealmUser.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Stack/WorkstationDoctor.cs"] = typeof(ComposeDriftTests),
        ["src/Admin.Host/Telemetry/GoldenSignals.cs"] = typeof(GoldenSignalsDriftTests),
        ["src/Admin.Host/Telemetry/GrafanaContracts.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Trace/EventTraceService.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Host/Trace/SpanRecogniser.cs"] = typeof(LiteralDriftTests),
        ["src/Admin.Web/src/app/features/logs/logs-page.ts"] = typeof(ComposeDriftTests),
    };

    /// <summary>Files that name the backend without copying anything from it.</summary>
    private static readonly Dictionary<string, string> CitesWithoutCopying = new(StringComparer.Ordinal)
    {
        ["src/Admin.Web/src/app/features/scenario/scenario-page.ts"] = "says a product-specific signal would be a backend change",
    };

    [Fact]
    public void Every_file_citing_the_backend_has_a_drift_check_or_a_reason_it_needs_none()
    {
        Citing().Where(f => !Checked.ContainsKey(f) && !CitesWithoutCopying.ContainsKey(f)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_listed_file_exists_and_still_cites_the_backend()
    {
        HashSet<string> citing = [.. Citing()];

        Checked.Keys.Concat(CitesWithoutCopying.Keys).Where(f => !citing.Contains(f)).ShouldBeEmpty();
    }

    [Fact]
    public void The_scan_reaches_both_products()
    {
        string[] citing = [.. Citing()];

        citing.ShouldContain(f => f.StartsWith("src/Admin.Host/", StringComparison.Ordinal));
        citing.ShouldContain(f => f.StartsWith("src/Admin.Web/src/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Host C# and SPA TypeScript under the two source roots, specs excluded: a fixture path in a
    /// spec is test data, not a copy. Build output is outside both roots by Directory.Build.props.
    /// </summary>
    private static IEnumerable<string> Citing()
    {
        (string Root, string Pattern)[] scanned = [("src/Admin.Host", "*.cs"), ("src/Admin.Web/src", "*.ts")];

        foreach ((string root, string pattern) in scanned)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Backend.AdminRoot, root), pattern, SearchOption.AllDirectories))
            {
                if (file.EndsWith(".spec.ts", StringComparison.Ordinal) || !File.ReadAllText(file).Contains(Citation, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return Path.GetRelativePath(Backend.AdminRoot, file).Replace('\\', '/');
            }
        }
    }
}
