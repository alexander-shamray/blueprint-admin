using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Admin.Host.Jobs;

/// <summary>
/// One run of a child process: its output as a bounded ring of numbered lines,
/// and a channel per live follower. One-shot commands and long-running ones
/// are the same type so that every command's output is visible the same way.
/// </summary>
public sealed class Job
{
    private readonly Lock gate = new();
    private readonly OutputLine?[] ring;
    private readonly List<Channel<OutputLine>> followers = [];
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider time;
    private long next;

    public Job(string id, ProcessSpec spec, TimeProvider time, int capacity = 2000)
    {
        Id = id;
        Spec = spec;
        this.time = time;
        ring = new OutputLine?[capacity];
        StartedAt = time.GetUtcNow();
    }

    public string Id { get; }

    public ProcessSpec Spec { get; }

    public DateTimeOffset StartedAt { get; }

    public JobState State { get; private set; } = JobState.Running;

    public int? ExitCode { get; private set; }

    public Task<int> Completion => completion.Task;

    public void Append(OutputStream stream, string text)
    {
        lock (gate)
        {
            OutputLine line = new(next, time.GetUtcNow(), stream, text);
            ring[next % ring.Length] = line;
            next++;

            foreach (Channel<OutputLine> follower in followers)
            {
                follower.Writer.TryWrite(line);
            }
        }
    }

    public void MarkExited(int exitCode)
    {
        lock (gate)
        {
            if (State == JobState.Exited)
            {
                return;
            }

            State = JobState.Exited;
            ExitCode = exitCode;

            foreach (Channel<OutputLine> follower in followers)
            {
                follower.Writer.TryComplete();
            }
        }

        completion.TrySetResult(exitCode);
    }

    /// <summary>Buffered lines with a sequence greater than <paramref name="afterSequence"/>. Pass -1 for everything still in the ring.</summary>
    public IReadOnlyList<OutputLine> Since(long afterSequence)
    {
        lock (gate)
        {
            return SinceUnlocked(afterSequence);
        }
    }

    public IReadOnlyList<OutputLine> Tail(int count)
    {
        lock (gate)
        {
            return SinceUnlocked(next - count - 1);
        }
    }

    /// <summary>Replay what is buffered after <paramref name="afterSequence"/>, then every new line until the job exits.</summary>
    public IAsyncEnumerable<OutputLine> Follow(long afterSequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<OutputLine> replay;
        Channel<OutputLine>? live = null;

        lock (gate)
        {
            replay = SinceUnlocked(afterSequence);

            if (State == JobState.Running)
            {
                live = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions { SingleReader = true });
                followers.Add(live);
            }
        }

        return FollowCore(replay, live, cancellationToken);
    }

    private async IAsyncEnumerable<OutputLine> FollowCore(
        IReadOnlyList<OutputLine> replay,
        Channel<OutputLine>? live,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (OutputLine line in replay)
        {
            yield return line;
        }

        if (live is null)
        {
            yield break;
        }

        try
        {
            await foreach (OutputLine line in live.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
            }
        }
        finally
        {
            lock (gate)
            {
                followers.Remove(live);
            }
        }
    }

    private List<OutputLine> SinceUnlocked(long afterSequence)
    {
        long first = Math.Max(afterSequence + 1, next - ring.Length);
        List<OutputLine> lines = [];

        for (long sequence = Math.Max(first, 0); sequence < next; sequence++)
        {
            lines.Add(ring[sequence % ring.Length]!);
        }

        return lines;
    }
}
