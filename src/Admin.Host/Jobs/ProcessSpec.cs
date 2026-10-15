namespace Admin.Host.Jobs;

/// <summary>What to run and where. Arguments are passed as a list, never joined into a shell string.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>The command as the host shows and logs it: every word that is one of <see cref="Secrets"/> is scrubbed.</summary>
    public string CommandLine => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments.Select(Shown))}";

    /// <summary>
    /// Whether <see cref="JobRegistry"/> keeps the job, so that <c>/api/jobs</c> lists it and a screen can reopen
    /// its output by id. False for a read whose output reaches a screen only as the view its caller parses: the
    /// <c>ps</c> and the <c>exec</c> reads that <c>ComposeService</c> runs (spec §5.2). An <c>exec</c> the operator caused
    /// is kept (<c>ComposeService.ExecListedAsync</c>).
    /// </summary>
    public bool Listed { get; init; } = true;

    /// <summary>
    /// Variables set for the child on top of the host's own, each a line run-locally.md gives as <c>$env:Name = …</c>.
    /// Never part of <see cref="CommandLine"/>: a value may be a connection string.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Values never shown as they are, wherever this spec is: an argument or a variable equal to one is scrubbed in
    /// <see cref="CommandLine"/>, which the job list and the host's log print, and in the transcript. No pattern can
    /// recognise these by value: the dead-letter operator's password is random hex, minted for one run
    /// (<c>Broker/DeadLetterService.cs</c>).
    /// </summary>
    public IReadOnlySet<string> Secrets { get; init; } = new HashSet<string>();

    /// <summary>A word as it may be shown: <see cref="Fakes.FixtureScrubber.Scrubbed"/> for one of <see cref="Secrets"/>.</summary>
    public string Shown(string word) => Secrets.Contains(word) ? Fakes.FixtureScrubber.Scrubbed : word;
}
