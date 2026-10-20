using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Platform;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Platform;

public sealed class PlatformMapServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public PlatformMapServiceTests()
    {
        runner = new FakeProcessRunner(new JobRegistry(time));
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    private PlatformMapService Map()
    {
        IOptions<AdminOptions> options = Options.Create(new AdminOptions());

        return new PlatformMapService(new BrokerService(new ComposeService(runner, Paths, time), options), options);
    }

    [Fact]
    public async Task A_queue_on_the_broker_in_no_table_and_a_table_queue_the_broker_lacks_are_both_disagreements()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"ordering-catalog-events","messages":4}""",
            """,{"name":"ordering-catalog-events_error","messages":1}""",
            """,{"name":"mystery-queue","messages":9}""",
            "]");
        runner.On("docker", Exec + "list_bindings", 0,
            "[",
            """{"source_name":"","source_kind":"exchange","destination_name":"ordering-catalog-events","destination_kind":"queue","routing_key":"ordering-catalog-events"}""",
            """,{"source_name":"Common.Contracts.Catalog.V1:PriceChanged","source_kind":"exchange","destination_name":"ordering-catalog-events","destination_kind":"queue","routing_key":""}""",
            "]");

        PlatformMapView view = await Map().ReadAsync(TestContext.Current.CancellationToken);

        view.BrokerReachable.ShouldBeTrue();
        view.Disagreements.ShouldContain("mystery-queue is on the broker but in no service's queue table.");
        view.Disagreements.ShouldContain("ordering-commands is in the console's queue table but the broker does not list it.");
        view.Disagreements.ShouldNotContain(d => d.StartsWith("ordering-catalog-events", StringComparison.Ordinal));
        MapQueue queue = view.Services.Single(s => s.Name == "ordering").Queues.Single(q => q.Name == "ordering-catalog-events");
        queue.Messages.ShouldBe(4);
        queue.Bindings.ShouldBe([new MapBinding("Common.Contracts.Catalog.V1:PriceChanged", string.Empty)]);
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_is_a_state_and_keeps_every_service_on_the_map()
    {
        runner.On("docker", Exec + "list_queues", 1, "Error: unreachable");
        runner.On("docker", Exec + "list_bindings", 1, "Error: unreachable");

        PlatformMapView view = await Map().ReadAsync(TestContext.Current.CancellationToken);

        view.BrokerReachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull();
        view.Services.Select(s => s.Name).ShouldBe(PlatformServices.All.Select(s => s.Name));
        view.Disagreements.ShouldBeEmpty();
    }
}
