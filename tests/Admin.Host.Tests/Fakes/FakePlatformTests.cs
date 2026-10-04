using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Broker;
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

    /// <summary>
    /// The ps fixture and the recorded Compose model describe one stack, or the doctor would read a port Docker holds
    /// for this stack as a stranger's.
    /// </summary>
    [Fact]
    public void The_ps_fixture_lists_every_service_of_the_recorded_compose_model_and_no_other()
    {
        using JsonDocument model = JsonDocument.Parse(WorkstationRecordings.ComposeConfig);
        string[] modelled = [.. model.RootElement.GetProperty("services").EnumerateObject().Select(s => s.Name).Order(StringComparer.Ordinal)];

        string[] listed = [.. FakePlatformScripts.ComposePsLines()
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("Service").GetString()!)
            .Order(StringComparer.Ordinal)];

        listed.ShouldBe(modelled);
    }

    /// <summary>
    /// The queue recording holds every queue the Broker screen gives a row. The table is held to the backend by
    /// QueueDriftTests, so a queue the backend adds fails here until the fakes are re-recorded.
    /// </summary>
    [Fact]
    public void The_queue_recording_lists_every_platform_queue()
    {
        string[] recorded = [.. JsonDocument.Parse(string.Join('\n', FakePlatformScripts.FixtureLines("rabbitmq-queues.json")))
            .RootElement.EnumerateArray().Select(q => q.GetProperty("name").GetString()!)];

        PlatformQueues.Table.Select(q => q.Name).Where(q => !recorded.Contains(q)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_surface_is_reachable_in_fake_mode()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

        stack.GetProperty("reachability").EnumerateArray().ShouldAllBe(r => r.GetProperty("up").GetBoolean());
        stack.GetProperty("backend").GetProperty("services").EnumerateArray().Count().ShouldBe(FakePlatformScripts.ComposePsLines().Count);
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

    // The recorded logs -f output, by Compose service prefix: gateway 3,
    // catalog-api 4, ordering-api 3, web-bff 1, rabbitmq 1; twelve in all.
    [Fact]
    public async Task Logs_follow_of_no_services_replays_every_recorded_line()
    {
        IReadOnlyList<string> lines = await FollowAsync([]);

        lines.Count.ShouldBe(12);
    }

    [Fact]
    public async Task Logs_follow_replays_only_the_selected_services()
    {
        IReadOnlyList<string> lines = await FollowAsync(["gateway", "ordering-api"]);

        // gateway 3 + ordering-api 3.
        lines.Count.ShouldBe(6);
        lines.ShouldAllBe(t => t.StartsWith("gateway ", StringComparison.Ordinal) || t.StartsWith("ordering-api ", StringComparison.Ordinal));
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

    [Fact]
    public async Task Stack_and_broker_reads_are_not_listed_among_the_jobs()
    {
        await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);
        await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        JsonElement jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs", TestContext.Current.CancellationToken);

        jobs.EnumerateArray().Select(j => j.GetProperty("commandLine").GetString()!)
            .ShouldAllBe(c => !c.Contains(" ps ") && !c.Contains(" exec "));
    }

    private async Task<IReadOnlyList<string>> FollowAsync(string[] services)
    {
        HttpResponseMessage started = await client.PostAsJsonAsync("/api/logs/follow", new { services }, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;
        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

        return [.. job.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!)];
    }
}
