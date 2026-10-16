using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client's one <c>npm start</c>, in the frontend clone (spec §5.4).
/// Start refuses while a job is running, and when <c>node_modules</c> is absent,
/// because <c>npm ci</c> is a prerequisite the console reports rather than runs
/// (spec §2.1). Stop kills the tree through the runner. Start and Stop are
/// serialised so two clicks cannot leave two <c>ng serve</c> processes.
/// The device build (<c>npm run emulator:android</c>, run-locally.md step 4) is a second job beside it, under the
/// same gate: it ends by itself, so it has a Start and no Stop, and it does not wait for the dev server.
/// </summary>
public sealed class FrontendSupervisor(IProcessRunner runner, RepoPaths paths, Func<string, bool> installed) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Written under the gate; Status reads it without, and a reference read is atomic.
    private volatile Job? current;
    private volatile Job? deviceBuild;

    /// <summary>The npm script run-locally.md step 4 builds and syncs the client for the emulator with.</summary>
    public const string DeviceScript = "emulator:android";

    public static bool HasNodeModules(string frontendDir) => Directory.Exists(Path.Combine(frontendDir, "node_modules"));

    public FrontendStatus Status()
    {
        Job? job = current;
        Job? device = deviceBuild;

        return new FrontendStatus(
            job is null ? null : JobSummary.Of(job),
            installed(paths.FrontendDir),
            device is null ? null : JobSummary.Of(device));
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

    public async Task<FrontendStartResult> StartDeviceBuildAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (deviceBuild is { State: JobState.Running } running)
            {
                return new FrontendStartResult(FrontendStartOutcome.AlreadyRunning, running);
            }

            if (!installed(paths.FrontendDir))
            {
                return new FrontendStartResult(FrontendStartOutcome.NotInstalled, null);
            }

            Job job = runner.Start(new ProcessSpec("npm", ["run", DeviceScript], paths.FrontendDir));
            deviceBuild = job;

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
