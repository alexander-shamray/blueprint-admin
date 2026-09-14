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
    public async Task Disposing_the_runner_marks_its_running_jobs_exited()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .OnLongRunning("docker", "compose -f x logs", "gateway | started");

        Job job = runner.Start(new ProcessSpec("docker", ["compose", "-f", "x", "logs", "-f"], "/b"));
        job.State.ShouldBe(JobState.Running);

        await runner.DisposeAsync();

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public void Concurrent_starts_record_every_spec()
    {
        // One runner is a singleton that concurrent requests share. 1000 rather than 50
        // so that an unsynchronised List<T>.Add loses entries on most runs.
        const int Starts = 1000;
        FakeProcessRunner runner = new FakeProcessRunner(registry).On("docker", "compose", 0, "ok");

        Parallel.For(0, Starts, i => runner.Start(new ProcessSpec("docker", ["compose", $"{i}"], "/b")));

        runner.Started.Count.ShouldBe(Starts);
        runner.Started.Select(s => s.Arguments[1]).Distinct().Count().ShouldBe(Starts);
    }

    [Fact]
    public void Started_is_a_snapshot_that_later_starts_do_not_change()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry).On("docker", "compose", 0, "ok");
        runner.Start(new ProcessSpec("docker", ["compose", "ps"], "/b"));

        IReadOnlyList<ProcessSpec> before = runner.Started;
        runner.Start(new ProcessSpec("docker", ["compose", "up"], "/b"));

        before.Count.ShouldBe(1);
        runner.Started.Count.ShouldBe(2);
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
