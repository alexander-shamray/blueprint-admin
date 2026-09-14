using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakeProcessRunnerTests
{
    private readonly JobRegistry registry = new(new FakeTimeProvider());

    [Fact]
    public async Task A_scripted_command_emits_its_lines_and_exits()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .On("docker", "compose -f x ps", 0, "{\"Service\":\"gateway\"}");

        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "x", "ps", "-a", "--format", "json"], "/b"));
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        job.Since(-1).Single().Text.ShouldBe("{\"Service\":\"gateway\"}");
        runner.Started.Single().CommandLine.ShouldBe("docker compose -f x ps -a --format json");
    }

    [Fact]
    public async Task A_long_running_script_stays_running_until_stopped()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .OnLongRunning("docker", "compose -f x logs", "gateway | started");

        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "x", "logs", "-f"], "/b"));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Running);
        job.Since(-1).Single().Text.ShouldBe("gateway | started");

        await runner.StopAsync(job, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task An_unscripted_command_exits_127_with_a_message()
    {
        FakeProcessRunner runner = new(registry);

        Job job = runner.Start(new ProcessSpec("npm", ["start"], "/f"));
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        exit.ShouldBe(127);
        job.Since(-1).Single().Text.ShouldBe("fake: no script for npm start");
    }
}
