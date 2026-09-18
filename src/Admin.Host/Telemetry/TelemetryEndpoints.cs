namespace Admin.Host.Telemetry;

/// <summary>The golden-signal strip (spec §5.10).</summary>
public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetry(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/telemetry/health", (TelemetryHealthService health, CancellationToken cancellationToken) =>
            health.ReadAsync(cancellationToken));

        return app;
    }
}
