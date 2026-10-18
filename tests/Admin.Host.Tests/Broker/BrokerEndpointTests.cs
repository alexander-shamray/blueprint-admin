using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Broker;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class BrokerEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public BrokerEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Queues_lists_the_fake_brokers_queues_with_the_error_queue_marked_and_the_projection_drained()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement[] queues = [.. view.GetProperty("queues").EnumerateArray()];
        queues.Length.ShouldBe(PlatformQueues.Table.Length + 2);
        JsonElement error = queues.Single(q => q.GetProperty("parked").GetString() == "error");
        error.GetProperty("name").GetString().ShouldBe("ordering-catalog-events_error");
        error.GetProperty("messages").GetInt64().ShouldBe(1);
        error.GetProperty("parked").GetString().ShouldBe("error");
        error.GetProperty("runbook").GetString().ShouldEndWith("/docs/runbooks/error-queue.md");
        error.GetProperty("consumer").GetString().ShouldBe("Ordering's price projection");
        JsonElement projection = view.GetProperty("projection");
        projection.GetProperty("queue").GetString().ShouldBe("ordering-catalog-events");
        projection.GetProperty("drained").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Queues_names_the_endpoint_a_skipped_message_was_skipped_from_and_links_its_runbook()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        JsonElement skipped = view.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("name").GetString() == "bff-order-events_skipped");
        skipped.GetProperty("messages").GetInt64().ShouldBe(1);
        skipped.GetProperty("parked").GetString().ShouldBe("skipped");
        skipped.GetProperty("consumer").GetString().ShouldBe("the BFF's order projection");
        skipped.GetProperty("runbook").GetString().ShouldEndWith("/docs/runbooks/skipped-queue.md");
    }

    [Fact]
    public async Task Queues_gives_a_queue_that_is_not_parked_no_kind_and_no_runbook()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        JsonElement live = view.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("name").GetString() == PlatformQueues.Projection);
        live.GetProperty("parked").ValueKind.ShouldBe(JsonValueKind.Null);
        live.GetProperty("runbook").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Exchanges_lists_the_fake_brokers_exchanges_with_their_types()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/exchanges", TestContext.Current.CancellationToken);

        JsonElement[] exchanges = [.. view.GetProperty("exchanges").EnumerateArray()];
        exchanges.Length.ShouldBe(56);
        exchanges.ShouldContain(e => e.GetProperty("name").GetString() == "ordering-fulfilment-saga_delay" && e.GetProperty("type").GetString() == "x-delayed-message");
    }

    // The two bindings run-locally.md names for Catalog's price changes reaching Ordering, both from fanout exchanges.
    [Fact]
    public async Task Bindings_lists_the_fake_brokers_bindings_with_the_two_run_locally_names()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/bindings", TestContext.Current.CancellationToken);

        JsonElement[] bindings = [.. view.GetProperty("bindings").EnumerateArray()];
        bindings.Length.ShouldBe(102);
        string[] rows = [.. bindings.Select(b =>
            $"{b.GetProperty("source").GetString()} -> {b.GetProperty("destination").GetString()} ({b.GetProperty("destinationKind").GetString()}) '{b.GetProperty("routingKey").GetString()}'")];
        rows.ShouldContain("Common.Contracts.Catalog.V1:PriceChanged -> ordering-catalog-events (exchange) ''");
        rows.ShouldContain("ordering-catalog-events -> ordering-catalog-events (queue) ''");
    }

    [Fact]
    public async Task Permissions_lists_each_service_user_the_broker_answered()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/permissions", TestContext.Current.CancellationToken);

        view.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("user").GetString()).ShouldBe(
            [
                "bff-svc", "catalog-svc", "dead-letter-operator", "inventory-svc", "notifications-svc", "ordering-svc",
                "payments-svc", "privacy-svc", "shipping-svc",
            ]);
    }
}
