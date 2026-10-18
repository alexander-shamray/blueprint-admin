using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class BrokerServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private static readonly string Runbooks = new AdminOptions().BackendRunbooksUrl;
    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public BrokerServiceTests()
    {
        runner = new FakeProcessRunner(new JobRegistry(time));
    }

    private BrokerService Service => new(new ComposeService(runner, Paths, time), Options.Create(new AdminOptions()));

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
    public async Task Queues_are_listed_by_name_with_error_queues_marked_and_their_consumer_named()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"ordering-commands","messages":0}""",
            """,{"name":"ordering-catalog-events_error","messages":1}""",
            """,{"name":"ordering-catalog-events","messages":3}""",
            """,{"name":"something-else","messages":0}""",
            "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeTrue();
        view.Queues.ShouldBe(
        [
            new BrokerQueue("ordering-catalog-events", 3, "Ordering's price projection"),
            new BrokerQueue(
                "ordering-catalog-events_error", 1, "Ordering's price projection", "error", $"{Runbooks}/error-queue.md"),
            new BrokerQueue("ordering-commands", 0, "Ordering's command consumers"),
            new BrokerQueue("something-else", 0, null),
        ]);
    }

    [Fact]
    public async Task A_skipped_queue_is_named_by_the_endpoint_it_was_skipped_from_and_given_its_own_runbook()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"bff-order-events_skipped","messages":2}""",
            """,{"name":"unknown-endpoint_skipped","messages":1}""",
            "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Queues.ShouldBe(
        [
            new BrokerQueue("bff-order-events_skipped", 2, "the BFF's order projection", "skipped", $"{Runbooks}/skipped-queue.md"),
            new BrokerQueue("unknown-endpoint_skipped", 1, null, "skipped", $"{Runbooks}/skipped-queue.md"),
        ]);
    }

    [Fact]
    public async Task The_runbook_base_is_the_configured_one_whatever_its_trailing_slash()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"a_skipped","messages":1}""", "]");
        BrokerService service = new(
            new ComposeService(runner, Paths, time), Options.Create(new AdminOptions { BackendRunbooksUrl = "https://example.test/rb/" }));

        QueuesView view = await service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Queues.Single().Runbook.ShouldBe("https://example.test/rb/skipped-queue.md");
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
        BrokerService.IsDrained([new BrokerQueue("ordering-commands", 0)], "ordering-catalog-events")
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
    public async Task A_queue_row_missing_name_is_unreachable_not_an_exception()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"messages":3}""", "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("could not be parsed");
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

    // run-locally.md's line names five columns, and with --formatter json each row is keyed by them.
    [Fact]
    public async Task Bindings_are_listed_by_source_with_the_default_exchange_first()
    {
        runner.On("docker", Exec + "list_bindings", 0,
            "[",
            """{"source_name":"ordering-catalog-events","source_kind":"exchange","destination_name":"ordering-catalog-events","destination_kind":"queue","routing_key":""}""",
            """,{"source_name":"","source_kind":"exchange","destination_name":"ordering-catalog-events","destination_kind":"queue","routing_key":"ordering-catalog-events"}""",
            """,{"source_name":"Common.Contracts.Catalog.V1:PriceChanged","source_kind":"exchange","destination_name":"ordering-catalog-events","destination_kind":"exchange","routing_key":""}""",
            "]");

        BindingsView view = await Service.BindingsAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.TakeLast(8).ShouldBe(
            ["list_bindings", "source_name", "source_kind", "destination_name", "destination_kind", "routing_key", "--formatter", "json"]);
        view.Reachable.ShouldBeTrue();
        view.Bindings.ShouldBe(
        [
            new BrokerBinding("", "exchange", "ordering-catalog-events", "queue", "ordering-catalog-events"),
            new BrokerBinding("Common.Contracts.Catalog.V1:PriceChanged", "exchange", "ordering-catalog-events", "exchange", ""),
            new BrokerBinding("ordering-catalog-events", "exchange", "ordering-catalog-events", "queue", ""),
        ]);
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
