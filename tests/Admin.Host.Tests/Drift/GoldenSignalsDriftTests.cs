using System.Text.Json;
using Admin.Host.Telemetry;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>The strip's queries against the dashboard panels <see cref="GoldenSignals"/> copies, both ways.</summary>
public sealed class GoldenSignalsDriftTests
{
    /// <summary>What Grafana's "All" puts where the dashboard writes <c>$service</c>; GoldenSignals copies that form.</summary>
    private const string AllServices = ".+";

    /// <summary>
    /// Keyed by panel id, because the §13.7 titles carry the backend's targets and a moved target is not a
    /// changed query.
    /// </summary>
    private static readonly Dictionary<int, string> Copied = new()
    {
        [2] = GoldenSignals.RequestRate,
        [3] = GoldenSignals.ErrorRatio,
        [4] = GoldenSignals.LatencyP99,
        [6] = GoldenSignals.DomainRefusalRate,
        [7] = GoldenSignals.UnauthorisedRate,
        [9] = GoldenSignals.CommandP95,
        [10] = GoldenSignals.QueryP95,
    };

    public static TheoryData<int> PanelIds => [.. Copied.Keys];

    [Theory]
    [MemberData(nameof(PanelIds))]
    public void The_panel_still_queries_what_the_strip_copied(int id)
    {
        JsonElement[] panels = QueryPanels();
        JsonElement panel = panels.FirstOrDefault(p => p.GetProperty("id").GetInt32() == id);
        panel.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"golden-signals.json has no query panel {id}");

        Expressions(panel).ShouldBe([Copied[id]], panel.GetProperty("title").GetString());
    }

    [Fact]
    public void Every_query_panel_on_the_dashboard_is_copied()
    {
        string[] uncopied = [.. QueryPanels()
            .Where(p => !Copied.ContainsKey(p.GetProperty("id").GetInt32()))
            .Select(p => $"{p.GetProperty("id").GetInt32()}: {p.GetProperty("title").GetString()}")];

        uncopied.ShouldBeEmpty("a panel the strip does not show");
    }

    [Fact]
    public void Every_copied_query_is_one_GoldenSignals_holds()
    {
        string[] held = [.. typeof(GoldenSignals).GetFields().Select(f => (string)f.GetRawConstantValue()!)];

        Copied.Values.Order(StringComparer.Ordinal).ShouldBe(held.Order(StringComparer.Ordinal));
    }

    private static string[] Expressions(JsonElement panel) =>
        [.. panel.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("expr").GetString()!.Replace("$service", AllServices, StringComparison.Ordinal))];

    /// <summary>Every panel with queries, at any depth: a collapsed row holds its panels inside itself.</summary>
    private static JsonElement[] QueryPanels()
    {
        JsonDocument dashboard = JsonDocument.Parse(Backend.Read("deploy", "observability", "dashboards", "golden-signals.json"));
        List<JsonElement> found = [];
        Walk(dashboard.RootElement.GetProperty("panels"), found);

        return [.. found];
    }

    private static void Walk(JsonElement panels, List<JsonElement> found)
    {
        foreach (JsonElement panel in panels.EnumerateArray())
        {
            if (panel.TryGetProperty("targets", out JsonElement targets) && targets.GetArrayLength() > 0)
            {
                found.Add(panel.Clone());
            }

            if (panel.TryGetProperty("panels", out JsonElement nested))
            {
                Walk(nested, found);
            }
        }
    }
}
