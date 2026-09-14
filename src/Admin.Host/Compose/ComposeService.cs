using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Compose;

/// <summary>
/// Every Compose command the console runs, built from <see cref="RepoPaths"/>
/// and run in the backend clone: the exact lines run-locally.md gives.
/// </summary>
public sealed class ComposeService(IProcessRunner runner, RepoPaths paths)
{
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(30);

    public Job Up() => Run("up", "-d", "--wait");

    public Job Down(bool wipeVolumes) => wipeVolumes ? Run("down", "-v") : Run("down");

    public Job FollowLogs(IReadOnlyList<string> services) => Run(["logs", "-f", "--tail", "200", .. services]);

    public Job Exec(string service, params string[] args) => Run(["exec", "-T", service, .. args]);

    public async Task<ComposeStatus> PsAsync(CancellationToken cancellationToken)
    {
        Job job = Run("ps", "-a", "--format", "json");
        int exitCode;

        try
        {
            exitCode = await job.Completion.WaitAsync(PsTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await runner.StopAsync(job, cancellationToken);

            return ComposeStatus.Unreachable("docker compose ps did not answer within 30 seconds");
        }

        IReadOnlyList<OutputLine> lines = job.Since(-1);

        if (exitCode != 0)
        {
            string? lastError = lines.LastOrDefault(l => l.Stream == OutputStream.Stderr)?.Text;

            return ComposeStatus.Unreachable(lastError ?? $"docker compose ps exited with {exitCode}");
        }

        return ComposeStatus.Up(ComposePsParser.Parse(lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)));
    }

    private Job Run(params string[] args) => runner.Start(new ProcessSpec("docker", ["compose", "-f", paths.ComposeFile, .. args], paths.BackendDir));
}
