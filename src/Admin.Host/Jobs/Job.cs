using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Admin.Host.Jobs;

/// <summary>
/// One run of a child process: its output as a bounded ring of numbered lines,
/// and a channel per live follower. One-shot commands and long-running ones
/// are the same type so that every command's output is visible the same way.
/// A follower's channel holds at most a ring's worth of lines: one that falls
/// that far behind is ended rather than buffered without bound, and resumes
/// from the ring by sequence number (an EventSource does this on its own).
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

    /// <summary>Live followers still registered; for tests.</summary>
    internal int FollowerCount
    {
        get
        {
            lock (gate)
            {
                return followers.Count;
            }
        }
    }

    public void Append(OutputStream stream, string text)
    {
        lock (gate)
        {
            OutputLine line = new(next, time.GetUtcNow(), stream, text);
            ring[next % ring.Length] = line;
            next++;

            // Never wait on a slow reader here: this runs on the process's output
            // thread. A full channel means that follower is a ring behind, so it is
            // completed (it drains what it has, then ends) and dropped.
            followers.RemoveAll(follower =>
            {
                if (follower.Writer.TryWrite(line))
                {
                    return false;
                }

                follower.Writer.TryComplete();

                return true;
            });
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

    /// <summary>True when a line newer than <paramref name="sequence"/> has been appended.</summary>
    public bool HasLinesAfter(long sequence)
    {
        lock (gate)
        {
            return next - 1 > sequence;
        }
    }

    public IReadOnlyList<OutputLine> Tail(int count)
    {
        lock (gate)
        {
            return SinceUnlocked(next - count - 1);
        }
    }

    /// <summary>
    /// Replay what is buffered after <paramref name="afterSequence"/>, then every new line until the job
    /// exits or this follower falls a ring's worth of lines behind. Only in the first case is nothing left
    /// after the last line yielded; <see cref="HasLinesAfter"/> tells the two apart.
    /// </summary>
    public IAsyncEnumerable<OutputLine> Follow(long afterSequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<OutputLine> replay;
        Channel<OutputLine>? live = null;

        lock (gate)
        {
            replay = SinceUnlocked(afterSequence);

            if (State == JobState.Running)
            {
                live = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(ring.Length)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });
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
