using System.Collections.Concurrent;
using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// A process runner that replays scripts instead of starting processes. Lives
/// in the host, not the test project, because FakePlatform mode ships it.
/// </summary>
public sealed class FakeProcessRunner(JobRegistry registry) : IProcessRunner, IAsyncDisposable
{
    private readonly List<FakeScript> scripts = [];
    private readonly List<ProcessSpec> started = [];
    private readonly ConcurrentQueue<Job> longRunning = new();

    public IReadOnlyList<ProcessSpec> Started => started;

    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)
    {
        scripts.Add(new FakeScript(fileName, argumentPrefix, exitCode, _ => lines));

        return this;
    }

    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines) =>
        OnLongRunning(fileName, argumentPrefix, _ => lines);

    /// <summary>A long-running script whose lines depend on the arguments that follow <paramref name="argumentPrefix"/>.</summary>
    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, Func<IReadOnlyList<string>, IEnumerable<string>> lines)
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
            longRunning.Enqueue(job);
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
        foreach (Job job in longRunning)
        {
            job.MarkExited(-1);
        }

        return ValueTask.CompletedTask;
    }

    private sealed record FakeScript(string FileName, string ArgumentPrefix, int? ExitCode, Func<IReadOnlyList<string>, IEnumerable<string>> Lines);
}
