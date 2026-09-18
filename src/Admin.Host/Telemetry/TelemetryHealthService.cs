namespace Admin.Host.Telemetry;

/// <summary>
/// One service's golden signals, as the dashboard's "Rate, errors, duration" row shows them. Each
/// value is null where Prometheus returned no series for the service, or a value that is not a
/// finite number.
/// </summary>
public sealed record ServiceSignals(string Service, double? RequestRate, double? ErrorRatio, double? LatencyP99Seconds);

/// <summary>The Stack screen's health strip (spec §5.8). Unreachable is a state, not an exception (spec §9).</summary>
public sealed record TelemetryHealthView(bool Reachable, string? Error, IReadOnlyList<ServiceSignals> Services);

/// <summary>
/// Runs the three <see cref="GoldenSignals"/> queries and joins them on <c>service_name</c>. A
/// service with requests and no 5xx has no error-ratio series at all, because the division has no
/// numerator to match; that is shown as absent rather than as zero, which Prometheus did not say.
/// </summary>
public sealed class TelemetryHealthService(GrafanaClient grafana)
{
    public async Task<TelemetryHealthView> ReadAsync(CancellationToken cancellationToken)
    {
        PrometheusResult[] results = await Task.WhenAll(
            grafana.InstantQueryAsync(GoldenSignals.RequestRate, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.ErrorRatio, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.LatencyP99, cancellationToken));

        if (results.FirstOrDefault(r => !r.Reachable) is { } failed)
        {
            return new TelemetryHealthView(false, failed.Error, []);
        }

        PrometheusResult rate = results[0];
        PrometheusResult errors = results[1];
        PrometheusResult latency = results[2];

        ServiceSignals[] services =
        [
            .. results
                .SelectMany(r => r.Samples)
                .Select(s => s.Service)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(service => new ServiceSignals(
                    service,
                    ValueFor(rate, service),
                    ValueFor(errors, service),
                    ValueFor(latency, service))),
        ];

        return new TelemetryHealthView(true, null, services);
    }

    private static double? ValueFor(PrometheusResult result, string service) =>
        result.Samples.FirstOrDefault(s => s.Service == service)?.Value;
}
