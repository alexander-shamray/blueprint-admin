namespace Admin.Host.Telemetry;

/// <summary>The golden-signal and alert strips (spec §5.10).</summary>
public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetry(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/telemetry/health", (TelemetryHealthService health, CancellationToken cancellationToken) =>
            health.ReadAsync(cancellationToken));

        app.MapGet("/api/telemetry/alerts", (AlertStripService alerts, CancellationToken cancellationToken) =>
            alerts.ReadAsync(cancellationToken));

        return app;
    }
}
