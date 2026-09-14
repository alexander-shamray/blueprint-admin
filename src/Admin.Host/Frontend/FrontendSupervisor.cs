using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client's one <c>npm start</c>, in the frontend clone (spec §5.4).
/// Start refuses while a job is running, and when <c>node_modules</c> is absent,
/// because <c>npm ci</c> is a prerequisite the console reports rather than runs
/// (spec §2.1). Stop kills the tree through the runner. Start and Stop are
/// serialised so two clicks cannot leave two <c>ng serve</c> processes.
/// </summary>
public sealed class FrontendSupervisor(IProcessRunner runner, RepoPaths paths, Func<string, bool> installed) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Written under the gate; Status reads it without, and a reference read is atomic.
    private volatile Job? current;

    public static bool HasNodeModules(string frontendDir) => Directory.Exists(Path.Combine(frontendDir, "node_modules"));

    public FrontendStatus Status()
    {
        Job? job = current;

        return new FrontendStatus(job is null ? null : JobSummary.Of(job), installed(paths.FrontendDir));
    }

    public async Task<FrontendStartResult> StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is { State: JobState.Running } running)
            {
                return new FrontendStartResult(FrontendStartOutcome.AlreadyRunning, running);
            }

            if (!installed(paths.FrontendDir))
            {
                return new FrontendStartResult(FrontendStartOutcome.NotInstalled, null);
            }

            Job job = runner.Start(new ProcessSpec("npm", ["start"], paths.FrontendDir));
            current = job;

            return new FrontendStartResult(FrontendStartOutcome.Started, job);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Job?> StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is not { State: JobState.Running } running)
            {
                return null;
            }

            // Not the request's token: an aborted request must not leave ng serve half-killed.
            await runner.StopAsync(running, CancellationToken.None);

            return running;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
