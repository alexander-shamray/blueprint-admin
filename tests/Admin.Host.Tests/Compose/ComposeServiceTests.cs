using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Compose;

public sealed class ComposeServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private readonly FakeTimeProvider time = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner runner;

    public ComposeServiceTests()
    {
        registry = new JobRegistry(time);
        runner = new FakeProcessRunner(registry);
    }

    private ComposeService Service => new(runner, Paths, time);

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public void Up_runs_compose_up_detached_and_waits()
    {
        Service.Up();

        ProcessSpec spec = runner.Started.Single();
        spec.FileName.ShouldBe("docker");
        spec.Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "up", "-d", "--wait"]);
        spec.WorkingDirectory.ShouldBe("/repo/backend");
    }

    [Fact]
    public void Down_wipes_volumes_only_when_asked()
    {
        Service.Down(wipeVolumes: false);
        Service.Down(wipeVolumes: true);

        runner.Started[0].Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "down"]);
        runner.Started[1].Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "down", "-v"]);
    }

    [Fact]
    public void FollowLogs_tails_the_named_services()
    {
        Service.FollowLogs(["gateway", "web-bff"]);

        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "logs", "-f", "--tail", "200", "gateway", "web-bff"]);
    }

    [Fact]
    public void Exec_runs_inside_the_service_without_a_tty()
    {
        Service.Exec("rabbitmq", "rabbitmqctl", "list_queues");

        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues"]);
    }

    [Fact]
    public async Task Ps_parses_the_services_when_docker_answers()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps -a --format json", 0,
            """{"Name":"commerce-gateway-1","Service":"gateway","State":"running","Health":"healthy","ExitCode":0,"Publishers":[]}""");

        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeTrue();
        status.Services.Single().Service.ShouldBe("gateway");
    }

    [Fact]
    public async Task Ps_is_unreachable_with_the_last_stderr_line_when_docker_fails()
    {
        // The fake writes to stdout only; a non-zero exit with no stderr still reports the exit code.
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps", 1);

        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldBe("docker compose ps exited with 1");
        status.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ps_is_unreachable_when_docker_is_not_installed()
    {
        // No script: the fake exits 127 with a stderr line, the same shape as a missing executable.
        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldStartWith("fake: no script for docker compose");
    }

    [Fact]
    public async Task Ps_stops_the_job_when_the_caller_cancels_before_it_answers()
    {
        // A long-running script never exits on its own, the same shape as a
        // docker compose ps that never answers because the daemon is stuck.
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} ps");
        using CancellationTokenSource cts = new();

        Task<ComposeStatus> psTask = Service.PsAsync(cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => psTask);
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task Ps_stops_the_job_and_is_unreachable_when_it_does_not_answer_within_30_seconds()
    {
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} ps");

        Task<ComposeStatus> psTask = Service.PsAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(29));
        psTask.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        ComposeStatus status = await psTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldNotBeNull().ShouldContain("did not answer within 30 seconds");
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task Ps_is_unreachable_when_the_output_cannot_be_parsed()
    {
        // A warning line on stdout ahead of the JSON rows, or truncated output,
        // is not valid JSON; the parser throws and PsAsync must not propagate it.
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps -a --format json", 0,
            """{"Name":"commerce-gateway-1","Service":"gateway","State":"running","Health":"healthy","ExitCode":0,"Publishers":[]}""",
            "WARN[0000] something");

        ComposeStatus status = await Service.PsAsync(TestContext.Current.CancellationToken);

        status.Reachable.ShouldBeFalse();
        status.Error.ShouldNotBeNull().ShouldContain("could not be parsed");
    }

    [Fact]
    public async Task ExecAsync_returns_the_stdout_lines_when_the_command_exits_zero()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl list_queues", 0, "[", "]");

        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldBeNull();
        output.Stdout.ShouldBe(["[", "]"]);
        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues"]);
    }

    [Fact]
    public async Task ExecAsync_fails_with_the_exit_code_when_the_command_wrote_no_stderr()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq", 1);

        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldBe("docker compose exec rabbitmq exited with 1");
        output.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecAsync_fails_with_the_last_stderr_line_when_the_command_cannot_start()
    {
        // No script: the fake exits 127 with a stderr line, the shape of a service that is not running.
        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldNotBeNull().ShouldStartWith("fake: no script for docker compose");
    }

    [Fact]
    public async Task ExecAsync_stops_the_job_and_fails_when_it_does_not_answer_within_30_seconds()
    {
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq");

        Task<CommandOutput> execTask = Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        CommandOutput output = await execTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        output.Error.ShouldBe("docker compose exec rabbitmq did not answer within 30 seconds");
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task ExecAsync_stops_the_job_when_the_caller_cancels()
    {
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq");
        using CancellationTokenSource cts = new();

        Task<CommandOutput> execTask = Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => execTask);
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }
}
