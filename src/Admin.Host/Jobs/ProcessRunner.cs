using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Admin.Host.Jobs;

/// <summary>
/// Real child processes. Output is read line by line on the process's own
/// threads; the ring buffer in <see cref="Job"/> is what makes that safe to
/// read from a request. This is the one type that knows a process has a tree,
/// which is what makes <c>ng serve</c> stoppable on Windows. Disposed with the
/// host's service provider, it kills every tree still running, so a
/// <c>docker compose logs -f</c> does not outlive the host.
/// </summary>
public sealed partial class ProcessRunner(JobRegistry registry, ILogger<ProcessRunner> logger) : IProcessRunner, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, (Job Job, Process Process)> processes = new();
    private int disposed;

    public Job Start(ProcessSpec spec)
    {
        Job job = registry.Create(spec);

        ProcessStartInfo info = new(spec.FileName)
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
        processes[job.Id] = (job, process);

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

        _ = CompleteWhenExitedAsync(job, process);

        return job;
    }

    public async Task StopAsync(Job job, CancellationToken cancellationToken)
    {
        if (!processes.TryGetValue(job.Id, out (Job Job, Process Process) tracked))
        {
            return;
        }

        Kill(tracked.Process);

        await job.Completion.WaitAsync(cancellationToken);
    }

    /// <summary>Kill every process tree still running and wait, at most a few seconds, for their jobs to exit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        List<Task<int>> exits = [];

        foreach ((Job job, Process process) in processes.Values)
        {
            try
            {
                Kill(process);
            }
            catch (Win32Exception ex)
            {
                LogCouldNotKill(ex, job.Spec.CommandLine);
            }

            exits.Add(job.Completion);
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

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already exited - and possibly already disposed by
            // CompleteWhenExitedAsync - between the lookup and the kill.
        }
    }

    private async Task CompleteWhenExitedAsync(Job job, Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);

        // WaitForExitAsync can complete before the async output/error readers
        // have delivered their last buffered lines. The parameterless
        // WaitForExit() overload, called after the process has already
        // exited, blocks only long enough for those readers to drain.
        process.WaitForExit();
        job.MarkExited(process.ExitCode);
        processes.TryRemove(job.Id, out _);
        process.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start {CommandLine}")]
    private partial void LogCouldNotStart(Exception ex, string commandLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not kill {CommandLine} at shutdown")]
    private partial void LogCouldNotKill(Exception ex, string commandLine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} child processes had not exited when shutdown stopped waiting")]
    private partial void LogShutdownTimedOut(Exception ex, int count);
}
