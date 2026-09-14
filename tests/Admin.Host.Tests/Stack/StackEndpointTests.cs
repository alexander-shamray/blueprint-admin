using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class StackEndpointTests : IClassFixture<AdminHostFactory>
{
    private static readonly string[] GatewayOnly = ["gateway"];

    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public StackEndpointTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private FakeProcessRunner Runner => factory.Services.GetRequiredService<FakeProcessRunner>();

    [Fact]
    public async Task Stack_reports_compose_services_and_reachability()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

        stack.GetProperty("backend").GetProperty("reachable").GetBoolean().ShouldBeTrue();
        stack.GetProperty("backend").GetProperty("services").EnumerateArray()
            .Select(s => s.GetProperty("service").GetString()).ShouldContain("gateway");
        stack.GetProperty("reachability").EnumerateArray().Count().ShouldBe(7);
    }

    [Fact]
    public async Task Up_starts_a_compose_up_job()
    {
        HttpResponseMessage response = await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldEndWith("up -d --wait");
        Runner.Started.ShouldContain(s => s.Arguments.Contains("up"));
    }

    [Fact]
    public async Task Down_without_wipe_needs_no_confirmation()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = false }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Runner.Started[^1].Arguments.ShouldNotContain("-v");
    }

    [Fact]
    public async Task Wiping_volumes_without_the_typed_confirmation_is_refused()
    {
        // R5: FakeProcessRunner is shared across the fixture, and xunit v3 does
        // not guarantee method order, so the sibling test below can already have
        // recorded a "-v" spec. Assert this request adds none, rather than that
        // none exists.
        int wipeSpecsBefore = Runner.Started.Count(s => s.Arguments.Contains("-v"));

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = true, confirm = "yes" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().ShouldBe("Confirmation required");
        Runner.Started.Count(s => s.Arguments.Contains("-v")).ShouldBe(wipeSpecsBefore);
    }

    [Fact]
    public async Task Wiping_volumes_with_the_typed_confirmation_runs_down_v()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/down", new { wipeVolumes = true, confirm = "down -v" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Runner.Started[^1].Arguments.TakeLast(2).ShouldBe(["down", "-v"]);
    }

    [Fact]
    public async Task Follow_logs_starts_a_running_job_for_the_named_services()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/logs/follow", new { services = GatewayOnly }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("state").GetString().ShouldBe("Running");
        job.GetProperty("commandLine").GetString().ShouldEndWith("logs -f --tail 200 gateway");
    }
}
