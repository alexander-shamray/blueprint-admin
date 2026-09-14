using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakePlatformTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public FakePlatformTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public void The_fixture_lists_thirteen_services()
    {
        FakePlatformScripts.ComposePsLines().Count.ShouldBe(13);
    }

    [Fact]
    public async Task Every_surface_is_reachable_in_fake_mode()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

        stack.GetProperty("reachability").EnumerateArray().ShouldAllBe(r => r.GetProperty("up").GetBoolean());
        stack.GetProperty("backend").GetProperty("services").EnumerateArray().Count().ShouldBe(13);
    }

    [Fact]
    public async Task Up_produces_output_and_exits_zero()
    {
        HttpResponseMessage started = await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

        job.GetProperty("summary").GetProperty("exitCode").GetInt32().ShouldBe(0);
        job.GetProperty("lines").EnumerateArray().Count().ShouldBe(6);
    }

    [Fact]
    public async Task Logs_follow_carries_a_correlation_id_line()
    {
        HttpResponseMessage started = await client.PostAsJsonAsync("/api/logs/follow", new { services = Array.Empty<string>() }, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

        job.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Running");
        job.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ShouldContain(t => t.Contains("CorrelationId=fake-corr-0001"));
    }
}
