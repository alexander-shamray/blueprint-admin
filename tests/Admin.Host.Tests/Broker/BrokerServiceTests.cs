using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class BrokerServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public BrokerServiceTests()
    {
        runner = new FakeProcessRunner(new JobRegistry(time));
    }

    private BrokerService Service => new(new ComposeService(runner, Paths, time));

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task Queues_run_list_queues_name_messages_as_json_inside_the_rabbitmq_container()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", "]");

        await Service.QueuesAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.ShouldBe(
            ["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues", "name", "messages", "--formatter", "json"]);
    }

    [Fact]
    public async Task Queues_are_listed_by_name_with_error_queues_marked()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"ordering-commands","messages":0}""",
            """,{"name":"ordering-catalog-events_error","messages":1}""",
            """,{"name":"ordering-catalog-events","messages":3}""",
            "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeTrue();
        view.Queues.ShouldBe(
        [
            new BrokerQueue("ordering-catalog-events", 3, false),
            new BrokerQueue("ordering-catalog-events_error", 1, true),
            new BrokerQueue("ordering-commands", 0, false),
        ]);
    }

    [Fact]
    public async Task The_projection_is_waiting_while_its_queue_holds_messages()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"ordering-catalog-events","messages":3}""", "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", true, 3, false));
    }

    [Fact]
    public async Task The_projection_is_drained_when_its_queue_holds_no_messages()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"ordering-catalog-events","messages":0}""", "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", true, 0, true));
    }

    [Fact]
    public void A_queue_that_is_not_declared_is_not_drained()
    {
        BrokerService.IsDrained([new BrokerQueue("ordering-commands", 0, false)], "ordering-catalog-events")
            .ShouldBe(new ProjectionDrain("ordering-catalog-events", false, null, false));
    }

    [Fact]
    public async Task Queues_are_unreachable_with_the_error_when_the_container_does_not_answer()
    {
        // No script: the fake exits 127 with a stderr line, as compose exec does for a stopped service.
        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldStartWith("fake: no script for docker compose");
        view.Queues.ShouldBeEmpty();
        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", false, null, false));
    }

    [Fact]
    public async Task Output_that_is_not_a_json_array_is_unreachable_not_an_exception()
    {
        runner.On("docker", Exec + "list_queues", 0, "Error: unable to perform an operation on node");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("rabbitmqctl list_queues output could not be parsed");
    }

    [Fact]
    public async Task Exchanges_are_listed_with_their_type_including_the_default_exchange()
    {
        runner.On("docker", Exec + "list_exchanges", 0,
            "[", """{"name":"amq.topic","type":"topic"}""", """,{"name":"","type":"direct"}""", "]");

        ExchangesView view = await Service.ExchangesAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.TakeLast(5).ShouldBe(["list_exchanges", "name", "type", "--formatter", "json"]);
        view.Reachable.ShouldBeTrue();
        view.Exchanges.ShouldBe([new BrokerExchange("", "direct"), new BrokerExchange("amq.topic", "topic")]);
    }

    [Fact]
    public async Task Permissions_are_listed_per_user()
    {
        runner.On("docker", Exec + "list_permissions", 0,
            "[",
            """{"user":"ordering-svc","configure":"^(ordering-)","write":"^(ordering-)","read":"^(ordering-)"}""",
            """,{"user":"catalog-svc","configure":"^(MassTransit:)","write":"^(MassTransit:)","read":"^(MassTransit:)"}""",
            "]");

        PermissionsView view = await Service.PermissionsAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.TakeLast(3).ShouldBe(["list_permissions", "--formatter", "json"]);
        view.Permissions.Select(p => p.User).ShouldBe(["catalog-svc", "ordering-svc"]);
        view.Permissions[0].ShouldBe(new BrokerPermission("catalog-svc", "^(MassTransit:)", "^(MassTransit:)", "^(MassTransit:)"));
    }
}
