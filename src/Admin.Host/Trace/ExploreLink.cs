using System.Text.Json.Nodes;

namespace Admin.Host.Trace;

/// <summary>
/// Grafana Explore deep links in the pane form measured on Grafana 13.1.1 (plan M8):
/// <c>{grafanaUrl}/explore?schemaVersion=1&amp;orgId=1&amp;panes={"t":{…}}</c> with the pane value
/// URL-encoded. The JSON is built through <see cref="JsonNode"/> rather than string concatenation so
/// a query that contains a quote or a backslash cannot break out of the pane document.
/// </summary>
public static class ExploreLink
{
    /// <summary>The Explore query for the correlation id's own Loki filter.</summary>
    public static string Loki(string grafanaUrl, string lokiUid, string logQl, string window) =>
        Pane(grafanaUrl, lokiUid, window, new JsonObject
        {
            ["refId"] = "A",
            ["expr"] = logQl,
        });

    /// <summary>The Explore query that opens one trace in Tempo by its hex id.</summary>
    public static string Tempo(string grafanaUrl, string tempoUid, string traceIdHex, string window) =>
        Pane(grafanaUrl, tempoUid, window, new JsonObject
        {
            ["refId"] = "A",
            ["queryType"] = "traceql",
            ["query"] = traceIdHex,
        });

    private static string Pane(string grafanaUrl, string uid, string window, JsonObject query)
    {
        JsonObject panes = new()
        {
            ["t"] = new JsonObject
            {
                ["datasource"] = uid,
                ["queries"] = new JsonArray(query),
                ["range"] = new JsonObject
                {
                    ["from"] = $"now-{window}",
                    ["to"] = "now",
                },
            },
        };

        return $"{grafanaUrl.TrimEnd('/')}/explore?schemaVersion=1&orgId=1&panes={Uri.EscapeDataString(panes.ToJsonString())}";
    }
}
