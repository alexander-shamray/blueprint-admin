using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "ps"], "/tmp");

    private static Job NewJob(int capacity = 2000) => new("job-1", Spec, new FakeTimeProvider(), capacity);

    [Fact]
    public void Appended_lines_are_numbered_from_zero()
    {
        Job job = NewJob();

        job.Append(OutputStream.Stdout, "one");
        job.Append(OutputStream.Stderr, "two");

        job.Since(-1).Select(l => (l.Sequence, l.Stream, l.Text)).ShouldBe([(0L, OutputStream.Stdout, "one"), (1L, OutputStream.Stderr, "two")]);
    }

    [Fact]
    public void The_ring_keeps_only_the_last_capacity_lines()
    {
        Job job = NewJob(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            job.Append(OutputStream.Stdout, $"line {i}");
        }

        job.Since(-1).Select(l => l.Text).ShouldBe(["line 2", "line 3", "line 4"]);
        job.Tail(2).Select(l => l.Text).ShouldBe(["line 3", "line 4"]);
    }

    [Fact]
    public void Since_returns_only_lines_after_the_given_sequence()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "a");
        job.Append(OutputStream.Stdout, "b");
        job.Append(OutputStream.Stdout, "c");

        job.Since(0).Select(l => l.Text).ShouldBe(["b", "c"]);
        job.Since(2).ShouldBeEmpty();
    }

    [Fact]
    public async Task Follow_replays_buffered_lines_then_streams_live_until_exit()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "before");
        List<string> seen = [];

        Task reader = Task.Run(async () =>
        {
            await foreach (OutputLine line in job.Follow(-1, TestContext.Current.CancellationToken))
            {
                seen.Add(line.Text);
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        job.Append(OutputStream.Stdout, "during");
        job.MarkExited(0);
        await reader.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        seen.ShouldBe(["before", "during"]);
        job.State.ShouldBe(JobState.Exited);
        job.ExitCode.ShouldBe(0);
        (await job.Completion).ShouldBe(0);
    }

    [Fact]
    public async Task Follow_on_an_exited_job_replays_and_completes()
    {
        Job job = NewJob();
        job.Append(OutputStream.Stdout, "only");
        job.MarkExited(3);

        List<string> seen = [];
        await foreach (OutputLine line in job.Follow(-1, TestContext.Current.CancellationToken))
        {
            seen.Add(line.Text);
        }

        seen.ShouldBe(["only"]);
    }

    [Fact]
    public async Task A_follower_that_falls_a_ring_behind_is_ended_and_removed_without_blocking_Append()
    {
        Job job = NewJob(capacity: 3);
        IAsyncEnumerable<OutputLine> follower = job.Follow(-1, TestContext.Current.CancellationToken);

        // Nobody reads: the fourth line finds the follower's queue full.
        await Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                job.Append(OutputStream.Stdout, $"line {i}");
            }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        job.FollowerCount.ShouldBe(0);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        List<string> seen = [];
        await foreach (OutputLine line in follower.WithCancellation(timeout.Token))
        {
            seen.Add(line.Text);
        }

        seen.ShouldBe(["line 0", "line 1", "line 2"]);
        job.State.ShouldBe(JobState.Running);
    }

    [Fact]
    public async Task A_follower_that_keeps_up_receives_every_line_past_the_capacity()
    {
        Job job = NewJob(capacity: 3);
        await using IAsyncEnumerator<OutputLine> follower = job.Follow(-1, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (int i = 0; i < 10; i++)
        {
            job.Append(OutputStream.Stdout, $"line {i}");
            (await follower.MoveNextAsync()).ShouldBeTrue();
            follower.Current.Text.ShouldBe($"line {i}");
        }

        job.MarkExited(0);

        (await follower.MoveNextAsync()).ShouldBeFalse();
        job.FollowerCount.ShouldBe(0);
    }

    [Fact]
    public void MarkExited_twice_keeps_the_first_exit_code()
    {
        Job job = NewJob();

        job.MarkExited(1);
        job.MarkExited(2);

        job.ExitCode.ShouldBe(1);
    }
}
