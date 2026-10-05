using System.Globalization;
using Admin.Host.Fakes;
using Admin.Host.Jobs;

namespace Admin.Host.Transcript;

/// <summary>
/// What the operator did this session, as a person would type it: each child process the host started and each
/// request the proxy sent, in order (spec §5.11). It lives in memory for the host's life and is written nowhere
/// (spec §8). Every line is kept only after <see cref="FixtureScrubber.Scrub"/>, the rules a fixture is held to, so
/// a transcript and a recording cannot disagree about what a secret is.
/// </summary>
public sealed class OperatorTranscript(TimeProvider time)
{
    /// <summary>Entries kept; past it the oldest goes, and <see cref="TranscriptView.Dropped"/> counts it.</summary>
    public const int Capacity = 500;

    private readonly Lock gate = new();
    private readonly Queue<Kept> kept = new();
    private long next;
    private long dropped;

    /// <summary>A started child process. Its exit code is read from the job whenever the transcript is.</summary>
    public void Process(Job job) =>
        Keep(TranscriptKind.Process, ShellLine.Command(job.Spec), job.Spec.WorkingDirectory, null, job, null);

    /// <summary>A request the proxy sent, as <see cref="ShellLine.Curl"/> rendered it; a null status is no answer.</summary>
    public void Request(string curl, string? identity, int? status) =>
        Keep(TranscriptKind.Request, curl, null, identity, null, status);

    public TranscriptView Read()
    {
        lock (gate)
        {
            return new TranscriptView([.. kept.Select(k => k.Entry())], dropped);
        }
    }

    /// <summary>
    /// The transcript as a bash script, one command per entry with its outcome above it, in LF whatever the host's
    /// platform. Scrubbed once more as a whole, so that what the copy button hands over is held to the scrubber and
    /// not only each line.
    /// </summary>
    public string Script()
    {
        TranscriptView view = Read();
        List<string> lines =
        [
            "#!/usr/bin/env bash",
            "# blueprint-admin's operator transcript: each command the console ran and each request it sent,",
            $"# in order. Tokens, passwords and Authorization values read {FixtureScrubber.Scrubbed}: mint a token",
            "# as run-locally.md does and put it in their place.",
        ];

        if (view.Dropped > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"# {view.Dropped} earlier entries were dropped past the last {Capacity}."));
        }

        foreach (TranscriptEntry entry in view.Entries)
        {
            lines.Add("");
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"# {entry.Sequence} · {entry.At:u} · {Outcome(entry)}"));
            lines.Add(entry.WorkingDirectory is { } directory ? $"(cd {ShellLine.Quote(directory)} && {entry.Command})" : entry.Command);
        }

        return FixtureScrubber.Scrub(string.Join('\n', lines) + "\n");
    }

    private static string Outcome(TranscriptEntry entry) => entry.Kind switch
    {
        TranscriptKind.Process => entry.ExitCode is { } code ? $"exit {code}" : "still running",
        _ => $"{(entry.Identity is { } user ? $"as {user}" : "anonymous")} · {(entry.Status is { } status ? $"HTTP {status}" : "no answer")}",
    };

    private void Keep(TranscriptKind kind, string command, string? workingDirectory, string? identity, Job? job, int? status)
    {
        lock (gate)
        {
            kept.Enqueue(new Kept(++next, time.GetUtcNow(), kind, command, workingDirectory, identity, job, status));

            if (kept.Count > Capacity)
            {
                kept.Dequeue();
                dropped++;
            }
        }
    }

    private sealed record Kept(
        long Sequence, DateTimeOffset At, TranscriptKind Kind, string Command, string? WorkingDirectory, string? Identity, Job? Job, int? Status)
    {
        public TranscriptEntry Entry() =>
            new(Sequence, At, Kind, Command, WorkingDirectory, Identity, Job?.Status.ExitCode, Status);
    }
}

public enum TranscriptKind
{
    Process,
    Request,
}

/// <summary>
/// One thing the operator did. A process carries its working directory and, once it has exited, its exit code; a
/// request carries the identity it was sent as (null for anonymous) and the status, null when nothing answered.
/// </summary>
public sealed record TranscriptEntry(
    long Sequence,
    DateTimeOffset At,
    TranscriptKind Kind,
    string Command,
    string? WorkingDirectory,
    string? Identity,
    int? ExitCode,
    int? Status);

public sealed record TranscriptView(IReadOnlyList<TranscriptEntry> Entries, long Dropped);
