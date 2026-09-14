using System.Net.ServerSentEvents;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobStreamTests
{
    private static readonly ProcessSpec Spec = new("docker", ["compose", "logs", "-f"], "/tmp");

    [Fact]
    public async Task A_stream_whose_reader_fell_behind_ends_without_an_exited_event_so_the_browser_resumes()
    {
        Job job = new("job-1", Spec, new FakeTimeProvider(), capacity: 3);
        job.Append(OutputStream.Stdout, "line 0");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<SseItem<string>> events = JobStream.Events(job, -1, timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await events.MoveNextAsync()).ShouldBeTrue();
        events.Current.EventId.ShouldBe("0");

        for (int i = 1; i <= 10; i++)
        {
            job.Append(OutputStream.Stdout, $"line {i}");
        }

        List<SseItem<string>> rest = [];
        while (await events.MoveNextAsync())
        {
            rest.Add(events.Current);
        }

        rest.Select(e => e.EventId).ShouldBe(["1", "2", "3"]);
        rest.ShouldAllBe(e => e.EventType == "line");
    }

    [Fact]
    public async Task A_stream_that_fell_behind_before_the_job_exited_still_sends_no_exited_event()
    {
        Job job = new("job-1", Spec, new FakeTimeProvider(), capacity: 3);
        job.Append(OutputStream.Stdout, "line 0");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<SseItem<string>> events = JobStream.Events(job, -1, timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await events.MoveNextAsync()).ShouldBeTrue();

        for (int i = 1; i <= 10; i++)
        {
            job.Append(OutputStream.Stdout, $"line {i}");
        }

        job.MarkExited(0);

        List<SseItem<string>> rest = [];
        while (await events.MoveNextAsync())
        {
            rest.Add(events.Current);
        }

        rest.ShouldAllBe(e => e.EventType == "line");
    }

    [Fact]
    public async Task A_stream_that_kept_up_ends_with_the_exited_event()
    {
        Job job = new("job-1", Spec, new FakeTimeProvider(), capacity: 3);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<SseItem<string>> events = JobStream.Events(job, -1, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Task<bool> first = events.MoveNextAsync().AsTask();
        job.Append(OutputStream.Stdout, "only");
        (await first).ShouldBeTrue();
        job.MarkExited(4);

        (await events.MoveNextAsync()).ShouldBeTrue();
        events.Current.EventType.ShouldBe("exited");
        events.Current.Data.ShouldContain("\"exitCode\":4");
        (await events.MoveNextAsync()).ShouldBeFalse();
    }
}
