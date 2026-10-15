using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Privacy;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Privacy;

public sealed class ErasureEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public ErasureEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Holders_lists_every_holder_with_its_queue_depth_and_the_runbook()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/erasure/holders", TestContext.Current.CancellationToken);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement[] holders = [.. view.GetProperty("holders").EnumerateArray()];
        holders.Select(h => h.GetProperty("name").GetString()).ShouldBe([.. ErasureHolders.Table.Select(h => h.Name)]);
        holders.Select(h => h.GetProperty("queue").GetString()).ShouldBe([.. ErasureHolders.Table.Select(h => h.Queue)]);
        holders.ShouldAllBe(h => h.GetProperty("pending").GetInt64() == 0);
        view.GetProperty("completionsPending").GetInt64().ShouldBe(0);
        view.GetProperty("runbookUrl").GetString().ShouldEndWith("/docs/runbooks/erasure-overdue.md");
    }

    [Fact]
    public async Task The_proxy_reaches_privacy_through_the_gateway_as_the_privacy_login()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/proxy",
            new
            {
                method = "POST",
                url = "http://localhost:5000/api/v1/privacy/erasure-requests",
                body = $$"""{"subjectId":"{{Guid.NewGuid()}}"}""",
                identity = new { username = "privacy" },
            },
            TestContext.Current.CancellationToken);

        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBe(200);
    }
}
