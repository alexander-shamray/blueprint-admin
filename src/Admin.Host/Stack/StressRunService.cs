using System.Text.RegularExpressions;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.Extensions.Options;

namespace Admin.Host.Stack;

/// <summary>
/// blueprint-backend's stress run, <c>deploy/observability/stress/stress.js</c>, as one job on the Stack screen beside the load run
/// (spec §5.3). Every step is a block of run-locally.md's "Stress run (k6)", run as written through PowerShell from the backend
/// clone and in the document's order: k6 answers, the stack comes up with the stress override, it is left idle for the minute the
/// document asks, Prometheus answers through Grafana, then the run block chooses its products, starts the sampler and k6, and
/// writes the summary. The console runs the backend's scripts and carries none: the staircase, the journey and the knee are
/// theirs, and the step shown is read off k6's own progress lines (<see cref="StressStaircase"/>). Unlike the load run, the job
/// claims <see cref="ComposeGate"/>, because its first block recreates each service the override changes; the way back to the normal
/// profile is the section's last block, which is the command the Stack's Up runs (<see cref="ComposeService.Up"/>), and the
/// override is read from the containers rather than remembered (<see cref="OnOverride"/>).
/// </summary>
public sealed partial class StressRunService(
    IProcessRunner runner,
    ComposeGate composeGate,
    JobRegistry registry,
    RepoPaths paths,
    IOptions<AdminOptions> options,
    TimeProvider time,
    TimeSpan settle,
    Func<string, IReadOnlyList<string>?> readSummary,
    Func<string, IReadOnlyList<string>> readOverride)
{
    /// <summary>The stress-run job's name in the job list, followed by its steps' names, and the kind a refusal names.</summary>
    public const string Kind = "stress-run";

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the full-stack up with the stress override.</summary>
    internal const string Up =
        "docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.stress.yml up -d --build --wait";

    /// <summary>Owner: run-locally.md, "Stress run (k6)": the override the up adds, as Compose records it on each container it creates.</summary>
    public const string OverrideFile = "docker-compose.stress.yml";

    /// <summary>
    /// Owner: blueprint-backend <c>deploy/compose/infrastructure.yml</c>, the service that runs <c>grafana/otel-lgtm</c> and
    /// mounts no volume of its own (held by <c>StressRunDriftTests</c>): recreating it loses the Prometheus, Loki and Tempo
    /// data it holds, so an Up that recreates it says so first.
    /// </summary>
    public const string ObservabilityService = "grafana";

    /// <summary>Owner: run-locally.md, "Stress run (k6)": "Leave the stack idle for a minute afterwards".</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the run, which exits with k6's own code after the summary is written.</summary>
    internal static readonly string Run = Block(
        """
        $ErrorActionPreference = 'Stop'
        $rows = @(docker compose -f deploy/compose/docker-compose.yml exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Local_Dev_Pa55w0rd!' -d Ordering -h -1 -W -s ',' -Q "SET NOCOUNT ON; SELECT LOWER(CONVERT(char(36), ProductId)), Currency FROM ordering.ProductPrices WHERE IsAvailable = 1 AND CONVERT(char(36), ProductId) LIKE '5EED0000-%' AND CAST(ROUND(Amount * 100, 0) AS bigint) % 100 NOT IN (1, 2, 5, 9) ORDER BY Currency, ProductId" | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^[0-9a-f-]{36},[A-Z]{3}$' })
        if ($rows.Count -eq 0) { 'products: none usable in ordering.ProductPrices'; exit 1 }
        $cur = ($rows[0] -split ',')[1]
        $ids = ($rows | Where-Object { $_.EndsWith(",$cur") } | ForEach-Object { ($_ -split ',')[0] }) -join ','
        "currency: $cur"; "products: $ids"
        $out = "artifacts/stress/$(Get-Date -Format yyyyMMdd-HHmmss)"
        New-Item -ItemType Directory -Force $out | Out-Null
        $sampler = Start-Process node -ArgumentList 'deploy/observability/stress/sample.mjs', $out -NoNewWindow -PassThru
        $env:KEYCLOAK_ADMIN_PASSWORD = 'admin'
        $ErrorActionPreference = 'Continue'
        k6 run --no-color --address 127.0.0.1:6565 -e BASE_URL=http://localhost:5000 -e KEYCLOAK_URL=http://localhost:8080 -e "STRESS_PRODUCTS=$ids" -e "STRESS_CURRENCY=$cur" -e "STRESS_OUT=$out" deploy/observability/stress/stress.js
        $code = $LASTEXITCODE; "k6 exit: $code"
        $sampler.WaitForExit()
        node deploy/observability/stress/summarise.mjs $out
        exit $code
        """);

    /// <summary>The step that waits out <see cref="Settle"/>: no block, so it starts no process.</summary>
    internal const string SettleStep = "settle";

    /// <summary>The marker the summary's lines are copied under once k6 has ended; a step of the job after the last block's.</summary>
    internal const string SummaryStep = "summary";

    /// <summary>The steps in the document's order, each a block but the settle; the k6 read and Prometheus's are the load run's.</summary>
    public static readonly IReadOnlyList<LoadRunStep> Steps =
    [
        new("k6", LoadRunService.K6Read, lines => !lines.Any(l => l.StartsWith(LoadRunService.K6Missing, StringComparison.Ordinal))),
        new("up", Up, _ => true),
        new(SettleStep, "", _ => true),
        new("prometheus", LoadRunService.PrometheusReady, lines => lines.Contains(LoadRunService.PrometheusYes)),
        new("run", Run, _ => true),
    ];

    /// <summary>Owner: run-locally.md, "Stress run (k6)", what k6's exit code says. A stress run reports and never passes or fails.</summary>
    public static readonly IReadOnlyDictionary<int, string> Verdicts = new Dictionary<int, string>
    {
        [0] = "every step ran, and none failed past the abort rate",
        [99] = "a step's requests failed at more than a fifth over its hold, and unless it was the last step k6 stopped there",
        [108] = "the knee stop ended the run at the end of that step's hold, so the step is measured whole and nothing past it ran",
        [107] = "setup() refused, so no load was driven and the run says nothing about the platform",
    };

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the start of the line summarise.mjs ends on that names the knee.</summary>
    internal const string KneePrefix = "Knee: ";

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the start of the line that says why the run ended, after the knee's.</summary>
    internal const string EndedPrefix = "Ended: ";

    /// <summary>Owner: blueprint-backend <c>deploy/observability/README.md</c>, "The stress run", the start of the line that names the service that crossed first.</summary>
    internal const string FirstToGiveUpPrefix = "First to give up: ";

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the line the run block prints with k6's code once k6 has run.</summary>
    internal const string K6ExitPrefix = "k6 exit: ";

    /// <summary>Owner: run-locally.md, "Stress run (k6)", the start of the line that names the summary, relative to the backend clone.</summary>
    internal const string SummaryPrefix = "Summary: ";

    /// <summary>
    /// What a Stop in the run block leaves. Ending k6's process tree is not its Ctrl+C: <c>handleSummary()</c> does not run, so
    /// no <c>raw.json</c> and no summary are written, and the run is neither a pass nor a fail. The stack stays on the override.
    /// </summary>
    internal const string StoppedInRun =
        "stopped in the run block: if k6 had started, it wrote no raw.json and no summary, so there is no knee and the run is neither a pass nor a fail; any orders it placed stay, and the stack is still on the stress override.";

    /// <summary>
    /// What a Stop leaves that ran out of <see cref="SummaryGrace"/> after k6 had ended: k6 wrote <c>raw.json</c>, but the sampler or
    /// summarise.mjs had not finished, so the block was ended and the summary may be missing or partial.
    /// </summary>
    internal const string StoppedSummarising =
        "stopped while the summary was being written: k6 had ended and wrote raw.json, but the sampler or summarise.mjs had not finished in time, so the block was ended and the summary may be missing; the stack is still on the stress override.";

    /// <summary>
    /// How long a Stop after k6's exit line waits for the sampler and summarise.mjs before it ends the block after all: the
    /// console's own bound, not the scripts'. How long each takes is <c>deploy/observability/stress/sample.mjs</c>'s and
    /// <c>summarise.mjs</c>'s to say.
    /// </summary>
    public static readonly TimeSpan SummaryGrace = TimeSpan.FromMinutes(2);

    /// <summary>What a Stop before k6 leaves: no load was driven. Only a Stop at or after the up can have left the override on.</summary>
    internal static string StoppedBefore(string step) => $"stopped at the {step} step, before k6 started: no load was driven and no order was placed.";

    private readonly Lock gate = new();

    // Written under the gate; the reads take them without it, and a reference read is atomic.
    private volatile Job? current;
    private volatile StressStaircase? staircase;
    private volatile string? knee;
    private volatile string? firstToGiveUp;
    private volatile string? ended;
    private volatile StressChoice? choice;
    private Job? step;
    private volatile bool k6Ended;
    private TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;

    /// <summary>The last stress run, running or exited, for the Stack screen; null before the first.</summary>
    public Job? Current => current;

    /// <summary>The last run with its step and its knee, as the Stack screen reads it; null before the first.</summary>
    public StressRunView? View() =>
        current is { } run ? new StressRunView(JobSummary.Of(run), staircase?.Current, knee, choice, ended, firstToGiveUp) : null;

    /// <summary>
    /// The services the override changes whose running container was created with <see cref="OverrideFile"/>: what the Stack
    /// screen shows while the stack runs with the override, and empty when it does not. Both ups recreate each service the
    /// override changes, so the files a running one was created from say which profile it runs; a service the override leaves
    /// alone keeps whatever label the last up that recreated it wrote, so only the ones it changes are read. Which those are is
    /// read from the clone's override file on every call (<see cref="ServicesOf"/>), never copied, so a service the backend
    /// adds to it is read the moment it is there, save one the Up never starts, which <see cref="ServicesOf"/> leaves out; in
    /// the file's order, so the screen's line does not move with the order
    /// <c>ps</c> prints.
    /// </summary>
    public IReadOnlyList<string> OnOverride(ComposeStatus backend) =>
        [.. readOverride(Path.Combine(paths.BackendDir, "deploy", "compose", OverrideFile)).Where(name => backend.Services.Any(s =>
            s.Service == name && s.State == "running" && s.ConfigFiles?.Contains(OverrideFile) == true))];

    /// <summary>
    /// Whether the Up that is the way back recreates <see cref="ObservabilityService"/>, given what <see cref="OnOverride"/>
    /// read: it is among the services running on the override.
    /// </summary>
    public static bool UpRecreatesObservability(IReadOnlyList<string> overridden) => overridden.Contains(ObservabilityService);

    /// <summary>
    /// The services the Up with the override file at <paramref name="path"/> starts, or none when there is no file there or it cannot be
    /// read: every Stack poll reads it, and a backend checkout replacing or locking it for a moment must not fail the read.
    /// </summary>
    public static IReadOnlyList<string> ReadOverride(string path)
    {
        try
        {
            return ServicesOf(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The keys of an override's top-level <c>services:</c> map that its Up starts, in the file's order: each line under it
    /// indented two spaces that starts with a key, whatever its value, until the next top-level key, less every service that sets
    /// a non-empty <c>profiles:</c>. Compose names a service by that key alone, so the Up changes exactly these; a profiled
    /// service is one the Up's command line, which names no <c>--profile</c>, does not start (the backend's compose k6,
    /// blueprint-backend ADR-170, is one), so it is not a service of the stack the Up moved onto the override, and the Up that
    /// is the way back has nothing of it to recreate.
    /// </summary>
    public static IReadOnlyList<string> ServicesOf(string overlay)
    {
        string[] lines = overlay.ReplaceLineEndings("\n").Split('\n');
        int services = Array.FindIndex(lines, l => TopLevelKey().Match(l) is { Success: true } m && m.Groups[1].Value == "services");
        if (services < 0)
        {
            return [];
        }

        // A service's own keys sit at the indent of its first one; a `profiles:` deeper than that is some map's entry, not the service's.
        List<(string Name, bool Profiled)> keys = [];
        int own = 0;
        foreach (string line in lines.Skip(services + 1).TakeWhile(l => !TopLevelKey().IsMatch(l)))
        {
            if (ServiceKey().Match(line) is { Success: true } service)
            {
                keys.Add((service.Groups[1].Value, FlowProfiles().IsMatch(line[service.Length..])));
                own = 0;
            }
            else if (keys.Count > 0 && NestedKey().Match(line) is { Success: true } nested)
            {
                own = own == 0 ? nested.Groups["indent"].Length : own;
                if (nested.Groups["indent"].Length == own && nested.Groups["key"].Value == "profiles"
                    && !NoProfiles().IsMatch(line[nested.Length..]))
                {
                    keys[^1] = keys[^1] with { Profiled = true };
                }
            }
        }

        return [.. keys.Where(k => !k.Profiled).Select(k => k.Name)];
    }

    /// <summary>
    /// The probe's rows, each surface that did not answer marked unpublished when its service runs on the override and none of
    /// its containers publishes a host port: the override may take a port away (it takes Ordering's, blueprint-backend
    /// ADR-158, and the gateway reaches the replicas by name), so the configured URL has nothing behind it by design while the
    /// profile lasts, and that is not the service being down. Read from <c>ps</c> as <see cref="OnOverride"/> is, so which
    /// service loses its port is not copied here.
    /// </summary>
    public static IReadOnlyList<Reachability> MarkUnpublished(ComposeStatus backend, IReadOnlyList<string> overridden, IReadOnlyList<Reachability> probed) =>
        [.. probed.Select(r => !r.Up
            && PlatformServices.All.FirstOrDefault(s => s.Name == r.Name) is { } service
            && overridden.Contains(service.ComposeService)
            && backend.Services.Where(s => s.Service == service.ComposeService).All(s => s.PublishedPorts.Count == 0)
                ? r with { Published = false }
                : r)];

    /// <summary>
    /// Starts a run under <see cref="ComposeGate"/>, or returns the one still going: its up recreates services, so it races an Up,
    /// a Down, a reset or a lease as they race each other.
    /// </summary>
    public Claim Start(StressChoice? asked = null) => composeGate.Claim(Kind, () =>
    {
        lock (gate)
        {
            Job run = registry.Create(new ProcessSpec(Kind, [.. Steps.Select(s => s.Name)], paths.BackendDir) { Steps = [.. Steps.Select(s => s.Name), SummaryStep] });
            current = run;
            staircase = null;
            knee = null;
            firstToGiveUp = null;
            ended = null;
            choice = asked ?? StressChoice.Default;
            step = null;
            k6Ended = false;
            stopping = false;
            stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => RunAsync(run));

            return run;
        }
    });

    /// <summary>
    /// Ends the run going, at the step it is in, and waits for the job to say so; null when none is going. As the load run's Stop,
    /// it kills the step's process tree. One that arrives after k6 has ended by itself kills nothing at first: the run block's
    /// sampler wait and summarise.mjs still run after k6's exit line, and killing them would lose the summary k6's raw.json was
    /// written for, so the Stop waits for them, for <see cref="SummaryGrace"/> at most, and then ends the block after all.
    /// </summary>
    public async Task<Job?> StopAsync()
    {
        Job run;
        Job? running;

        lock (gate)
        {
            if (current is not { State: JobState.Running } going)
            {
                return null;
            }

            run = going;
            running = step;

            if (running is not null && IsRun(running) && (k6Ended || running.State == JobState.Exited))
            {
                running = null;
            }
            else
            {
                stopping = true;
                stopped.TrySetResult();
            }
        }

        if (running is null && await Task.WhenAny(run.Completion, Task.Delay(SummaryGrace, time)) != run.Completion)
        {
            lock (gate)
            {
                stopping = true;
                running = step;
            }
        }

        if (running is not null)
        {
            await runner.StopAsync(running, CancellationToken.None);
        }

        await run.Completion;

        return run;
    }

    private async Task RunAsync(Job run)
    {
        try
        {
            foreach (LoadRunStep next in Steps)
            {
                if (next.Name == SettleStep)
                {
                    if (!await SettleAsync(run))
                    {
                        run.Append(OutputStream.Stderr, StoppedBefore(next.Name));
                        run.MarkExited(-1);
                        return;
                    }

                    continue;
                }

                Job job;

                lock (gate)
                {
                    if (stopping)
                    {
                        run.Append(OutputStream.Stderr, StoppedBefore(next.Name));
                        run.MarkExited(-1);
                        return;
                    }

                    ProcessSpec spec = LoadRunService.Spec(next.Block, paths, options.Value.SqlPassword);

                    // The choice reaches only the run block, as the document sets it before that block.
                    job = runner.Start(ReferenceEquals(next, Steps[^1]) ? spec with { Environment = choice?.Environment } : spec);
                    step = job;
                }

                StressStaircase? read = ReferenceEquals(next, Steps[^1]) ? new StressStaircase() : null;
                staircase = read;
                (int exitCode, List<string> stdout) = await FollowAsync(run, next, job, read, () => k6Ended = true);
                bool stopped;

                lock (gate)
                {
                    stopped = stopping;
                }

                if (stopped)
                {
                    run.Append(OutputStream.Stderr, !IsRun(job) ? StoppedBefore(next.Name) : k6Ended ? StoppedSummarising : StoppedInRun);
                    run.MarkExited(-1);
                    return;
                }

                if (read is not null)
                {
                    Finish(run, exitCode, stdout);
                    return;
                }

                if (exitCode != 0 || !next.Passed(stdout))
                {
                    string said = exitCode != 0 ? $"exited with {exitCode}" : $"said \"{stdout.LastOrDefault(l => l.Length > 0) ?? ""}\"";
                    run.Append(OutputStream.Stderr, $"{next.Name} {said}; the stress run stops here.");
                    run.MarkExited(exitCode != 0 ? exitCode : 1);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            run.Append(OutputStream.Stderr, $"stress run failed: {ex.Message}");
            run.MarkExited(-1);
        }
    }

    /// <summary>The minute the document leaves the stack idle; false when a Stop ended it.</summary>
    private async Task<bool> SettleAsync(Job run)
    {
        Task stop;

        lock (gate)
        {
            if (stopping)
            {
                return false;
            }

            stop = stopped.Task;
        }

        // The timer starts before the line that says so; a Stop leaves it to run out on its own, since it holds nothing.
        Task idle = Task.Delay(settle, time);
        run.Append(OutputStream.Stdout, $"== {SettleStep}: the stack is left idle for {settle.TotalSeconds:0} s, as run-locally.md asks");

        return await Task.WhenAny(idle, stop) == idle;
    }

    /// <summary>
    /// The run's end: the summary summarise.mjs wrote, carried into the job so it outlives the folder, then k6's code and what the
    /// document says it means, with the knee.
    /// </summary>
    private void Finish(Job run, int exitCode, List<string> stdout)
    {
        string? kneeLine = stdout.LastOrDefault(l => l.StartsWith(KneePrefix, StringComparison.Ordinal));
        string? summary = stdout.LastOrDefault(l => l.StartsWith(SummaryPrefix, StringComparison.Ordinal))?[SummaryPrefix.Length..].Trim();
        string? endedLine = stdout.LastOrDefault(l => l.StartsWith(EndedPrefix, StringComparison.Ordinal));
        string? giveUpLine = stdout.LastOrDefault(l => l.StartsWith(FirstToGiveUpPrefix, StringComparison.Ordinal));
        knee = kneeLine;
        ended = endedLine;
        firstToGiveUp = giveUpLine;

        if (summary is not null)
        {
            if (SummaryPath(summary) is { } path && readSummary(path) is { } lines)
            {
                run.Append(OutputStream.Stdout, $"== {SummaryStep} ({summary})");

                foreach (string line in lines)
                {
                    run.Append(OutputStream.Stdout, line);
                }
            }
            else
            {
                run.Append(OutputStream.Stderr, $"the summary {summary} could not be read under the clone's artifacts/stress, so it is not carried here.");
            }
        }

        // The block prints k6's code once k6 has run; without that line the block itself stopped first, and its code is not k6's.
        if (!stdout.Any(l => l.StartsWith(K6ExitPrefix, StringComparison.Ordinal)))
        {
            string last = stdout.LastOrDefault(l => l.Length > 0) ?? "";
            run.Append(OutputStream.Stderr, $"the run block exited {exitCode} before k6 ran, saying \"{last}\": no load was driven.");
            run.MarkExited(exitCode != 0 ? exitCode : 1);
            return;
        }

        string verdict = Verdicts.TryGetValue(exitCode, out string? said) ? said : "k6's own code, which run-locally.md does not read";
        run.Append(exitCode == 0 ? OutputStream.Stdout : OutputStream.Stderr, $"k6 exited {exitCode}: {verdict}. {kneeLine ?? "No knee was named."}{(endedLine is null ? "" : $" {endedLine}")}{(giveUpLine is null ? "" : $" {giveUpLine}")}");
        run.MarkExited(exitCode);
    }

    /// <summary>The summary's full path, when the line names a file under the clone's <c>artifacts/stress/</c>; null otherwise.</summary>
    private string? SummaryPath(string relative)
    {
        string root = Path.GetFullPath(Path.Combine(paths.BackendDir, "artifacts", "stress")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(paths.BackendDir, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The summary as summarise.mjs wrote it, or null when it is not there.</summary>
    public static IReadOnlyList<string>? ReadSummary(string path) => File.Exists(path) ? File.ReadAllLines(path) : null;

    /// <summary>
    /// Copies a step's lines into the run as they arrive, under a line naming the step and its own job. The run block's feed the
    /// staircase, and its k6 exit line calls <paramref name="ended"/>.
    /// </summary>
    private static async Task<(int ExitCode, List<string> Stdout)> FollowAsync(Job run, LoadRunStep next, Job job, StressStaircase? read, Action ended)
    {
        List<string> stdout = [];
        run.Append(OutputStream.Stdout, $"== {next.Name} (job {job.Id})");

        await foreach (OutputLine line in job.Follow(-1, CancellationToken.None))
        {
            run.Append(line.Stream, line.Text);

            if (line.Stream == OutputStream.Stdout)
            {
                stdout.Add(line.Text.Trim());
                read?.Observe(line.Text);

                if (read is not null && line.Text.StartsWith(K6ExitPrefix, StringComparison.Ordinal))
                {
                    ended();
                }
            }
        }

        return (await job.Completion, stdout);
    }

    /// <summary>Whether a step's job is the run block's, as it was started: with the configured <c>sa</c> password swapped in.</summary>
    private bool IsRun(Job job) => job.Spec.Arguments is [.., var block] && block == LoadRunService.Spec(Run, paths, options.Value.SqlPassword).Arguments[^1];

    /// <summary>A block as the document prints it, one line to a newline, whatever line ending this file was checked out with.</summary>
    private static string Block(string text) => text.ReplaceLineEndings("\n");

    /// <summary>A key at the start of a line, the next character not a space: a top-level key of a Compose file.</summary>
    [GeneratedRegex(@"^([A-Za-z0-9_][A-Za-z0-9._-]*):(\s|$)")]
    private static partial Regex TopLevelKey();

    /// <summary>A key indented two spaces, whatever follows it (a tag, an anchor, a flow map): a service under <c>services:</c>.</summary>
    [GeneratedRegex(@"^  ([A-Za-z0-9][A-Za-z0-9._-]*):(\s|$)")]
    private static partial Regex ServiceKey();

    /// <summary>A key indented deeper than a service's: one of a service's own keys, or an entry of a map under one.</summary>
    [GeneratedRegex(@"^(?<indent> {3,})(?<key>[A-Za-z0-9_][A-Za-z0-9._-]*):(\s|$)")]
    private static partial Regex NestedKey();

    /// <summary>
    /// A service written as a flow map, after any tag or anchor, that sets <c>profiles</c>, read up to any comment after it.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:[!&]\S+\s+)*\{[^#]*\bprofiles\s*:")]
    private static partial Regex FlowProfiles();

    /// <summary>A <c>profiles:</c> value that names none, so the service starts on every up: an empty list, reset or not.</summary>
    [GeneratedRegex(@"^\s*(?:!reset\s*)?\[\s*\]\s*(?:#.*)?$")]
    private static partial Regex NoProfiles();
}

/// <summary>
/// The last stress run as the Stack screen reads it: the job, the step k6 is at while it runs, the level and stop rule it was asked
/// for, and once summarise.mjs has printed them the knee, why the run ended and the service that crossed first, each as its line.
/// </summary>
public sealed record StressRunView(JobSummary Job, StressProgress? Progress, string? Knee, StressChoice? Choice, string? Ended, string? FirstToGiveUp);

/// <summary>
/// Where k6 is on the staircase: the users it runs and k6's ceiling, its elapsed time as it prints it, and the hold it is in or
/// last reached. <see cref="Step"/> counts holds seen, 0 during the first ramp. <see cref="MaxUsers"/> is the run's cap, not the
/// staircase's top: a level doubles on past its own steps until a stop rule ends it or the cap does, so the screen shows
/// <see cref="Users"/> as the target the run is at and the ceiling only as a ceiling.
/// </summary>
public sealed record StressProgress(int Users, int MaxUsers, string Elapsed, int Step, bool Holding);

/// <summary>
/// The step read off k6's own progress lines, <c>running (1m10.0s), 050/400 VUs, …</c>, which k6 prints once a second when its
/// output is not a terminal. The staircase is the backend's (<c>steps.js</c>) and can be changed from the environment, so it is not
/// copied: a hold is a count k6 has printed unchanged for <see cref="HoldTicks"/> lines, and each new one is the next step. The
/// default ramps move every second; a ramp slower than one user in <see cref="HoldTicks"/> seconds would read as a hold.
/// </summary>
public sealed partial class StressStaircase
{
    /// <summary>Progress lines, a second apart, a count must hold for before it is a step's hold rather than a ramp passing through it.</summary>
    public const int HoldTicks = 10;

    private int last = -1;
    private int same;
    private int lastHold = -1;
    private int holds;

    /// <summary>The last progress line read, or null before k6 printed one.</summary>
    public StressProgress? Current { get; private set; }

    public void Observe(string line)
    {
        Match m = Progress().Match(line);

        if (!m.Success)
        {
            return;
        }

        int users = int.Parse(m.Groups["users"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
        int max = int.Parse(m.Groups["max"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
        same = users == last ? same + 1 : 1;
        last = users;
        bool holding = users > 0 && same >= HoldTicks;

        if (holding && users != lastHold)
        {
            holds++;
            lastHold = users;
        }

        Current = new StressProgress(users, max, m.Groups["elapsed"].Value, holds, holding);
    }

    [GeneratedRegex(@"^running \((?<elapsed>[^)]+)\), (?<users>\d+)/(?<max>\d+) VUs,")]
    private static partial Regex Progress();
}
