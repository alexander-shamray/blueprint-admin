using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Compose;

public sealed class ComposeServiceTests
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private readonly FakeProcessRunner runner = new(new JobRegistry(new FakeTimeProvider()));

    private ComposeService Service => new(runner, Paths);

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
}
