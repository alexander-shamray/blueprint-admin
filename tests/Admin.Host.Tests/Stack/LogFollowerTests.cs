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
        using LogFollower follower = new(new ComposeService(runner, Paths), runner);

        Job previous = await follower.StartAsync([], TestContext.Current.CancellationToken);

        // The client aborts while the host is stopping the previous follow.
        await Should.ThrowAsync<OperationCanceledException>(() => follower.StartAsync([], request.Token));

        previous.State.ShouldBe(JobState.Exited);
        fake.Started.Count.ShouldBe(1);
        registry.All().ShouldNotContain(j => j.State == JobState.Running);
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
