using System.Text.Json.Nodes;

namespace Admin.Host.Workspace;

/// <summary>
/// The migration hook (widget-workspace spec §6.3): step <c>n</c> turns a version-<c>n + 1</c> document into a
/// version-<c>n + 2</c> one, run on read. A test holds each one.
/// </summary>
public static class WorkspaceMigrations
{
    public static readonly IReadOnlyList<Func<JsonObject, JsonObject>> All =
    [
        // 1 to 2, phase 6 (§5.3): the legacy screen widget is retired. The host knows no widget types, so the document
        // is kept as it is; the SPA, which owns each screen's default widgets, replaces a remaining `legacy` instance
        // when it reads the workspace, and saves the result.
        RetireLegacyScreens,
    ];

    private static JsonObject RetireLegacyScreens(JsonObject document) => document;
}
