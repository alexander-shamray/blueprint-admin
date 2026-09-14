using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// A process runner that replays scripts instead of starting processes. Lives
/// in the host, not the test project, because FakePlatform mode ships it.
/// It is a singleton that concurrent requests share, so its lists are read and
/// written under one lock.
/// </summary>
public sealed class FakeProcessRunner(JobRegistry registry) : IProcessRunner, IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly List<FakeScript> scripts = [];
    private readonly List<ProcessSpec> started = [];
    private readonly List<Job> longRunning = [];

    /// <summary>A snapshot of every spec started so far, oldest first.</summary>
    public IReadOnlyList<ProcessSpec> Started
    {
        get
        {
            lock (gate)
            {
                return [.. started];
            }
        }
    }

    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, exitCode, _ => lines));

        return this;
    }

    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines) =>
        OnLongRunning(fileName, argumentPrefix, _ => lines);

    /// <summary>A long-running script whose lines depend on the arguments that follow <paramref name="argumentPrefix"/>.</summary>
    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, Func<IReadOnlyList<string>, IEnumerable<string>> lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, null, lines));

        return this;
    }

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);
        string arguments = string.Join(' ', spec.Arguments);
        FakeScript? script;

        lock (gate)
        {
            started.Add(spec);
            script = scripts.FirstOrDefault(s => s.FileName == spec.FileName && arguments.StartsWith(s.ArgumentPrefix, StringComparison.Ordinal));
        }

        if (script is null)
        {
            job.Append(OutputStream.Stderr, $"fake: no script for {spec.CommandLine}");
            job.MarkExited(127);

            return job;
        }

        string[] rest = arguments[script.ArgumentPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in script.Lines(rest))
        {
            job.Append(OutputStream.Stdout, line);
        }

        if (script.ExitCode is int exitCode)
        {
            job.MarkExited(exitCode);
        }
        else
        {
            lock (gate)
            {
                longRunning.RemoveAll(j => j.State == JobState.Exited);
                longRunning.Add(job);
            }
        }

        return job;
    }

    public Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        job.MarkExited(-1);

        return Task.CompletedTask;
    }

    /// <summary>Host shutdown: a scripted long-running job ends as a killed process would.</summary>
    public ValueTask DisposeAsync()
    {
        List<Job> running;

        lock (gate)
        {
            running = [.. longRunning];
        }

        foreach (Job job in running)
        {
            job.MarkExited(-1);
        }

        return ValueTask.CompletedTask;
    }

    private void Add(FakeScript script)
    {
        lock (gate)
        {
            scripts.Add(script);
        }
    }

    private sealed record FakeScript(string FileName, string ArgumentPrefix, int? ExitCode, Func<IReadOnlyList<string>, IEnumerable<string>> Lines);
}
