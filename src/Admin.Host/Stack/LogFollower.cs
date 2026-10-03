using Admin.Host.Compose;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>
/// The Logs screen's one <c>docker compose logs -f</c>. Each Follow starts a new
/// long-running job, so starting a follow stops the previous one if it is still
/// running and the host keeps at most one. Stop, and leaving the Logs screen, end
/// it by name through <see cref="StopAsync"/>.
/// </summary>
public sealed class LogFollower(ComposeService compose, IProcessRunner runner) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Job? current;

    public async Task<Job> StartAsync(IReadOnlyList<string> services, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is { State: JobState.Running } previous)
            {
                // Not the request's token: a cancelled request must not leave the old process half-stopped.
                await runner.StopAsync(previous, CancellationToken.None);
            }

            // A request aborted during that stop wants no follow: starting one anyway
            // would leave a logs -f running that nothing is reading.
            cancellationToken.ThrowIfCancellationRequested();

            current = compose.FollowLogs(services);

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops the follow job <paramref name="jobId"/> names, and only while it is still the current one. The name is
    /// what makes a late Stop harmless: one that reaches the host after the next Follow names the job that Follow
    /// already stopped. There is deliberately no stop for any other job (spec §5.10).
    /// </summary>
    public async Task StopAsync(string jobId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Status, not State: only Job's own lock orders this read against MarkExited.
            if (current is { Status.State: JobState.Running } running && running.Id == jobId)
            {
                // Not the request's token, as in StartAsync: an aborted request must not leave the process half-stopped.
                await runner.StopAsync(running, CancellationToken.None);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
