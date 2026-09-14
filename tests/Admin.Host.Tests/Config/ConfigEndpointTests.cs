using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class ConfigEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public ConfigEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Reports_the_resolved_paths_and_urls()
    {
        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/api/config", TestContext.Current.CancellationToken);

        body.GetProperty("fakePlatform").GetBoolean().ShouldBeTrue();
        body.GetProperty("backendDir").GetString().ShouldEndWith("blueprint-backend");
        body.GetProperty("urls").GetProperty("gateway").GetString().ShouldBe("http://localhost:5000");
        body.GetProperty("urls").GetProperty("client").GetString().ShouldBe("http://localhost:5173");
    }
}
