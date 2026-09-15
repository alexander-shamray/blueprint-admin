using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Compose;

/// <summary>
/// Every Compose command the console runs, built from <see cref="RepoPaths"/>
/// and run in the backend clone: the exact lines run-locally.md gives.
/// </summary>
public sealed class ComposeService(IProcessRunner runner, RepoPaths paths, TimeProvider time)
{
    private static readonly TimeSpan OneShotTimeout = TimeSpan.FromSeconds(30);

    public Job Up() => Run("up", "-d", "--wait");

    public Job Down(bool wipeVolumes) => wipeVolumes ? Run("down", "-v") : Run("down");

    public Job FollowLogs(IReadOnlyList<string> services) => Run(["logs", "-f", "--tail", "200", .. services]);

    public Job Exec(string service, params string[] args) => Run(["exec", "-T", service, .. args]);

    public Task<CommandOutput> ExecAsync(string service, string[] args, CancellationToken cancellationToken) =>
        CompleteAsync(Exec(service, args), $"docker compose exec {service}", cancellationToken);

    public async Task<ComposeStatus> PsAsync(CancellationToken cancellationToken)
    {
        CommandOutput output = await CompleteAsync(Run("ps", "-a", "--format", "json"), "docker compose ps", cancellationToken);

        if (output.Error is not null)
        {
            return ComposeStatus.Unreachable(output.Error);
        }

        try
        {
            return ComposeStatus.Up(ComposePsParser.Parse(output.Stdout));
        }
        catch (JsonException ex)
        {
            return ComposeStatus.Unreachable($"docker compose ps output could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits at most 30 seconds for a one-shot command. One that does not answer is stopped and
    /// reported rather than awaited, so a stuck daemon costs a screen one slow answer, not a hang.
    /// </summary>
    private async Task<CommandOutput> CompleteAsync(Job job, string name, CancellationToken cancellationToken)
    {
        int exitCode;

        try
        {
            exitCode = await job.Completion.WaitAsync(OneShotTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            await runner.StopAsync(job, cancellationToken);

            return CommandOutput.Failed($"{name} did not answer within 30 seconds");
        }
        catch (OperationCanceledException)
        {
            // The caller's own token cancelled the wait, not the timeout: stop the
            // orphaned process with a fresh token so the stop itself is not
            // cancelled, then let the cancellation propagate as cancellation, not
            // as a failed command.
            await runner.StopAsync(job, CancellationToken.None);

            throw;
        }

        IReadOnlyList<OutputLine> lines = job.Since(-1);

        if (exitCode != 0)
        {
            string? lastError = lines.LastOrDefault(l => l.Stream == OutputStream.Stderr)?.Text;

            return CommandOutput.Failed(lastError ?? $"{name} exited with {exitCode}");
        }

        return CommandOutput.Answered([.. lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)]);
    }

    private Job Run(params string[] args) => runner.Start(new ProcessSpec("docker", ["compose", "-f", paths.ComposeFile, .. args], paths.BackendDir));
}
