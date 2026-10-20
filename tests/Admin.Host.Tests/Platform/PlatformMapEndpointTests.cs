using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Platform;

public sealed class PlatformMapEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public PlatformMapEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    private async Task<JsonElement> MapAsync() =>
        await client.GetFromJsonAsync<JsonElement>("/api/platform/map", TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_map_has_a_node_for_every_platform_service_in_the_records_order()
    {
        JsonElement map = await MapAsync();

        map.GetProperty("brokerReachable").GetBoolean().ShouldBeTrue();
        map.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("name").GetString())
            .ShouldBe(PlatformServices.All.Select(s => s.Name));
    }

    [Fact]
    public async Task A_service_carries_the_routes_that_reach_it_and_the_queues_it_consumes_with_their_depth()
    {
        JsonElement map = await MapAsync();
        JsonElement ordering = map.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "ordering");

        ordering.GetProperty("routes").EnumerateArray().Single().GetProperty("prefix").GetString().ShouldBe("/api/v1/orders/");
        JsonElement[] queues = [.. ordering.GetProperty("queues").EnumerateArray()];
        queues.Select(q => q.GetProperty("name").GetString()).ShouldContain("ordering-catalog-events");
        JsonElement parked = queues.Single(q => q.GetProperty("name").GetString() == "ordering-catalog-events_error");
        parked.GetProperty("parked").GetString().ShouldBe("error");
        parked.GetProperty("messages").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task The_fake_platform_has_no_disagreement_between_the_broker_and_the_tables()
    {
        JsonElement map = await MapAsync();

        map.GetProperty("disagreements").EnumerateArray().Select(d => d.GetString()).ShouldBeEmpty();
    }
}
