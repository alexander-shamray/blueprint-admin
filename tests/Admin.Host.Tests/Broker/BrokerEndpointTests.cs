using System.Net.Http.Json;
using System.Text.Json;
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
        queues.Length.ShouldBe(5);
        JsonElement error = queues.Single(q => q.GetProperty("isErrorQueue").GetBoolean());
        error.GetProperty("name").GetString().ShouldBe("ordering-catalog-events_error");
        error.GetProperty("messages").GetInt64().ShouldBe(1);
        JsonElement projection = view.GetProperty("projection");
        projection.GetProperty("queue").GetString().ShouldBe("ordering-catalog-events");
        projection.GetProperty("drained").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Exchanges_lists_the_fake_brokers_exchanges_with_their_types()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/exchanges", TestContext.Current.CancellationToken);

        JsonElement[] exchanges = [.. view.GetProperty("exchanges").EnumerateArray()];
        exchanges.Length.ShouldBe(15);
        exchanges.ShouldContain(e => e.GetProperty("name").GetString() == "ordering-fulfilment-saga_delay" && e.GetProperty("type").GetString() == "x-delayed-message");
    }

    [Fact]
    public async Task Permissions_lists_the_two_service_users()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/permissions", TestContext.Current.CancellationToken);

        view.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("user").GetString()).ShouldBe(["catalog-svc", "ordering-svc"]);
    }
}
