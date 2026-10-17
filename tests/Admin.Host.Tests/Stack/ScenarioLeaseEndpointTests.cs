using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class ScenarioLeaseEndpointTests
{
    private static readonly string[] Both = ["short-waits", "stop-inventory"];
    private static readonly string[] ShortWaitsOnly = ["short-waits"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Acquire(HttpClient client, string change = "stop-inventory", int seconds = 120) =>
        client.PostAsJsonAsync("/api/stack/lease", new { changes = new[] { change }, holdSeconds = seconds, label = "a run" }, Token);

    [Fact]
    public async Task The_table_of_changes_is_listed_with_no_lease_held()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonElement state = await client.GetFromJsonAsync<JsonElement>("/api/stack/lease", Token);

        state.GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
        state.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ShouldBe(["stop-inventory", "short-waits"]);
    }

    [Fact]
    public async Task A_lease_is_held_shows_on_the_stack_blocks_up_and_restores_on_request()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage acquired = await Acquire(client);

        acquired.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement lease = await acquired.Content.ReadFromJsonAsync<JsonElement>(Token);
        lease.GetProperty("services").EnumerateArray().Select(s => s.GetString()).ShouldBe(["inventory-api"]);

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);
        stack.GetProperty("lease").GetProperty("id").GetString().ShouldBe(lease.GetProperty("id").GetString());

        HttpResponseMessage up = await client.PostAsync("/api/stack/backend/up", null, Token);
        up.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await up.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("detail").GetString()!.ShouldContain("holds a lease");

        HttpResponseMessage second = await Acquire(client, "short-waits");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        HttpResponseMessage restored = await client.PostAsync("/api/stack/lease/restore", null, Token);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await restored.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);

        (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
        (await client.PostAsync("/api/stack/backend/up", null, Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_lease_of_two_changes_is_held_as_one_and_changes_what_a_fake_order_does_until_it_is_restored()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage acquired = await client.PostAsJsonAsync(
            "/api/stack/lease", new { changes = Both, holdSeconds = 120 }, Token);

        acquired.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement lease = await acquired.Content.ReadFromJsonAsync<JsonElement>(Token);
        lease.GetProperty("services").EnumerateArray().Select(s => s.GetString()).ShouldBe(["ordering-api", "inventory-api"]);

        (await client.PostAsync("/api/stack/lease/restore", null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static Task<HttpResponseMessage> AddChange(HttpClient client, string change) =>
        client.PostAsJsonAsync("/api/stack/lease/changes", new { change }, Token);

    [Fact]
    public async Task A_change_is_added_to_a_held_lease_and_both_are_restored_together()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        JsonElement acquired = await (await client.PostAsJsonAsync(
            "/api/stack/lease", new { changes = ShortWaitsOnly, holdSeconds = 120 }, Token)).Content.ReadFromJsonAsync<JsonElement>(Token);

        HttpResponseMessage added = await AddChange(client, "stop-inventory");

        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement lease = await added.Content.ReadFromJsonAsync<JsonElement>(Token);
        lease.GetProperty("id").GetString().ShouldBe(acquired.GetProperty("id").GetString());
        lease.GetProperty("changes").EnumerateArray().Select(c => c.GetString()).ShouldBe(["short-waits", "stop-inventory"]);
        lease.GetProperty("expiresAt").GetString().ShouldBe(acquired.GetProperty("expiresAt").GetString());

        (await client.PostAsync("/api/stack/lease/restore", null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_change_off_the_table_or_already_held_is_a_400()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        (await Acquire(client)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AddChange(client, "nothing-else")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AddChange(client, "stop-inventory")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/stack/lease/changes", new { }, Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_change_added_with_no_lease_held_is_a_409()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage added = await AddChange(client, "stop-inventory");

        added.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await added.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("detail").GetString()!.ShouldContain("No lease is held");
    }

    [Fact]
    public async Task A_restore_with_nothing_held_is_a_200_with_no_lease()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage restored = await client.PostAsync("/api/stack/lease/restore", null, Token);

        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> FailableHost(AdminHostFactory factory, out FailingProcessRunner runner)
    {
        FailingProcessRunner? failing = null;
        WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IProcessRunner>(sp => failing = new FailingProcessRunner(sp.GetRequiredService<FakeProcessRunner>(), sp.GetRequiredService<JobRegistry>()))));
        _ = host.Server;
        runner = failing!;

        return host;
    }

    private static bool Stops(ProcessSpec spec) => spec.Arguments.Contains("stop");

    private static bool StopsOrBringsUp(ProcessSpec spec) => Stops(spec) || (spec.Arguments.Contains("up") && spec.Arguments.Contains("--wait"));

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task An_acquire_whose_change_fails_is_a_500_that_says_the_changes_were_undone()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> host = FailableHost(factory, out FailingProcessRunner runner);
        HttpClient client = host.CreateClient();
        runner.FailIf = Stops;

        JsonElement problem = await ProblemOf(await Acquire(client));

        problem.GetProperty("title").GetString().ShouldBe("The change failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("The changes were undone.");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack/lease", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_acquire_whose_change_and_undo_both_fail_is_a_500_that_says_the_lease_is_still_held()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> host = FailableHost(factory, out FailingProcessRunner runner);
        HttpClient client = host.CreateClient();
        runner.FailIf = StopsOrBringsUp;

        JsonElement problem = await ProblemOf(await Acquire(client));

        problem.GetProperty("title").GetString().ShouldBe("The change failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("The lease is still held");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack/lease", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task An_added_change_that_fails_is_a_500_that_says_the_changes_were_undone()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> host = FailableHost(factory, out FailingProcessRunner runner);
        HttpClient client = host.CreateClient();
        (await Acquire(client, "short-waits")).StatusCode.ShouldBe(HttpStatusCode.OK);
        runner.FailIf = Stops;

        JsonElement problem = await ProblemOf(await AddChange(client, "stop-inventory"));

        problem.GetProperty("title").GetString().ShouldBe("The change failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("The changes were undone.");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack/lease", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_added_change_and_its_undo_that_both_fail_are_a_500_that_says_the_lease_is_still_held()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> host = FailableHost(factory, out FailingProcessRunner runner);
        HttpClient client = host.CreateClient();
        (await Acquire(client, "short-waits")).StatusCode.ShouldBe(HttpStatusCode.OK);
        runner.FailIf = StopsOrBringsUp;

        JsonElement problem = await ProblemOf(await AddChange(client, "stop-inventory"));

        problem.GetProperty("title").GetString().ShouldBe("The change failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("The lease is still held");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack/lease", Token)).GetProperty("lease").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Theory]
    [InlineData("nothing-else", 60)]
    [InlineData("stop-inventory", 0)]
    [InlineData("stop-inventory", 99999)]
    public async Task A_change_off_the_table_and_a_hold_off_its_bounds_are_a_400(string change, int seconds)
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        (await Acquire(client, change, seconds)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
