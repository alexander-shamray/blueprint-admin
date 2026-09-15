using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Admin.Host.Jobs;

/// <summary>
/// Real child processes. Output is read line by line on the process's own
/// threads; the ring buffer in <see cref="Job"/> is what makes that safe to
/// read from a request. This is the one type that knows a process has a tree,
/// which is what makes <c>ng serve</c> stoppable on Windows; with <see cref="ExecutableResolver"/>
/// it is also what makes <c>npm</c> startable there. Disposed with the
/// host's service provider, it kills every tree still running, so a
/// <c>docker compose logs -f</c> does not outlive the host.
/// </summary>
/// <remarks>
/// On Windows each process is put in its own <see cref="WindowsJobObject"/> just
/// after it starts, and stopping it terminates the job: <c>npm start</c> reaches
/// <c>ng serve</c> through a <c>cmd.exe</c> that exits first, and the orphan it
/// leaves is invisible to <see cref="Process.Kill(bool)"/>, which walks live
/// parent links. Assignment follows <see cref="Process.Start()"/> within
/// microseconds, so the window in which a child spawned before the assignment
/// escapes the job is tiny but real. Such a child is not killed directly, but
/// it does not make a stop hang: <see cref="StopAsync"/> is bounded regardless,
/// and closing the kill-on-close job when the job completes (see
/// <see cref="CompleteWhenExitedAsync"/>) also ends any descendant that
/// outlived the root and was still inside the job. Elsewhere, and on Windows
/// when the assignment fails, the kill is the tree walk.
/// </remarks>
public sealed partial class ProcessRunner : IProcessRunner, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Tracked> processes = new();
    private readonly JobRegistry registry;
    private readonly ILogger<ProcessRunner> logger;
    private readonly TimeSpan stopWait;
    private readonly Action<Job>? killOverride;
    private int disposed;

    public ProcessRunner(JobRegistry registry, ILogger<ProcessRunner> logger)
        : this(registry, logger, DefaultStopWait, killOverride: null)
    {
    }

    /// <summary>
    /// For tests: <paramref name="stopWait"/> bounds the wait after a kill, and
    /// <paramref name="killOverride"/>, when given, replaces the kill itself.
    /// </summary>
    internal ProcessRunner(JobRegistry registry, ILogger<ProcessRunner> logger, TimeSpan stopWait, Action<Job>? killOverride)
    {
        this.registry = registry;
        this.logger = logger;
        this.stopWait = stopWait;
        this.killOverride = killOverride;
    }

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);

        ProcessStartInfo info = new(ExecutableResolver.Resolve(spec.FileName))
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = new() { StartInfo = info };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                job.Append(OutputStream.Stdout, e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                job.Append(OutputStream.Stderr, e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            LogCouldNotStart(ex, spec.CommandLine);
            job.Append(OutputStream.Stderr, $"Could not start '{spec.FileName}': {ex.Message}");
            job.MarkExited(-1);
            process.Dispose();

            return job;
        }

        // The dictionary entry must exist before anything async happens so
        // that StopAsync can always find a running process.
        Tracked tracked = new(job, process, AssignToJobObject(process, spec));
        processes[job.Id] = tracked;

        // Begin the async readers before awaiting exit. Doing this on the
        // Exited event instead (as raised via EnableRaisingEvents) races: for
        // a process that exits immediately, Exited can run - and dispose the
        // process - on a thread pool thread before this method has even
        // reached BeginOutputReadLine, losing output and throwing
        // ObjectDisposedException out of Start. Waiting on WaitForExitAsync
        // from a continuation started right here, after the readers are
        // already running, keeps the ordering deterministic.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = CompleteWhenExitedAsync(tracked);

        return job;
    }

    /// <summary>
    /// Kill the process and everything it started, then wait a bounded time (ten
    /// seconds) for its output to end. A job whose output is still open after that,
    /// because some process the kill did not reach holds the pipes, is marked exited
    /// with -1, so a stop never hangs. After a timed-out stop the tracked entry and
    /// the process/job handles remain until the pipes eventually close or the host
    /// exits; a restart in the meantime runs beside the old process.
    /// </summary>
    public async Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        if (!processes.TryGetValue(job.Id, out Tracked? tracked))
        {
            return;
        }

        Kill(tracked);

        try
        {
            await job.Completion.WaitAsync(stopWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogStopTimedOut(job.Spec.CommandLine, stopWait);
            job.Append(OutputStream.Stderr, $"Stopped waiting after {stopWait.TotalSeconds:0} s; a process may still be running and holding the output.");
            job.MarkExited(-1);
        }
    }

    /// <summary>Kill every process tree still running and wait, at most a few seconds, for their jobs to exit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        List<Task<int>> exits = [];

        foreach (Tracked tracked in processes.Values)
        {
            Kill(tracked);
            exits.Add(tracked.Job.Completion);
        }

        try
        {
            // Bounded: a tree that will not die must not hang the host's shutdown.
            await Task.WhenAll(exits).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            LogShutdownTimedOut(ex, exits.Count(e => !e.IsCompleted));
        }
    }

    private WindowsJobObject? AssignToJobObject(Process process, ProcessSpec spec)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return WindowsJobObject.Assign(process);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // A process that has already exited cannot join a job, and has nothing
            // left to kill. Otherwise the tree walk still reaches every descendant
            // whose parents are alive.
            if (!process.HasExited)
            {
                LogCouldNotAssignJobObject(ex, spec.CommandLine);
            }

            return null;
        }
    }

    private void Kill(Tracked tracked)
    {
        if (killOverride is not null)
        {
            killOverride(tracked.Job);

            return;
        }

        try
        {
            if (OperatingSystem.IsWindows() && tracked.JobObject is { } jobObject)
            {
                jobObject.Terminate(-1);
            }
            else
            {
                tracked.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already exited - and the process or job object possibly already
            // disposed by CompleteWhenExitedAsync - between the lookup and the kill.
        }
        catch (Win32Exception ex)
        {
            LogCouldNotKill(ex, tracked.Job.Spec.CommandLine);
        }
    }

    private async Task CompleteWhenExitedAsync(Tracked tracked)
    {
        Process process = tracked.Process;

        // With redirected output this waits for the output to end as well as for
        // the process to exit: a root that exits while its descendants still write
        // keeps the job running, so StopAsync can still reach them.
        await process.WaitForExitAsync().ConfigureAwait(false);

        // WaitForExitAsync can complete before the async output/error readers
        // have delivered their last buffered lines. The parameterless
        // WaitForExit() overload, called after the process has already
        // exited, blocks only long enough for those readers to drain.
        process.WaitForExit();
        tracked.Job.MarkExited(process.ExitCode);
        processes.TryRemove(tracked.Job.Id, out _);

        // Closing a job object kills whatever is still in it.
        if (OperatingSystem.IsWindows())
        {
            tracked.JobObject?.Dispose();
        }

        process.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start {CommandLine}")]
    private partial void LogCouldNotStart(Exception ex, string commandLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not kill {CommandLine}")]
    private partial void LogCouldNotKill(Exception ex, string commandLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not put {CommandLine} in a job object; stopping it falls back to a process tree kill")]
    private partial void LogCouldNotAssignJobObject(Exception ex, string commandLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Output of {CommandLine} was still open {Wait} after the kill; the job is marked exited with -1")]
    private partial void LogStopTimedOut(string commandLine, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} child processes had not exited when shutdown stopped waiting")]
    private partial void LogShutdownTimedOut(Exception ex, int count);

    /// <summary>A started process, and on Windows the job object holding it and its descendants.</summary>
    private sealed record Tracked(Job Job, Process Process, WindowsJobObject? JobObject);
}
