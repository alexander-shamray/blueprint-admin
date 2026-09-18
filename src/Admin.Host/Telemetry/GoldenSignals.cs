namespace Admin.Host.Telemetry;

/// <summary>
/// The three PromQL expressions of the "Rate, errors, duration" row in blueprint-backend's
/// <c>deploy/observability/dashboards/golden-signals.json</c> (spec §5.8), copied with the dashboard's
/// <c>$service</c> variable at "All". A changed panel there is a change here; the window each query
/// ranges over is the dashboard's, not this console's.
/// </summary>
public static class GoldenSignals
{
    /// <summary>The "Request rate" panel, in requests per second.</summary>
    public const string RequestRate =
        """sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+"}[5m]))""";

    /// <summary>The "Error ratio (5xx)" panel, a fraction of the request rate.</summary>
    public const string ErrorRatio =
        """sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+", http_response_status_code=~"5.."}[5m])) / sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+"}[5m]))""";

    /// <summary>The "Latency p99" panel, in seconds.</summary>
    public const string LatencyP99 =
        """histogram_quantile(0.99, sum by (service_name, le) (rate(http_server_request_duration_seconds_bucket{service_name=~".+"}[10m])))""";
}
