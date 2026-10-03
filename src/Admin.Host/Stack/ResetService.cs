using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Stack;

/// <summary>
/// "Back to a known state" as one job: <c>down -v</c>, then <c>up -d --wait</c>, then the Stack screen's
/// readiness probe until every platform surface answers or the cap runs out (spec §5.3). Each step is the
/// command <see cref="ComposeService"/> already runs, so nothing here is a line run-locally.md lacks; the
/// reset job owns no process and carries its steps' output, in order, under one id.
/// </summary>
public sealed class ResetService(
    ComposeService compose,
    Func<CancellationToken, Task<IReadOnlyList<Reachability>>> probe,
    JobRegistry registry,
    RepoPaths paths,
    TimeProvider time)
{
    public static readonly TimeSpan ReadinessInterval = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan ReadinessCap = TimeSpan.FromMinutes(2);

    /// <summary>The reference client is not part of the backend a reset brings back, so it is not waited for.</summary>
    private const string Client = "client";

    private readonly Lock gate = new();
    private Job? current;

    /// <summary>Starts a reset, or returns the one still running: two at once would race each other's down and up.</summary>
    public Job Start()
    {
        lock (gate)
        {
            if (current is { Status.State: JobState.Running } running)
            {
                return running;
            }

            Job reset = registry.Create(new ProcessSpec("reset", ["down -v", "up -d --wait", "readiness"], paths.BackendDir));
            current = reset;
            _ = Task.Run(() => RunAsync(reset));

            return reset;
        }
    }

    private async Task RunAsync(Job reset)
    {
        try
        {
            bool ready = await StepAsync(reset, compose.Down(wipeVolumes: true))
                && await StepAsync(reset, compose.Up())
                && await WaitForReadinessAsync(reset);

            reset.MarkExited(ready ? 0 : 1);
        }
        catch (Exception ex)
        {
            reset.Append(OutputStream.Stderr, $"reset failed: {ex.Message}");
            reset.MarkExited(-1);
        }
    }

    /// <summary>Copies a step's lines into the reset job as they arrive; a step that fails ends the reset there.</summary>
    private static async Task<bool> StepAsync(Job reset, Job step)
    {
        reset.Append(OutputStream.Stdout, $"$ {step.Spec.CommandLine}");

        await foreach (OutputLine line in step.Follow(-1, CancellationToken.None))
        {
            reset.Append(line.Stream, line.Text);
        }

        int exitCode = await step.Completion;

        if (exitCode != 0)
        {
            reset.Append(OutputStream.Stderr, $"{step.Spec.CommandLine} exited with {exitCode}; the reset stops here.");
        }

        return exitCode == 0;
    }

    private async Task<bool> WaitForReadinessAsync(Job reset)
    {
        DateTimeOffset deadline = time.GetUtcNow() + ReadinessCap;

        while (true)
        {
            IReadOnlyList<Reachability> surfaces = [.. (await probe(CancellationToken.None)).Where(r => r.Name != Client)];
            string[] waiting = [.. surfaces.Where(r => !r.Up).Select(r => r.Name)];

            if (waiting.Length == 0)
            {
                reset.Append(OutputStream.Stdout, $"ready: {string.Join(", ", surfaces.Select(r => r.Name))}");
                return true;
            }

            if (time.GetUtcNow() >= deadline)
            {
                reset.Append(OutputStream.Stderr, $"not ready after {ReadinessCap.TotalSeconds:0} s: {string.Join(", ", waiting)}");
                return false;
            }

            // The delay starts before the line is written, so anything that reads the line sees a wait already under way.
            Task interval = Task.Delay(ReadinessInterval, time);
            reset.Append(OutputStream.Stdout, $"waiting for: {string.Join(", ", waiting)}");
            await interval;
        }
    }
}
