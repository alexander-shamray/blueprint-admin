using System.Globalization;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.Extensions.Options;

namespace Admin.Host.Broker;

/// <summary>
/// The BFF's order projection put right, as one job (run-locally.md, "Rebuild the BFF's order projection"). A repair is
/// the tool with no argument. A rebuild stops the BFF, runs the tool with <c>--reset</c> and starts the BFF again, in the
/// order that document gives, because the tool's README says a reset needs the BFF's consumption stopped. Either ends by
/// watching <see cref="PlatformQueues.BffProjection"/> until it is empty, as the document says to before trusting the
/// projection. Every step is a command a person types; the job owns no process and carries its steps' output, in order,
/// under one id, as <see cref="Stack.ResetService"/> does.
/// </summary>
public sealed class ProjectionRebuildService(
    ComposeService compose,
    ComposeGate composeGate,
    IProcessRunner runner,
    BrokerService broker,
    JobRegistry registry,
    RepoPaths paths,
    IOptions<AdminOptions> options,
    TimeProvider time)
{
    public static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan DrainCap = TimeSpan.FromMinutes(2);

    /// <summary>The Compose service that consumes <see cref="PlatformQueues.BffProjection"/>, owned by deploy/compose/services/web-bff.yml.</summary>
    public const string BffService = "web-bff";

    /// <summary>What a person types to confirm a rebuild, spelled as the command is.</summary>
    public const string RebuildConfirm = "bff-replay --reset";

    /// <summary>
    /// What the tool cannot do, each line with the words of blueprint-backend's <c>tools/bff-replay/README.md</c> it
    /// restates. The drift gate reads the README for every anchor (tests/Admin.Host.Tests/Drift/ReplayDriftTests.cs), so
    /// a limit the owner stops stating fails there rather than staying on screen.
    /// </summary>
    public static readonly ReplayLimit[] Limits =
    [
        new(
            "A replay reaches back one outbox window and no further: an order whose every event is older than that window is not restored by any run.",
            "A replay reaches back one outbox window and no further"),
        new(
            "A rebuild deletes the projection's order rows first, so it costs every order older than the window; a repair deletes nothing.",
            "it costs every order older than the window"),
        new(
            "A rebuild keeps bff.Products: a product name that is wrong is deleted by hand before the run, and only products Catalog's window still holds come back.",
            "--reset keeps bff.Products"),
        new(
            "A rebuild needs the BFF's consumption stopped while it runs, so the console stops web-bff, runs the tool and starts web-bff again, even when the tool fails.",
            "--reset needs the BFF's consumption stopped"),
    ];

    private readonly Lock gate = new();
    private Claim? current;

    public static ProjectionPlan Plan() => new(PlatformQueues.BffProjection, RebuildConfirm, [.. Limits.Select(l => l.Text)]);

    /// <summary>
    /// Starts a run. Two at once would delete and send over each other, so a run still going answers instead: the same
    /// kind of run is returned as it is (a second click is idempotent), and a different kind is <see cref="Claim.Kind"/>
    /// saying so, for the caller to refuse rather than show the wrong job under a confirmed request. A rebuild stops and
    /// starts the BFF, so it also claims <see cref="ComposeGate"/>, and a reset, Up or Down still going answers the same
    /// way; a repair touches no service and claims nothing there.
    /// </summary>
    public Claim Start(RebuildMode mode)
    {
        lock (gate)
        {
            if (current is { Job.Status.State: JobState.Running } running)
            {
                return running;
            }

            Claim claim = mode == RebuildMode.Rebuild
                ? composeGate.Claim(Kind(mode), () => Begin(mode))
                : new Claim(Begin(mode), Kind(mode));

            if (claim.Kind == Kind(mode))
            {
                current = claim;
            }

            return claim;
        }
    }

    /// <summary>How a run is named in a refusal, and the kind <see cref="Claim.Answer"/> is asked for.</summary>
    public static string Kind(RebuildMode mode) => mode.ToString().ToLowerInvariant();

    private Job Begin(RebuildMode mode)
    {
        string[] arguments = mode == RebuildMode.Rebuild ? ["--reset"] : [];
        Job job = registry.Create(new ProcessSpec("bff-replay", arguments, paths.BackendDir));
        _ = Task.Run(() => RunAsync(job, mode));

        return job;
    }

    private async Task RunAsync(Job job, RebuildMode mode)
    {
        try
        {
            bool ok = mode == RebuildMode.Repair ? await StepAsync(job, Replay(reset: false)) : await RebuildAsync(job);

            if (ok)
            {
                await WatchDrainAsync(job);
            }

            job.MarkExited(ok ? 0 : 1);
        }
        catch (Exception ex)
        {
            job.Append(OutputStream.Stderr, $"projection rebuild failed: {ex.Message}");
            job.MarkExited(-1);
        }
    }

    /// <summary>
    /// Stop, run, start. The BFF is started again whether or not the tool finished: a console that left the BFF down
    /// after a failed run would turn a partial projection into an outage, and the output says what state the run left.
    /// </summary>
    private async Task<bool> RebuildAsync(Job job)
    {
        if (!await StepAsync(job, compose.Stop(BffService)))
        {
            return false;
        }

        bool replayed;

        try
        {
            replayed = await StepAsync(job, Replay(reset: true));
        }
        catch (Exception ex)
        {
            // Anything the runner or the follow throws once the BFF is stopped is the tool not finishing, and must not
            // skip the start below.
            job.Append(OutputStream.Stderr, $"The tool could not run: {ex.Message}");
            replayed = false;
        }

        if (!replayed)
        {
            job.Append(OutputStream.Stderr, "The tool did not finish; the BFF is started again and the projection may be partial. Read the tool's lines above before another run.");
        }

        bool started = await StepAsync(job, compose.Start(BffService));

        return replayed && started;
    }

    /// <summary>The tool as run-locally.md runs it, from the backend clone, with the six connections it reads from its environment.</summary>
    public static ProcessSpec ReplaySpec(bool reset, RepoPaths paths, AdminOptions options) => new(
        "dotnet",
        reset ? ["run", "--project", "tools/bff-replay", "--", "--reset"] : ["run", "--project", "tools/bff-replay"],
        paths.BackendDir)
    {
        Environment = BffReplayConnections.For(options),
    };

    private Job Replay(bool reset) => runner.Start(ReplaySpec(reset, paths, options.Value));

    /// <summary>Copies a step's lines into the job as they arrive; a step that fails ends the run there.</summary>
    private static async Task<bool> StepAsync(Job job, Job step)
    {
        job.Append(OutputStream.Stdout, $"$ {step.Spec.CommandLine}");

        await foreach (OutputLine line in step.Follow(-1, CancellationToken.None))
        {
            job.Append(line.Stream, line.Text);
        }

        int exitCode = await step.Completion;

        if (exitCode != 0)
        {
            job.Append(OutputStream.Stderr, $"{step.Spec.CommandLine} exited with {exitCode}.");
        }

        return exitCode == 0;
    }

    /// <summary>
    /// Reads the queue until it is empty or the cap runs out. Neither outcome fails the run: the tool succeeded, and a queue
    /// still draining or a broker that did not answer is a state to watch on the Broker screen, which the line says.
    /// </summary>
    private async Task WatchDrainAsync(Job job)
    {
        DateTimeOffset deadline = time.GetUtcNow() + DrainCap;

        while (true)
        {
            QueuesView view = await broker.QueuesAsync(CancellationToken.None);

            if (!view.Reachable)
            {
                job.Append(OutputStream.Stderr, $"{PlatformQueues.BffProjection}: the broker did not answer ({view.Error}); watch the Broker screen.");
                return;
            }

            BrokerQueue? queue = view.Queues.FirstOrDefault(q => q.Name == PlatformQueues.BffProjection);

            if (queue is null)
            {
                job.Append(OutputStream.Stderr, $"{PlatformQueues.BffProjection}: queue not declared; watch the Broker screen.");
                return;
            }

            if (queue.Messages is 0)
            {
                job.Append(OutputStream.Stdout, $"{PlatformQueues.BffProjection}: 0 messages (drained). Trust the projection now.");
                return;
            }

            string depth = queue.Messages is { } waiting ? waiting.ToString(CultureInfo.InvariantCulture) : "an unknown number of";

            if (time.GetUtcNow() >= deadline)
            {
                job.Append(
                    OutputStream.Stderr,
                    $"{PlatformQueues.BffProjection}: {depth} message(s) still waiting after {DrainCap.TotalSeconds:0} s; watch the Broker screen and trust the projection once it is empty.");
                return;
            }

            // The delay starts before the line is written, so anything that reads the line sees a wait already under way.
            Task interval = Task.Delay(DrainInterval, time);
            job.Append(OutputStream.Stdout, $"{PlatformQueues.BffProjection}: {depth} message(s) waiting");
            await interval;
        }
    }
}
