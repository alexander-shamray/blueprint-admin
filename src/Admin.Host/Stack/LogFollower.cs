using Admin.Host.Compose;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>
/// The Logs screen's one <c>docker compose logs -f</c>. Each Follow starts a new
/// long-running job, and nothing else ever ends one, so without this every click
/// left another docker process behind. Starting a follow stops the previous one
/// if it is still running; the host keeps at most one follow job.
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

            current = compose.FollowLogs(services);

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
