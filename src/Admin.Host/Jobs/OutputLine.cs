namespace Admin.Host.Jobs;

// CA1711: the name deliberately ends in "Stream" to mean stdout/stderr, not System.IO.Stream;
// the brief and the tests both spell it exactly this way, so it is suppressed rather than renamed.
#pragma warning disable CA1711
public enum OutputStream
#pragma warning restore CA1711
{
    Stdout,
    Stderr,
}

public enum JobState
{
    Running,
    Exited,
}

/// <summary>One line a child process wrote. Sequence numbers start at zero per job and never repeat.</summary>
public sealed record OutputLine(long Sequence, DateTimeOffset At, OutputStream Stream, string Text);
