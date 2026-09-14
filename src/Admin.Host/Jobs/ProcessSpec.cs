namespace Admin.Host.Jobs;

/// <summary>What to run and where. Arguments are passed as a list, never joined into a shell string.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";
}
