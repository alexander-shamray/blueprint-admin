using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// A process runner that replays scripts instead of starting processes. Lives
/// in the host, not the test project, because FakePlatform mode ships it.
/// </summary>
public sealed class FakeProcessRunner(JobRegistry registry) : IProcessRunner
{
    private readonly List<FakeScript> scripts = [];
    private readonly List<ProcessSpec> started = [];

    public IReadOnlyList<ProcessSpec> Started => started;

    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)
    {
        scripts.Add(new FakeScript(fileName, argumentPrefix, exitCode, lines));

        return this;
    }

    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines)
    {
        scripts.Add(new FakeScript(fileName, argumentPrefix, null, lines));

        return this;
    }

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);
        started.Add(spec);

        string arguments = string.Join(' ', spec.Arguments);
        FakeScript? script = scripts.FirstOrDefault(s => s.FileName == spec.FileName && arguments.StartsWith(s.ArgumentPrefix, StringComparison.Ordinal));

        if (script is null)
        {
            job.Append(OutputStream.Stderr, $"fake: no script for {spec.CommandLine}");
            job.MarkExited(127);

            return job;
        }

        foreach (string line in script.Lines)
        {
            job.Append(OutputStream.Stdout, line);
        }

        if (script.ExitCode is int exitCode)
        {
            job.MarkExited(exitCode);
        }

        return job;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        job.MarkExited(-1);

        return Task.CompletedTask;
    }

    private sealed record FakeScript(string FileName, string ArgumentPrefix, int? ExitCode, IReadOnlyList<string> Lines);
}
