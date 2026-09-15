namespace Admin.Host.Compose;

/// <summary>A one-shot Compose command run to completion: its stdout when it exited 0, otherwise why it did not.</summary>
public sealed record CommandOutput(IReadOnlyList<string> Stdout, string? Error)
{
    public static CommandOutput Answered(IReadOnlyList<string> stdout) => new(stdout, null);

    public static CommandOutput Failed(string error) => new([], error);
}
