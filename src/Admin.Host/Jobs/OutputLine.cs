namespace Admin.Host.Jobs;

public enum OutputStream
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
