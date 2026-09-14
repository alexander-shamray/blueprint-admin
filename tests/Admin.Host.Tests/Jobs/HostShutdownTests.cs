using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class HostShutdownTests
{
    [Fact]
    public async Task Stopping_the_host_kills_the_children_the_real_runner_started()
    {
        AdminHostFactory factory = new();
        Job job = factory.Services.GetRequiredService<ProcessRunner>()
            .Start(new ProcessSpec("node", ["-e", "setInterval(() => console.log('tick'), 50)"], Environment.CurrentDirectory));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Running);

        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The factory's DisposeAsync stops the host; the entry point disposes the
        // service provider on its own thread just after, so wait for the exit.
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task Stopping_the_host_ends_the_jobs_the_fake_runner_started()
    {
        AdminHostFactory factory = new();
        FakeProcessRunner runner = factory.Services.GetRequiredService<FakeProcessRunner>();
        Job job = runner.OnLongRunning("tail", "shutdown", "first").Start(new ProcessSpec("tail", ["shutdown"], "/"));
        job.State.ShouldBe(JobState.Running);

        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The factory's DisposeAsync stops the host; the entry point disposes the
        // service provider on its own thread just after, so wait for the exit.
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        job.State.ShouldBe(JobState.Exited);
    }
}
