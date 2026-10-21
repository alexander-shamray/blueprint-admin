using System.Text.Json.Nodes;

namespace Admin.Host.Workspace;

/// <summary>
/// The workspace a console has before anything is saved (widget-workspace spec §5.3): the eleven built-in pages as
/// shipped, <c>widgets: null</c>, which the SPA builds from the defaults it carries.
/// </summary>
public static class WorkspaceDefaults
{
    /// <summary>
    /// Copied from <c>SCREENS</c> in <c>src/Admin.Web/src/app/screens.ts</c>, which owns it: a page's id and
    /// <c>builtIn</c> are the screen's path, its title the screen's label. A drift test reads that file.
    /// </summary>
    public static readonly IReadOnlyList<(string Path, string Label)> Screens =
    [
        ("stack", "Stack"),
        ("logs", "Logs"),
        ("broker", "Broker"),
        ("map", "Map"),
        ("erasure", "Erasure"),
        ("mail", "Mail"),
        ("requests", "API"),
        ("trace", "Trace"),
        ("scenario", "Scenario"),
        ("runbooks", "Runbooks"),
        ("transcript", "Transcript"),
    ];

    public static JsonObject Document(int version) => new()
    {
        ["version"] = version,
        ["pages"] = new JsonArray([.. Screens.Select(screen => (JsonNode)new JsonObject
        {
            ["id"] = screen.Path,
            ["title"] = screen.Label,
            ["builtIn"] = screen.Path,
            ["hidden"] = false,
            ["widgets"] = null,
        })]),
        ["activePageId"] = Screens[0].Path,
    };
}
