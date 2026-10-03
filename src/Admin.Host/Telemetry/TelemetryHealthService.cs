namespace Admin.Host.Telemetry;

/// <summary>
/// One service's golden signals, a value per query panel of the backend's golden-signals dashboard. Each
/// value is null where it is unknown: no series for the service, a value that is not a finite number, or
/// a 5xx share over no requests.
/// </summary>
public sealed record ServiceSignals(
    string Service,
    double? RequestRate,
    double? ErrorRatio,
    double? LatencyP99Seconds,
    double? DomainRefusalRate,
    double? UnauthorisedRate,
    double? CommandP95Seconds,
    double? QueryP95Seconds);

/// <summary>The Stack screen's health strip (spec §5.8). Unreachable is a state, not an exception (spec §9).</summary>
public sealed record TelemetryHealthView(bool Reachable, string? Error, IReadOnlyList<ServiceSignals> Services);

/// <summary>
/// Runs the <see cref="GoldenSignals"/> queries and joins them on <c>service_name</c>. The query text is
/// the dashboard's, so the join owns what the dashboard leaves to the eye: a status code with no series is
/// a zero rate for a service the request rate names, the 5xx share is zero for a service with traffic
/// (the division has no numerator to match, so it has no series at all), and the 422 panel's routes are
/// summed, because rates add. One failed query makes the whole strip unreachable, never a partial one
/// whose empty cells would look like "no series".
/// </summary>
public sealed class TelemetryHealthService(GrafanaClient grafana)
{
    public async Task<TelemetryHealthView> ReadAsync(CancellationToken cancellationToken)
    {
        // The first query runs alone: it resolves Prometheus's uid, which GrafanaClient caches once
        // resolved but does not gate while resolving, so the six fanned out after it read the cache
        // rather than each asking /api/datasources. A Grafana with no Prometheus stops here, asked once.
        PrometheusResult first = await grafana.InstantQueryAsync(GoldenSignals.RequestRate, cancellationToken);

        if (!first.Reachable)
        {
            return new TelemetryHealthView(false, first.Error, []);
        }

        PrometheusResult[] rest = await Task.WhenAll(
            grafana.InstantQueryAsync(GoldenSignals.ErrorRatio, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.LatencyP99, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.DomainRefusalRate, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.UnauthorisedRate, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.CommandP95, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.QueryP95, cancellationToken));

        PrometheusResult[] results = [first, .. rest];

        if (results.FirstOrDefault(r => !r.Reachable) is { } failed)
        {
            return new TelemetryHealthView(false, failed.Error, []);
        }

        ServiceSignals[] services =
        [
            .. results
                .SelectMany(r => r.Samples)
                .Select(s => s.Service)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(service => Join(results, service)),
        ];

        return new TelemetryHealthView(true, null, services);
    }

    private static ServiceSignals Join(PrometheusResult[] results, string service)
    {
        double? requestRate = ValueFor(results[0], service);
        bool namedByRequestRate = results[0].Samples.Any(s => s.Service == service);

        return new ServiceSignals(
            service,
            requestRate,
            ErrorRatioFor(results[1], service, requestRate),
            ValueFor(results[2], service),
            StatusRateFor(results[3], service, namedByRequestRate),
            StatusRateFor(results[4], service, namedByRequestRate),
            ValueFor(results[5], service),
            ValueFor(results[6], service));
    }

    /// <summary>
    /// The 5xx share, or zero where the service served requests and the ratio has no series for it. Over
    /// no requests the share is undefined, and stays absent.
    /// </summary>
    private static double? ErrorRatioFor(PrometheusResult errors, string service, double? requestRate)
    {
        // Traffic first: the rows are the union of separate instant queries, not one snapshot, and a
        // share over no traffic, or over traffic the request-rate query did not report, is undefined
        // whatever the ratio query returned.
        if (requestRate is not > 0)
        {
            return null;
        }

        // Presence, not value: ValueFor is null for a present sample that is not finite too, and a NaN
        // share is an unknown one, not a zero.
        if (errors.Samples.FirstOrDefault(s => s.Service == service) is { } sample)
        {
            return sample.Value;
        }

        return 0;
    }

    /// <summary>
    /// A status code's rate, summed over the series the panel splits it into, or zero where the counter
    /// never recorded that code. Only for a service the request-rate query names: a refusal rate for a
    /// service with no reported traffic is a cell the strip cannot vouch for.
    /// </summary>
    private static double? StatusRateFor(PrometheusResult result, string service, bool namedByRequestRate)
    {
        if (!namedByRequestRate)
        {
            return null;
        }

        PrometheusSample[] series = [.. result.Samples.Where(s => s.Service == service)];

        return series.Any(s => s.Value is null) ? null : series.Sum(s => s.Value);
    }

    private static double? ValueFor(PrometheusResult result, string service) =>
        result.Samples.FirstOrDefault(s => s.Service == service)?.Value;
}
