using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.Extensions.Options;

namespace Admin.Host.Stack;

/// <summary>A doctor row's verdict: fine, a problem Up or a call will meet, or a read that did not answer.</summary>
public enum DoctorState
{
    Ok,
    Problem,
    Unknown,
}

/// <summary>One row of the doctor panel: what was checked, its verdict, and what the reads said.</summary>
public sealed record DoctorCheck(string Name, DoctorState State, string Detail);

public sealed record DoctorView(IReadOnlyList<DoctorCheck> Checks);

/// <summary>
/// The workstation checks a developer makes by hand after Up has failed, read before it (spec §5.3). Every
/// read is a line of run-locally.md's step 0, run as written; none changes anything, none leaves the
/// machine, and none fetches, so a clone is judged against its last fetch. A row that cannot be judged — a read
/// that did not answer, or nothing yet to compare — is <see cref="DoctorState.Unknown"/>, never a guess either way.
/// Docker or Node not answering is the exception, because there the silence is itself the problem Up will meet.
/// </summary>
public sealed partial class WorkstationDoctor(
    IProcessRunner runner, ComposeService compose, RepoPaths paths, IOptions<AdminOptions> options, TimeProvider time)
{
    /// <summary>
    /// Owner: run-locally.md step 0. Every listener with its process name, which is what tells Docker's hold
    /// on a published port from another program's; the published ports come from the Compose model.
    /// </summary>
    internal const string ListenersLine =
        "Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Select-Object LocalPort, OwningProcess, @{ n = 'Process'; e = { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } } | ConvertTo-Json";

    /// <summary>Docker Desktop's process on Windows, which holds every port the running stack publishes (measured 2026-10-04).</summary>
    internal const string DockerProcess = "com.docker.backend";

    /// <summary>Owner: blueprint-backend <c>deploy/compose/services/gateway.yml</c>, whose environment carries the CORS list.</summary>
    internal const string GatewayService = "gateway";

    /// <summary>
    /// Owner: blueprint-backend Gateway.Api <c>Program.cs</c>, which reads <c>Cors:Enabled</c> and defaults it off; the
    /// gateway's Compose unit sets it under this environment name.
    /// </summary>
    internal const string CorsSwitch = "Cors__Enabled";

    public async Task<DoctorView> ReadAsync(CancellationToken cancellationToken)
    {
        Task<CommandOutput> docker = RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], cancellationToken);
        Task<CommandOutput> config = compose.ConfigAsync(cancellationToken);
        Task<CommandOutput> images = compose.ImagesAsync(cancellationToken);
        Task<CommandOutput> listeners = PowerShellAsync(ListenersLine, cancellationToken);
        Task<CommandOutput> node = RunAsync("node", ["--version"], cancellationToken);
        Task<CommandOutput> nvmrc = PowerShellAsync($"Get-Content '{Quoted($"{paths.FrontendDir}/.nvmrc")}'", cancellationToken);
        Task<CommandOutput> backend = RunAsync("git", ["-C", paths.BackendDir, "status", "-sb"], cancellationToken);
        Task<CommandOutput> frontend = RunAsync("git", ["-C", paths.FrontendDir, "status", "-sb"], cancellationToken);
        Task<CommandOutput> headMoved = RunAsync("git", ["-C", paths.BackendDir, "reflog", "-1", "--date=iso-strict", "--format=%gd"], cancellationToken);
        Task<ComposeStatus> stack = compose.PsAsync(cancellationToken);

        await Task.WhenAll(docker, config, images, listeners, node, nvmrc, backend, frontend, headMoved, stack);

        JsonDocument? model = Parse(config.Result);

        try
        {
            return new DoctorView(
            [
                Docker(docker.Result),
                Ports(model, config.Result, listeners.Result, stack.Result),
                Cors(model, config.Result, options.Value.ClientUrl),
                Node(node.Result, nvmrc.Result),
                Clone("Backend clone", backend.Result),
                Clone("Frontend clone", frontend.Result),
                Images(images.Result, headMoved.Result),
            ]);
        }
        finally
        {
            model?.Dispose();
        }
    }

    internal static DoctorCheck Docker(CommandOutput info) =>
        info.Error is { } error
            ? new("Docker", DoctorState.Problem, $"Docker did not answer: {error}")
            : new("Docker", DoctorState.Ok, $"Docker {Joined(info)} is answering.");

    /// <summary>
    /// A published port held by anything but Docker is one Up cannot publish, so Compose fails late. So is one Docker
    /// holds for a container outside this stack: Docker's own process is every container's listener, so only the
    /// stack's running containers say which of Docker's ports are this stack's.
    /// </summary>
    internal static DoctorCheck Ports(JsonDocument? model, CommandOutput config, CommandOutput listeners, ComposeStatus stack)
    {
        const string name = "Ports";

        if (model is null)
        {
            return new(name, DoctorState.Unknown, $"The Compose model could not be read: {config.Error ?? "not JSON"}");
        }

        if (listeners.Error is { } error)
        {
            return new(name, DoctorState.Unknown, $"The listeners could not be read: {error}");
        }

        SortedSet<int> published = [.. PublishedPorts(model)];
        List<Listener> held;

        try
        {
            held = [.. ReadListeners(Joined(listeners)).Where(l => published.Contains(l.Port))];
        }
        catch (JsonException e)
        {
            return new(name, DoctorState.Unknown, $"The listeners were not the JSON the line prints: {e.Message}");
        }

        Listener[] others = [.. held.Where(l => l.Process != DockerProcess).DistinctBy(l => l.Port).OrderBy(l => l.Port)];
        int[] dockerHeld = [.. held.Where(l => l.Process == DockerProcess).Select(l => l.Port).Distinct().Order()];
        HashSet<int> ours = [.. stack.Services.Where(s => s.State == "running").SelectMany(s => s.PublishedPorts)];
        int[] strangers = stack.Reachable ? [.. dockerHeld.Where(p => !ours.Contains(p))] : [];
        List<string> problems = [];

        if (others.Length > 0)
        {
            string owners = string.Join("; ", others
                .GroupBy(l => (l.Process, l.Pid))
                .Select(g => $"{string.Join(", ", g.Select(l => l.Port))} by {g.Key.Process ?? "an unnamed process"} (pid {g.Key.Pid})"));

            problems.Add($"Held by another program, so Up cannot publish them: {owners}.");
        }

        if (strangers.Length > 0)
        {
            problems.Add($"Held by Docker for a container outside this stack, so Up cannot publish them: {string.Join(", ", strangers)}.");
        }

        if (problems.Count > 0)
        {
            return new(name, DoctorState.Problem, string.Join(" ", problems));
        }

        if (!stack.Reachable && dockerHeld.Length > 0)
        {
            return new(name, DoctorState.Unknown,
                $"Docker holds {dockerHeld.Length} published ports, and the stack's containers could not be listed ({stack.Error}), so whether they are this stack's cannot be told.");
        }

        int docker = dockerHeld.Length;

        return new(name, DoctorState.Ok, docker == 0 ? $"All {published.Count} published ports are free."
            : docker == published.Count ? $"All {published.Count} published ports are held by this stack's running containers."
            : $"{docker} of {published.Count} published ports are held by this stack's running containers, the rest are free.");
    }

    /// <summary>Read and reported, never fixed from here: the CORS list is the backend's.</summary>
    internal static DoctorCheck Cors(JsonDocument? model, CommandOutput config, string clientUrl)
    {
        const string name = "Gateway CORS";
        string client = new Uri(clientUrl).GetLeftPart(UriPartial.Authority);

        if (model is null)
        {
            return new(name, DoctorState.Unknown, $"The Compose model could not be read: {config.Error ?? "not JSON"}");
        }

        if (!model.RootElement.GetProperty("services").TryGetProperty(GatewayService, out JsonElement gateway)
            || !gateway.TryGetProperty("environment", out JsonElement environment))
        {
            return new(name, DoctorState.Unknown, $"The Compose model has no {GatewayService} service environment.");
        }

        // Off unless the switch reads true, as the gateway's GetValue<bool> takes it: an origin list behind a switch
        // that is off admits nothing.
        if (!environment.TryGetProperty(CorsSwitch, out JsonElement enabled)
            || !bool.TryParse(enabled.GetString(), out bool on)
            || !on)
        {
            string value = enabled.ValueKind == JsonValueKind.String ? enabled.GetString()! : "unset";

            return new(name, DoctorState.Problem, $"The gateway's CORS is off ({CorsSwitch} is {value}), so it admits no browser origin, the client's {client} included.");
        }

        string[] origins = [.. environment.EnumerateObject()
            .Where(e => e.Name.StartsWith("Cors__Origins__", StringComparison.Ordinal))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => e.Value.GetString() ?? "")];
        string listed = origins.Length == 0 ? "none" : string.Join(", ", origins);

        return origins.Contains(client, StringComparer.OrdinalIgnoreCase)
            ? new(name, DoctorState.Ok, $"The gateway admits the client's origin {client} (it lists {listed}).")
            : new(name, DoctorState.Problem, $"The gateway does not admit the client's origin {client}; it lists {listed}.");
    }

    internal static DoctorCheck Node(CommandOutput node, CommandOutput nvmrc)
    {
        const string name = "Node";

        if (node.Error is { } error)
        {
            return new(name, DoctorState.Problem, $"node did not answer: {error}");
        }

        if (nvmrc.Error is { } missing)
        {
            return new(name, DoctorState.Unknown, $"Node {Joined(node)}; the frontend clone's .nvmrc could not be read: {missing}");
        }

        string running = Joined(node).TrimStart('v');
        string pinned = Joined(nvmrc).TrimStart('v');

        // A pin may name only a major or a minor, as nvm accepts.
        return running == pinned || running.StartsWith(pinned + ".", StringComparison.Ordinal)
            ? new(name, DoctorState.Ok, $"Node {running}, as the frontend clone's .nvmrc pins.")
            : new(name, DoctorState.Problem, $"Node {running}; the frontend clone's .nvmrc pins {pinned}.");
    }

    /// <summary>Judged against the last fetch: the doctor makes no network call, so a clone behind an unfetched origin reads level.</summary>
    internal static DoctorCheck Clone(string name, CommandOutput status)
    {
        if (status.Error is { } error)
        {
            return new(name, DoctorState.Unknown, $"git status did not answer: {error}");
        }

        string first = status.Stdout.Count > 0 ? status.Stdout[0] : "";
        Match branch = StatusLine().Match(first);

        if (!branch.Success)
        {
            return new(name, DoctorState.Unknown, $"git status printed no branch line: {first}");
        }

        string on = branch.Groups["branch"].Value;
        string tracking = branch.Groups["tracking"].Value;
        Match behind = Behind().Match(tracking);
        string dirty = status.Stdout.Skip(1).Any(l => l.Length > 0) ? " It has uncommitted changes." : "";

        if (on != "main")
        {
            return new(name, DoctorState.Problem, $"On {on}, not main.{dirty}");
        }

        if (behind.Success)
        {
            return new(name, DoctorState.Problem, $"main is {behind.Groups[1].Value} behind its upstream as last fetched.{dirty}");
        }

        string upstream = branch.Groups["upstream"].Success ? $"level with {branch.Groups["upstream"].Value}" : "with no upstream";

        return new(name, DoctorState.Ok, $"On main, {upstream} as last fetched{(tracking.Length > 0 ? $" ({tracking})" : "")}.{dirty}");
    }

    /// <summary>
    /// An image tagged before the backend's checkout last moved may not hold what it moved to, and Up does not rebuild
    /// an image that exists. The moment is the reflog's, when HEAD last changed here, not a commit's date: a commit
    /// pulled today can carry yesterday's date. The image's time is when it was last tagged, which a fully cached
    /// rebuild refreshes and its creation time does not. Only the images Compose built are compared: Compose names
    /// those after their container without its replica number, and a pulled image keeps its own name.
    /// </summary>
    internal static DoctorCheck Images(CommandOutput images, CommandOutput headMoved)
    {
        const string name = "Images";

        if (images.Error is { } error)
        {
            return new(name, DoctorState.Unknown, $"docker compose images did not answer: {error}");
        }

        Match moved = ReflogEntry().Match(Joined(headMoved));

        if (headMoved.Error is not null
            || !moved.Success
            || !DateTimeOffset.TryParse(moved.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset checkedOut))
        {
            return new(name, DoctorState.Unknown, $"When the backend's checkout last moved could not be read: {headMoved.Error ?? Joined(headMoved)}");
        }

        List<(string Repository, DateTimeOffset Tagged)> built = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(Joined(images));

            foreach (JsonElement image in document.RootElement.EnumerateArray())
            {
                string repository = image.GetProperty("Repository").GetString() ?? "";
                string container = image.GetProperty("ContainerName").GetString() ?? "";

                if (ReplicaSuffix().Replace(container, "") == repository
                    && (image.TryGetProperty("LastTagTime", out JsonElement tagged) || image.TryGetProperty("Created", out tagged)))
                {
                    built.Add((repository, tagged.GetDateTimeOffset()));
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(name, DoctorState.Unknown, $"docker compose images printed something other than its JSON: {e.Message}");
        }

        string at = checkedOut.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

        if (built.Count == 0)
        {
            return new(name, DoctorState.Unknown, "No container runs an image Compose built, so there is nothing to compare yet.");
        }

        string[] stale = [.. built.Where(b => b.Tagged < checkedOut).Select(b => b.Repository).Distinct().Order(StringComparer.Ordinal)];

        return stale.Length == 0
            ? new(name, DoctorState.Ok, $"All {built.Count} built images were tagged after the backend's checkout last moved ({at}).")
            : new(name, DoctorState.Problem,
                $"{stale.Length} of {built.Count} built images were tagged before the backend's checkout last moved ({at}), and Up does not rebuild them: {string.Join(", ", stale)}.");
    }

    internal static IEnumerable<int> PublishedPorts(JsonDocument model)
    {
        foreach (JsonProperty service in model.RootElement.GetProperty("services").EnumerateObject())
        {
            if (!service.Value.TryGetProperty("ports", out JsonElement ports))
            {
                continue;
            }

            foreach (JsonElement port in ports.EnumerateArray())
            {
                if (port.TryGetProperty("published", out JsonElement published)
                    && int.TryParse(published.GetString(), CultureInfo.InvariantCulture, out int number))
                {
                    yield return number;
                }
            }
        }
    }

    /// <summary>ConvertTo-Json prints one listener as an object and several as an array.</summary>
    private static IEnumerable<Listener> ReadListeners(string json)
    {
        if (json.Trim().Length == 0)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement[] items = root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : [root];

        return [.. items.Select(i => new Listener(
            i.GetProperty("LocalPort").GetInt32(),
            i.GetProperty("OwningProcess").GetInt32(),
            i.TryGetProperty("Process", out JsonElement process) && process.ValueKind == JsonValueKind.String ? process.GetString() : null))];
    }

    private static JsonDocument? Parse(CommandOutput config)
    {
        if (config.Error is not null)
        {
            return null;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(Joined(config));

            if (document.RootElement.TryGetProperty("services", out _))
            {
                return document;
            }

            document.Dispose();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Joined(CommandOutput output) => string.Join('\n', output.Stdout).Trim();

    /// <summary>A single-quoted PowerShell string holds anything but a single quote, which it doubles.</summary>
    private static string Quoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private Task<CommandOutput> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken) =>
        OneShot.CompleteAsync(
            runner,
            runner.Start(new ProcessSpec(fileName, arguments, paths.BackendDir) { Listed = false }),
            fileName,
            time,
            cancellationToken);

    private Task<CommandOutput> PowerShellAsync(string line, CancellationToken cancellationToken) =>
        RunAsync("powershell", ["-NoProfile", "-Command", line], cancellationToken);

    private sealed record Listener(int Port, int Pid, string? Process);

    /// <summary><c>## main...origin/main [behind 2]</c>; a detached head reads <c>## HEAD (no branch)</c>.</summary>
    [GeneratedRegex(@"^## (?<branch>\S+?)(?:\.\.\.(?<upstream>\S+))?(?: \[(?<tracking>[^\]]+)\])?(?: .*)?$")]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"behind (\d+)")]
    private static partial Regex Behind();

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex ReplicaSuffix();

    /// <summary><c>git reflog -1 --date=iso-strict --format=%gd</c> prints <c>HEAD@{2026-10-04T06:36:23+05:00}</c>.</summary>
    [GeneratedRegex(@"^HEAD@\{(.+)\}$")]
    private static partial Regex ReflogEntry();
}
