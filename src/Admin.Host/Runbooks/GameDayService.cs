using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Runbooks;

/// <summary>
/// The backend's game day, run as run-locally.md's "Game day" runs it: <c>--list --json</c> for the runbooks, <c>--cause</c> to break
/// the stack and <c>--restore</c> to put it back. The console decides nothing about a runbook: which have a script, what a cause costs
/// and how long the alert takes are the tool's (<c>tools/game-day/README.md</c>), read each time, so a runbook added to the clone
/// appears without a change here. A cause stops or pauses a container, cuts a grant or purges a queue, so it claims
/// <see cref="ComposeGate"/> as the projection rebuild does, and the confirmation it takes is the one the tool's catalogue names.
/// </summary>
public sealed partial class GameDayService(
    IProcessRunner runner,
    ComposeGate gate,
    RepoPaths paths,
    Func<string, string?> readRunbook,
    TimeProvider time)
{
    /// <summary>The tool, from the backend clone, as run-locally.md runs it.</summary>
    public const string Tool = "tools/game-day/game_day.py";

    /// <summary>What a person sends to confirm a cause the tool lists as <c>confirm</c>: the Cause button pressed a second time.</summary>
    public const string CauseConfirm = "cause";

    /// <summary>run-locally.md's <c>--list</c> with the flag that makes the listing one JSON document, which the tool's README gives a console.</summary>
    public static readonly string[] ListArguments = ["--list", "--json"];

    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A runbook's file name without <c>.md</c>: lower-case words, never a path and never a flag.</summary>
    public static bool IsRunbookName(string name) => RunbookName().IsMatch(name);

    /// <summary>How a run is named in a refusal, and the kind <see cref="Claim.Answer"/> is asked for.</summary>
    public static string Kind(string runbook, GameDayMode mode) => $"game day {mode.ToString().ToLowerInvariant()} {runbook}";

    /// <summary>
    /// What the request must carry to run a phase, from the tool's <c>confirmation</c>: nothing for <c>none</c>, <see cref="CauseConfirm"/>
    /// for <c>confirm</c>, the runbook's name for <c>typed</c>, which is what <c>down -v</c> takes (spec §8). A word the tool adds later is
    /// taken as the strictest, so a cause is never easier to confirm than the owner meant.
    /// </summary>
    public static string? ConfirmationFor(string confirmation, string runbook) => confirmation switch
    {
        "none" => null,
        "confirm" => CauseConfirm,
        _ => runbook,
    };

    public static ProcessSpec Spec(IReadOnlyList<string> arguments, RepoPaths paths) =>
        new(DeadLetterService.Python[0], [.. DeadLetterService.Python[1..], Tool, .. arguments], paths.BackendDir);

    /// <summary>The Markdown of <c>docs/runbooks/&lt;name&gt;.md</c> in the sibling clone, or null where there is none; never a copy kept here.</summary>
    public static Func<string, string?> ReadFromClone(RepoPaths paths) => name =>
    {
        try
        {
            return File.ReadAllText(Path.Combine(paths.BackendDir, "docs", "runbooks", name + ".md"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    /// <summary>Every runbook the tool lists, read now: <c>--list --json</c> is a read, so it is not kept among the jobs.</summary>
    public async Task<RunbookCatalogue> ListAsync(CancellationToken cancellationToken)
    {
        Job job = runner.Start(Spec(ListArguments, paths) with { Listed = false });
        CommandOutput output = await OneShot.CompleteAsync(runner, job, "game_day --list", time, cancellationToken);

        return output.Error is { } error ? Failed(error) : Parse(output.Stdout);
    }

    public string? Text(string name) => IsRunbookName(name) ? readRunbook(name) : null;

    /// <summary>
    /// Starts one half as one listed job, unless a stack-changing job is still going (that one is returned, and the caller refuses).
    /// <paramref name="phase"/> is the alert of a runbook's second rule and belongs to a cause alone.
    /// </summary>
    public Claim Start(string runbook, GameDayMode mode, string? phase) =>
        gate.Claim(Kind(runbook, mode), () => runner.Start(Spec(Arguments(runbook, mode, phase), paths)));

    /// <summary>The tool's arguments for one half, as run-locally.md's "Game day" spells them; a phase is a cause's alone.</summary>
    public static string[] Arguments(string runbook, GameDayMode mode, string? phase) =>
    [
        runbook,
        mode == GameDayMode.Cause ? "--cause" : "--restore",
        .. mode == GameDayMode.Cause && phase is not null ? new[] { "--phase", phase } : [],
    ];

    /// <summary>The tool's <c>--list --json</c> document: an array of runbooks, each with its phases when a script causes them.</summary>
    internal static RunbookCatalogue Parse(IReadOnlyList<string> stdout)
    {
        try
        {
            Row[] rows = JsonSerializer.Deserialize<Row[]>(string.Join('\n', stdout), ToolJson) ?? [];

            return new RunbookCatalogue(
                [.. rows.Select(r => new RunbookEntry(
                    r.Runbook,
                    r.Alerts ?? [],
                    r.Script,
                    r.Reason,
                    [.. (r.Phases ?? []).Select(p => new RunbookPhase(
                        p.Alert,
                        p.Impact,
                        p.Confirmation,
                        ConfirmationFor(p.Confirmation, r.Runbook),
                        p.Sustained,
                        p.FiresWithinSeconds,
                        p.ClearsWithinSeconds))])).Where(e => IsRunbookName(e.Runbook))],
                null);
        }
        catch (JsonException ex)
        {
            return Failed($"The tool's output could not be read as its --list --json document: {ex.Message}");
        }
    }

    private static RunbookCatalogue Failed(string error) => new([], error);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z")]
    private static partial Regex RunbookName();

    private sealed record Row(string Runbook, string[]? Alerts, bool Script, string? Reason, PhaseRow[]? Phases);

    private sealed record PhaseRow(string Alert, string Impact, string Confirmation, bool Sustained, int FiresWithinSeconds, int ClearsWithinSeconds);
}
