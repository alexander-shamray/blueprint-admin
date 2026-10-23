using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Workspace;

/// <summary>The workspace as served: its JSON, and the strong entity tag of exactly those bytes.</summary>
/// <param name="Json">The document, compact.</param>
/// <param name="Etag">Quoted, as the <c>ETag</c> and <c>If-Match</c> headers carry it.</param>
/// <param name="KeptAside">Set when the file on disk could not be used and was moved here; <paramref name="Json"/> is then the default.</param>
public sealed record WorkspaceSnapshot(string Json, string Etag, string? KeptAside = null);

public enum SaveOutcome
{
    Saved,

    /// <summary>The <c>If-Match</c> etag is not the current one; the snapshot is the current workspace.</summary>
    Stale,

    /// <summary>The document fails the schema; <see cref="SaveResult.Problem"/> says how.</summary>
    Invalid,
}

public sealed record SaveResult(SaveOutcome Outcome, WorkspaceSnapshot? Snapshot, string? Problem);

/// <summary>
/// The operator's pages, in one file, <c>workspace.json</c> under <c>Admin:DataDir</c> (widget-workspace spec §6.1). It is
/// written whole, to a temp file and then renamed over the old one, under a lock, so a crash leaves the old document or the
/// new and a second tab's save is told so by the etag instead of overwriting the first. The document is kept as parsed JSON
/// and never as a typed model, so a widget type or a field this console does not know is saved back untouched.
/// </summary>
public sealed partial class WorkspaceStore(
    string directory,
    TimeProvider time,
    ILogger<WorkspaceStore>? log = null,
    int version = WorkspaceSchema.Current,
    IReadOnlyList<Func<JsonObject, JsonObject>>? migrations = null)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock gate = new();
    private readonly IReadOnlyList<Func<JsonObject, JsonObject>> steps = migrations ?? WorkspaceMigrations.All;

    private string FilePath => Path.Combine(directory, "workspace.json");

    /// <summary>The saved workspace, or the default when there is none or the file cannot be used.</summary>
    public WorkspaceSnapshot Read()
    {
        lock (gate)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>Replaces the workspace with <paramref name="document"/> when <paramref name="ifMatch"/> is the current etag.</summary>
    public SaveResult Save(JsonNode? document, string ifMatch)
    {
        lock (gate)
        {
            return SaveUnlocked(document, ifMatch);
        }
    }

    /// <summary>
    /// Replaces the workspace with <paramref name="document"/>, whatever it was (spec §6.2's import). A file exported by an
    /// older console is stepped up first, as a saved one is on read, so an export outlives the version it was made at.
    /// </summary>
    public SaveResult Replace(JsonNode? document)
    {
        lock (gate)
        {
            return SaveUnlocked(Migrate(document), ifMatch: null);
        }
    }

    private SaveResult SaveUnlocked(JsonNode? document, string? ifMatch)
    {
        string? problem = WorkspaceSchema.Validate(document, version);

        if (problem is not null)
        {
            return new SaveResult(SaveOutcome.Invalid, null, problem);
        }

        WorkspaceSnapshot current = ReadUnlocked();

        if (ifMatch is not null && !string.Equals(ifMatch, current.Etag, StringComparison.Ordinal))
        {
            return new SaveResult(SaveOutcome.Stale, current, null);
        }

        string json = document!.ToJsonString();
        Write(json);

        return new SaveResult(SaveOutcome.Saved, Snapshot(json), null);
    }

    private WorkspaceSnapshot ReadUnlocked()
    {
        if (!File.Exists(FilePath))
        {
            return Snapshot(WorkspaceDefaults.Document(version).ToJsonString());
        }

        string text = File.ReadAllText(FilePath, Utf8);

        try
        {
            JsonNode? node = Migrate(JsonNode.Parse(text));
            string? problem = WorkspaceSchema.Validate(node, version);

            if (problem is null)
            {
                return Snapshot(node!.ToJsonString());
            }

            return KeepAside(problem);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return KeepAside(e.Message);
        }
    }

    // A document older than this console is stepped up; one this console cannot read, newer or not a workspace, is left to KeepAside.
    private JsonNode? Migrate(JsonNode? node)
    {
        if (node is not JsonObject workspace || workspace["version"] is not JsonValue v || !v.TryGetValue(out int found))
        {
            return node;
        }

        while (found >= 1 && found < version && found - 1 < steps.Count)
        {
            workspace = steps[found - 1](workspace);
            found++;
            workspace["version"] = found;
        }

        return workspace;
    }

    private WorkspaceSnapshot KeepAside(string why)
    {
        // The kept file may be the last copy of the operator's pages, so a name already taken gets a suffix, never an overwrite.
        string stem = Path.Combine(directory, $"workspace.unreadable-{time.GetUtcNow():yyyyMMddTHHmmssfff}");
        string aside = $"{stem}.json";

        for (int n = 2; File.Exists(aside); n++)
        {
            aside = $"{stem}-{n}.json";
        }

        File.Move(FilePath, aside);
        if (log is not null)
        {
            LogKeptAside(log, FilePath, why, aside);
        }

        return Snapshot(WorkspaceDefaults.Document(version).ToJsonString()) with { KeptAside = aside };
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $"workspace.json.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temp, json, Utf8);
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{File} could not be used ({Why}); it was moved to {Aside} and the default workspace is served")]
    private static partial void LogKeptAside(ILogger logger, string file, string why, string aside);

    private static WorkspaceSnapshot Snapshot(string json) =>
        new(json, $"\"{Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(json)))}\"");
}
