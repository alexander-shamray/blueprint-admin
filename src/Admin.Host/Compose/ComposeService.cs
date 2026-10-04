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
    public Job Up() => Run("up", "-d", "--wait");

    public Job Down(bool wipeVolumes) => wipeVolumes ? Run("down", "-v") : Run("down");

    public Job FollowLogs(IReadOnlyList<string> services) => Run(["logs", "-f", "--tail", "200", .. services]);

    public Job Exec(string service, params string[] args) => Read(["exec", "-T", service, .. args]);

    public Task<CommandOutput> ExecAsync(string service, string[] args, CancellationToken cancellationToken) =>
        CompleteAsync(Exec(service, args), $"docker compose exec {service}", cancellationToken);

    /// <summary>The resolved Compose model, which owns every published port and service environment.</summary>
    public Task<CommandOutput> ConfigAsync(CancellationToken cancellationToken) =>
        CompleteAsync(Read("config", "--format", "json"), "docker compose config", cancellationToken);

    /// <summary>Each container's image and when it was built.</summary>
    public Task<CommandOutput> ImagesAsync(CancellationToken cancellationToken) =>
        CompleteAsync(Read("images", "--format", "json"), "docker compose images", cancellationToken);

    public async Task<ComposeStatus> PsAsync(CancellationToken cancellationToken)
    {
        CommandOutput output = await CompleteAsync(Read("ps", "-a", "--format", "json"), "docker compose ps", cancellationToken);

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

    private Task<CommandOutput> CompleteAsync(Job job, string name, CancellationToken cancellationToken) =>
        OneShot.CompleteAsync(runner, job, name, time, cancellationToken);

    private Job Run(params string[] args) => runner.Start(Spec(args));

    /// <summary>
    /// A read: its output reaches a screen only as the view the caller parses, never by job id, so the registry
    /// does not keep it (<see cref="ProcessSpec.Listed"/>).
    /// </summary>
    private Job Read(params string[] args) => runner.Start(Spec(args) with { Listed = false });

    private ProcessSpec Spec(string[] args) => new("docker", ["compose", "-f", paths.ComposeFile, .. args], paths.BackendDir);
}
