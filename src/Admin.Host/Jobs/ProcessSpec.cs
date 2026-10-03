namespace Admin.Host.Jobs;

/// <summary>What to run and where. Arguments are passed as a list, never joined into a shell string.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";

    /// <summary>
    /// Whether <see cref="JobRegistry"/> keeps the job, so that <c>/api/jobs</c> lists it and a screen can reopen
    /// its output by id. False for a read whose output reaches a screen only as the view its caller parses: the
    /// <c>ps</c> and <c>exec</c> that <c>ComposeService</c> runs (spec §5.2).
    /// </summary>
    public bool Listed { get; init; } = true;
}
