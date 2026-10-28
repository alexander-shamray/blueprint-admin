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
    private Job? lastStarted;

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

    /// <summary>
    /// The most recent job started, listed or not: an unlisted job is out of the registry by design, and a
    /// test still needs its final state. One job, not a list: FakePlatform's runner is a singleton that
    /// starts a ps every Stack poll, and a list would keep each one, output ring and all, for the host's
    /// life, which is the retention ProcessSpec.Listed removes.
    /// </summary>
    public Job? LastStarted
    {
        get
        {
            lock (gate)
            {
                return lastStarted;
            }
        }
    }

    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, params string[] lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, exitCode, OutputStream.Stdout, _ => lines));

        return this;
    }

    /// <summary>A script that exits, whose lines depend on the arguments that follow <paramref name="argumentPrefix"/>.</summary>
    public FakeProcessRunner On(string fileName, string argumentPrefix, int exitCode, Func<IReadOnlyList<string>, IEnumerable<string>> lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, exitCode, OutputStream.Stdout, lines));

        return this;
    }

    /// <summary>A non-zero exit whose output is stderr, not stdout, for testing how a failure's error message is chosen.</summary>
    public FakeProcessRunner OnFailing(string fileName, string argumentPrefix, int exitCode, params string[] stderrLines)
    {
        Add(new FakeScript(fileName, argumentPrefix, exitCode, OutputStream.Stderr, _ => stderrLines));

        return this;
    }

    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, params string[] lines) =>
        OnLongRunning(fileName, argumentPrefix, _ => lines);

    /// <summary>A long-running script whose lines depend on the arguments that follow <paramref name="argumentPrefix"/>.</summary>
    public FakeProcessRunner OnLongRunning(string fileName, string argumentPrefix, Func<IReadOnlyList<string>, IEnumerable<string>> lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, null, OutputStream.Stdout, lines));

        return this;
    }

    /// <summary>
    /// A script that prints its lines <paramref name="pace"/> apart and then exits, as a run that takes a while does; a Stop
    /// ends it between lines. The lines are read one at a time, so a script's own effects land as each line is printed.
    /// </summary>
    public FakeProcessRunner OnPaced(string fileName, string argumentPrefix, int exitCode, TimeSpan pace, TimeProvider time, Func<IReadOnlyList<string>, IEnumerable<string>> lines)
    {
        Add(new FakeScript(fileName, argumentPrefix, exitCode, OutputStream.Stdout, lines) { Pace = pace, Time = time });

        return this;
    }

    /// <summary>
    /// A paced script whose lines and exit code depend on the whole spec, its environment included: a run the caller shapes with
    /// variables, which the arguments alone do not say.
    /// </summary>
    public FakeProcessRunner OnPacedBySpec(string fileName, string argumentPrefix, TimeSpan pace, TimeProvider time, Func<ProcessSpec, (IEnumerable<string> Lines, int ExitCode)> run)
    {
        Add(new FakeScript(fileName, argumentPrefix, null, OutputStream.Stdout, _ => []) { Pace = pace, Time = time, BySpec = run });

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
            lastStarted = job;
            script = scripts.FirstOrDefault(s => s.FileName == spec.FileName && arguments.StartsWith(s.ArgumentPrefix, StringComparison.Ordinal));
        }

        if (script is null)
        {
            job.Append(OutputStream.Stderr, $"fake: no script for {spec.CommandLine}");
            job.MarkExited(127);

            return job;
        }

        string[] rest = arguments[script.ArgumentPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (script.BySpec is { } bySpec && script.Pace is { } specPace)
        {
            (IEnumerable<string> specLines, int specExit) = bySpec(spec);
            _ = Task.Run(() => PaceAsync(job, script with { ExitCode = specExit }, specLines, specPace));

            return job;
        }

        if (script.Pace is { } pace)
        {
            _ = Task.Run(() => PaceAsync(job, script, script.Lines(rest), pace));

            return job;
        }

        foreach (string line in script.Lines(rest))
        {
            job.Append(script.Stream, line);
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

    /// <summary>A paced script's lines, one every <paramref name="pace"/>, until they run out or a Stop marks the job exited.</summary>
    private static async Task PaceAsync(Job job, FakeScript script, IEnumerable<string> lines, TimeSpan pace)
    {
        foreach (string line in lines)
        {
            if (job.State == JobState.Exited)
            {
                return;
            }

            job.Append(script.Stream, line);
            await Task.Delay(pace, script.Time ?? TimeProvider.System);
        }

        job.MarkExited(script.ExitCode ?? 0);
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

    private sealed record FakeScript(string FileName, string ArgumentPrefix, int? ExitCode, OutputStream Stream, Func<IReadOnlyList<string>, IEnumerable<string>> Lines)
    {
        /// <summary>How far apart a paced script prints its lines; null for one that prints them all at once.</summary>
        public TimeSpan? Pace { get; init; }

        public TimeProvider? Time { get; init; }

        /// <summary>For a paced script shaped by its spec's environment: the lines and the code for the spec started.</summary>
        public Func<ProcessSpec, (IEnumerable<string> Lines, int ExitCode)>? BySpec { get; init; }
    }
}
