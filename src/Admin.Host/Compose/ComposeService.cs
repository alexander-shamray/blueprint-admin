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

    /// <summary>One service stopped, as the projection rebuild needs the BFF's consumption stopped (run-locally.md).</summary>
    public Job Stop(string service) => Run("stop", service);

    public Job Start(string service) => Run("start", service);

    /// <summary>
    /// The backend's override that cuts the saga's waits to seconds (ADR-128), a sibling of the Compose file. It belongs to the
    /// full-stack <c>up</c> only (run-locally.md), and it is only ever added to a recreate of Ordering by a Scenario's lease.
    /// </summary>
    internal const string ShortTimeoutsFileName = "docker-compose.short-timeouts.yml";

    /// <summary>
    /// One service recreated alone, as run-locally.md's "Restoring one service, and changing the saga's waits" gives it:
    /// <c>up -d --no-deps</c> for the named service, with the short-timeouts file added to the <c>-f</c> list or not. Compose
    /// starts a stopped container and recreates one whose environment differs, so the same line without the override
    /// restores either change. <c>--wait</c> is added, so the job ends when the service is healthy.
    /// </summary>
    public Job Recreate(string service, bool shortTimeouts) =>
        shortTimeouts
            ? runner.Start(new ProcessSpec("docker", ["compose", "-f", paths.ComposeFile, "-f", ShortTimeoutsFile(paths.ComposeFile), "up", "-d", "--no-deps", "--wait", service], paths.BackendDir))
            : Run("up", "-d", "--no-deps", "--wait", service);

    /// <summary>The override beside <paramref name="composeFile"/>, spelt with the separators that path uses.</summary>
    internal static string ShortTimeoutsFile(string composeFile) =>
        composeFile[..(composeFile.LastIndexOfAny(['/', '\\']) + 1)] + ShortTimeoutsFileName;

    public Job FollowLogs(IReadOnlyList<string> services) => Run(["logs", "-f", "--tail", "200", .. services]);

    public Job Exec(string service, params string[] args) => Read(["exec", "-T", service, .. args]);

    public Task<CommandOutput> ExecAsync(string service, string[] args, CancellationToken cancellationToken) =>
        CompleteAsync(Exec(service, args), $"docker compose exec {service}", cancellationToken);

    /// <summary>
    /// An exec the operator caused, not a read a screen polls: listed, so the job list and the transcript keep it, with
    /// each of <paramref name="secrets"/> shown scrubbed (<see cref="ProcessSpec.Secrets"/>).
    /// </summary>
    public Task<CommandOutput> ExecListedAsync(string service, string[] args, IReadOnlySet<string> secrets, CancellationToken cancellationToken) =>
        CompleteAsync(runner.Start(Spec(["exec", "-T", service, .. args]) with { Secrets = secrets }), $"docker compose exec {service}", cancellationToken);

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
