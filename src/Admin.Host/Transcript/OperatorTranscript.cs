using System.Globalization;
using Admin.Host.Fakes;
using Admin.Host.Jobs;

namespace Admin.Host.Transcript;

/// <summary>
/// What the operator did this session, as a person would type it: each child process the host started and each
/// request the proxy sent, in the order they began (spec §5.11). It lives in memory for the host's life and is
/// written nowhere (spec §8). Every line is kept only after <see cref="FixtureScrubber.Scrub"/>, the rules a fixture
/// is held to, so a transcript and a recording cannot disagree about what a secret is.
/// </summary>
public sealed class OperatorTranscript(TimeProvider time)
{
    /// <summary>Entries kept; past it the oldest goes, and <see cref="TranscriptView.Dropped"/> counts it.</summary>
    public const int Capacity = 500;

    private readonly Lock gate = new();
    private readonly Queue<Kept> kept = new();
    private long next;
    private long dropped;

    /// <summary>
    /// A started child process. The job is held only until it exits and then let go, its exit code copied, because
    /// its output ring is what <see cref="JobRegistry"/> trims and a transcript must not keep it alive.
    /// </summary>
    public void Process(Job job)
    {
        Kept entry = Keep(TranscriptKind.Process, ShellLine.Command(job.Spec), job.Spec.WorkingDirectory, null);

        lock (gate)
        {
            entry.Job = job;
        }

        _ = job.Completion.ContinueWith(_ => Settle(entry), TaskScheduler.Default);
    }

    /// <summary>
    /// A request the proxy is about to send, as <see cref="ShellLine.Curl"/> rendered it. It takes its place in the
    /// order now, before the answer, and the returned callback settles it with the status, null for no answer: the
    /// proxy calls it whatever became of the send, so a request abandoned in flight is still on the record.
    /// </summary>
    public Action<int?> Request(string curl, string? identity)
    {
        Kept entry = Keep(TranscriptKind.Request, curl, null, identity);

        return status =>
        {
            lock (gate)
            {
                entry.Status = status;
                entry.Settled = true;
            }
        };
    }

    public TranscriptView Read()
    {
        lock (gate)
        {
            return new TranscriptView([.. kept.Select(Entry)], dropped);
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
            $"# in order. Tokens, passwords, cookies and Authorization values read {FixtureScrubber.Scrubbed}: mint a",
            "# token as run-locally.md does and put it in their place.",
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
        TranscriptKind.Process => entry.Settled ? $"exit {entry.ExitCode}" : "still running",
        _ => $"{(entry.Identity is { } user ? $"as {user}" : "anonymous")} · "
            + (!entry.Settled ? "awaiting an answer" : entry.Status is { } status ? $"HTTP {status}" : "no answer"),
    };

    /// <summary>Under the gate. Reads a held job's state itself, so an exit is seen before the continuation runs.</summary>
    private static TranscriptEntry Entry(Kept k)
    {
        if (k.Job is { Status: { State: JobState.Exited, ExitCode: int code } })
        {
            k.ExitCode = code;
            k.Settled = true;
            k.Job = null;
        }

        return new(k.Sequence, k.At, k.Kind, k.Command, k.WorkingDirectory, k.Identity, k.ExitCode, k.Status, k.Settled);
    }

    private void Settle(Kept entry)
    {
        lock (gate)
        {
            _ = Entry(entry);
        }
    }

    private Kept Keep(TranscriptKind kind, string command, string? workingDirectory, string? identity)
    {
        lock (gate)
        {
            Kept entry = new(++next, time.GetUtcNow(), kind, command, workingDirectory, identity);
            kept.Enqueue(entry);

            if (kept.Count > Capacity)
            {
                kept.Dequeue();
                dropped++;
            }

            return entry;
        }
    }

    /// <summary>The fixed half is set once; the settled half is written and read under the gate only.</summary>
    private sealed class Kept(long sequence, DateTimeOffset at, TranscriptKind kind, string command, string? workingDirectory, string? identity)
    {
        public long Sequence { get; } = sequence;

        public DateTimeOffset At { get; } = at;

        public TranscriptKind Kind { get; } = kind;

        public string Command { get; } = command;

        public string? WorkingDirectory { get; } = workingDirectory;

        public string? Identity { get; } = identity;

        public Job? Job { get; set; }

        public int? ExitCode { get; set; }

        public int? Status { get; set; }

        public bool Settled { get; set; }
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
/// <c>Settled</c> is false while the process runs or the request awaits its answer.
/// </summary>
public sealed record TranscriptEntry(
    long Sequence,
    DateTimeOffset At,
    TranscriptKind Kind,
    string Command,
    string? WorkingDirectory,
    string? Identity,
    int? ExitCode,
    int? Status,
    bool Settled);

public sealed record TranscriptView(IReadOnlyList<TranscriptEntry> Entries, long Dropped);
