using System.Text.Json.Nodes;

namespace Admin.Host.Workspace;

/// <summary>
/// The migration hook (widget-workspace spec §6.3): step <c>n</c> turns a version-<c>n + 1</c> document into a
/// version-<c>n + 2</c> one, run on read. There are none yet, because version 1 is the first schema; a test holds each one added.
/// </summary>
public static class WorkspaceMigrations
{
    public static readonly IReadOnlyList<Func<JsonObject, JsonObject>> All = [];
}
