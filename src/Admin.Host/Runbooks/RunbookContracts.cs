namespace Admin.Host.Runbooks;

/// <summary>
/// One alert a runbook's script causes, as <c>game_day.py --list --json</c> describes it (the tool's README, "Cause and restore
/// apart"). <paramref name="Impact"/> and <paramref name="Confirmation"/> are the owner's words, kept so the screen can say what the cause
/// costs; <paramref name="ConfirmWith"/> is what <see cref="GameDayService"/> requires the request to carry before it runs the cause,
/// or null when the cause plants and removes rows and takes none.
/// </summary>
public sealed record RunbookPhase(
    string Alert,
    string Impact,
    string Confirmation,
    string? ConfirmWith,
    bool Sustained,
    int FiresWithinSeconds,
    int ClearsWithinSeconds);

/// <summary>
/// A runbook in the backend's <c>docs/runbooks/</c>. <paramref name="Script"/> says whether the game day can cause its alerts; a
/// runbook it cannot has <paramref name="Reason"/> and no phases, and is still listed, because its text can be read beside the alert.
/// </summary>
public sealed record RunbookEntry(string Runbook, IReadOnlyList<string> Alerts, bool Script, string? Reason, IReadOnlyList<RunbookPhase> Phases);

/// <summary>The listing, or why it could not be read: a clone without the tool is a state the screen says, not an exception (spec §9).</summary>
public sealed record RunbookCatalogue(IReadOnlyList<RunbookEntry> Runbooks, string? Error);

/// <summary>A runbook's Markdown as the clone holds it.</summary>
public sealed record RunbookText(string Runbook, string Markdown);

/// <summary>What the operator sends to cause a runbook's alert: the phase it names (the first when null) and the confirmation its phase takes.</summary>
public sealed record CauseRequest(string? Phase, string? Confirm);

/// <summary>The two halves <c>game_day.py</c> runs apart.</summary>
public enum GameDayMode
{
    Cause,
    Restore,
}
