using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class LogFollowerTests
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/compose.yml");

    [Fact]
    public async Task A_request_cancelled_while_the_previous_follow_stops_starts_no_new_job()
    {
        JobRegistry registry = new(new FakeTimeProvider());
        FakeProcessRunner fake = new FakeProcessRunner(registry).OnLongRunning("docker", "compose", "gateway | started");
        using CancellationTokenSource request = new();
        CancellingRunner runner = new(fake, request);
        using LogFollower follower = new(new ComposeService(runner, Paths, TimeProvider.System), runner);

        Job previous = await follower.StartAsync([], TestContext.Current.CancellationToken);

        // The client aborts while the host is stopping the previous follow.
        await Should.ThrowAsync<OperationCanceledException>(() => follower.StartAsync([], request.Token));

        previous.State.ShouldBe(JobState.Exited);
        fake.Started.Count.ShouldBe(1);
        registry.All().ShouldNotContain(j => j.State == JobState.Running);
    }

    [Fact]
    public async Task Stop_ends_the_follow_job_it_names()
    {
        using LogFollower follower = Follower();
        Job job = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync(job.Id, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task A_stop_naming_a_superseded_follow_leaves_the_current_one_running()
    {
        using LogFollower follower = Follower();
        Job first = await follower.StartAsync([], TestContext.Current.CancellationToken);
        Job second = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync(first.Id, TestContext.Current.CancellationToken);

        second.State.ShouldBe(JobState.Running);
    }

    [Fact]
    public async Task A_stop_naming_no_follow_job_ends_nothing()
    {
        using LogFollower follower = Follower();
        Job job = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync("not-a-follow-job", TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Running);
    }

    private static LogFollower Follower()
    {
        FakeProcessRunner runner = new FakeProcessRunner(new JobRegistry(new FakeTimeProvider()))
            .OnLongRunning("docker", "compose", "gateway | started");

        return new LogFollower(new ComposeService(runner, Paths, TimeProvider.System), runner);
    }

    /// <summary>Cancels the request's token from inside the stop, as a client disconnect would.</summary>
    private sealed class CancellingRunner(IProcessRunner inner, CancellationTokenSource request) : IProcessRunner
    {
        public Job Start(ProcessSpec spec) => inner.Start(spec);

        public async Task StopAsync(Job job, CancellationToken cancellationToken)
        {
            await request.CancelAsync();
            await inner.StopAsync(job, cancellationToken);
        }
    }
}
