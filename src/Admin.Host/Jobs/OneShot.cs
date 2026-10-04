using Admin.Host.Compose;

namespace Admin.Host.Jobs;

/// <summary>A one-shot command run to completion within a bound, for every read the console makes by process.</summary>
public static class OneShot
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits at most <see cref="Timeout"/> for a one-shot command. One that does not answer is stopped and
    /// reported rather than awaited, so a stuck daemon costs a screen one slow answer, not a hang.
    /// On a non-zero exit the message is the first stderr line starting with "Error" (rabbitmqctl's
    /// usage banner otherwise pushes the real cause off the last line), else the last non-blank
    /// stderr line, else the exit code.
    /// </summary>
    public static async Task<CommandOutput> CompleteAsync(
        IProcessRunner runner, Job job, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        int exitCode;

        try
        {
            exitCode = await job.Completion.WaitAsync(Timeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            await runner.StopAsync(job, cancellationToken);

            return CommandOutput.Failed($"{name} did not answer within {Timeout.TotalSeconds:0} seconds");
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
            IEnumerable<OutputLine> stderr = lines.Where(l => l.Stream == OutputStream.Stderr);
            string? message = stderr.FirstOrDefault(l => l.Text.Trim().StartsWith("Error", StringComparison.OrdinalIgnoreCase))?.Text
                ?? stderr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l.Text))?.Text;

            return CommandOutput.Failed(message ?? $"{name} exited with {exitCode}");
        }

        // The ring keeps only the job's last Capacity lines: when the first retained
        // line's sequence is not the job's first-ever sequence (0), earlier lines,
        // including a leading "[", were evicted before this read.
        if (lines.Count > 0 && lines[0].Sequence != 0)
        {
            return CommandOutput.Failed($"{name} printed more than {job.Capacity} lines; only the last {job.Capacity} were kept");
        }

        return CommandOutput.Answered([.. lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)]);
    }
}
