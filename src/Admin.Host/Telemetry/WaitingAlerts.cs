namespace Admin.Host.Telemetry;

/// <summary>A rule written out and not loaded, and the instrument whose absence is why it cannot fire.</summary>
/// <param name="Name">The rule's <c>alert:</c> name in <c>awaiting-signal.yaml</c>.</param>
/// <param name="Instruments">The Prometheus metric names its expression reads that nothing publishes yet.</param>
public sealed record WaitingAlert(string Name, IReadOnlyList<string> Instruments);

/// <summary>
/// The rules in blueprint-backend's <c>deploy/observability/alerts/awaiting-signal.yaml</c> (spec §5.8), copied with
/// the metrics each one reads. The backend does not load that file anywhere, so no Grafana can be asked about
/// them, and they are shown as waiting, never as quiet: a rule that cannot fire says nothing about health. A rule
/// moved into <c>platform-alerts.yaml</c> once its instrument ships leaves this table; the drift gate checks the
/// names both ways and each metric against the expression it is read from.
/// </summary>
public static class WaitingAlerts
{
    public static readonly WaitingAlert[] Table =
    [
        new("StuckSaga", ["ordering_saga_oldest_unfinalised_age_seconds"]),
        new("OrdersAwaitingReview", ["ordering_order_reviews_oldest_age_seconds"]),
        new("CacheHitRatioCollapse", ["hybrid_cache_hits_total", "hybrid_cache_misses_total"]),
    ];
}
