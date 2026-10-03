using System.Text.Json;
using Admin.Host.Telemetry;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>The strip's three queries against the dashboard panels <see cref="GoldenSignals"/> copies.</summary>
public sealed class GoldenSignalsDriftTests
{
    /// <summary>What Grafana's "All" puts where the dashboard writes <c>$service</c>; GoldenSignals copies that form.</summary>
    private const string AllServices = ".+";

    public static TheoryData<string, string> Panels => new()
    {
        { "Request rate", GoldenSignals.RequestRate },
        { "Error ratio (5xx)", GoldenSignals.ErrorRatio },
        { "Latency p99", GoldenSignals.LatencyP99 },
    };

    [Theory]
    [MemberData(nameof(Panels))]
    public void The_panel_still_queries_what_the_strip_copied(string title, string copied)
    {
        using JsonDocument dashboard = JsonDocument.Parse(Backend.Read("deploy", "observability", "dashboards", "golden-signals.json"));

        string[] expressions = [.. Panel(dashboard.RootElement, title)
            .GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("expr").GetString()!.Replace("$service", AllServices, StringComparison.Ordinal))];

        expressions.ShouldBe([copied], title);
    }

    private static JsonElement Panel(JsonElement element, string title)
    {
        foreach (JsonElement panel in element.GetProperty("panels").EnumerateArray())
        {
            if (panel.TryGetProperty("title", out JsonElement t) && t.GetString() == title && panel.TryGetProperty("targets", out _))
            {
                return panel;
            }
        }

        throw new ShouldAssertException($"No panel titled '{title}' with targets in golden-signals.json.");
    }
}
