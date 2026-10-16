namespace Admin.Host.Telemetry;

/// <summary>
/// The Stack screen's alert strip (spec §5.8). <paramref name="Loaded"/> is what Prometheus reports and is only
/// meaningful where <paramref name="Reachable"/>; <paramref name="Waiting"/> is always the backend's list of rules
/// that cannot fire, because it does not depend on any Grafana answering.
/// </summary>
public sealed record AlertStripView(bool Reachable, string? Error, IReadOnlyList<AlertRule> Loaded, IReadOnlyList<WaitingAlert> Waiting);

/// <summary>
/// Reads the loaded rules once and sets the waiting ones beside them. The two lists never mix: a waiting rule is
/// not reported as inactive, because a rule nothing evaluates is not a healthy one. Firing rules sort first, then
/// pending, then the quiet, each by name.
/// </summary>
public sealed class AlertStripService(GrafanaClient grafana)
{
    public async Task<AlertStripView> ReadAsync(CancellationToken cancellationToken)
    {
        RulesResult rules = await grafana.RulesAsync(cancellationToken);

        return new AlertStripView(
            rules.Reachable,
            rules.Error,
            [.. rules.Rules.OrderBy(r => Rank(r.State)).ThenBy(r => r.Name, StringComparer.Ordinal)],
            WaitingAlerts.Table);
    }

    private static int Rank(string state) => state switch
    {
        "firing" => 0,
        "pending" => 1,
        _ => 2,
    };
}
