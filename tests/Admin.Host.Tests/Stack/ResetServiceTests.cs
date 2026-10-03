using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class ResetServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Prefix = $"compose -f {Paths.ComposeFile} ";

    private readonly FakeTimeProvider time = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner runner;

    public ResetServiceTests()
    {
        registry = new JobRegistry(time);
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task A_reset_runs_down_v_then_up_then_reports_every_surface_ready()
    {
        runner.On("docker", Prefix + "down -v", 0, " Volume commerce_sql-data  Removed")
            .On("docker", Prefix + "up -d --wait", 0, " Container commerce-gateway-1  Healthy");

        Job reset = Service(Ready("gateway", "catalog", "client")).Start();
        int exitCode = await reset.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        Texts(reset).ShouldBe(
        [
            $"$ docker {Prefix}down -v",
            " Volume commerce_sql-data  Removed",
            $"$ docker {Prefix}up -d --wait",
            " Container commerce-gateway-1  Healthy",
            "ready: gateway, catalog",
        ]);
        runner.Started.Select(s => s.Arguments[^1]).ShouldBe(["-v", "--wait"]);
    }

    [Fact]
    public async Task A_failed_down_ends_the_reset_before_up()
    {
        runner.OnFailing("docker", Prefix + "down -v", 1, "Error response from daemon: no such volume");

        Job reset = Service(Ready("gateway")).Start();
        int exitCode = await reset.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        runner.Started.ShouldHaveSingleItem();
        Texts(reset)[^1].ShouldEndWith("exited with 1; the reset stops here.");
    }

    [Fact]
    public async Task The_wait_names_what_it_is_still_waiting_for_until_it_answers()
    {
        runner.On("docker", Prefix + "down -v", 0).On("docker", Prefix + "up -d --wait", 0);
        int polls = 0;

        Job reset = Service(_ =>
        {
            polls++;
            return Task.FromResult<IReadOnlyList<Reachability>>(
                [new("gateway", "", true, 200), new("ordering", "", polls > 1, polls > 1 ? 200 : 503)]);
        }).Start();

        await WaitUntilAsync(() => Texts(reset).Contains("waiting for: ordering"));
        time.Advance(ResetService.ReadinessInterval);
        int exitCode = await reset.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        Texts(reset)[^1].ShouldBe("ready: gateway, ordering");
    }

    [Fact]
    public async Task The_wait_gives_up_at_its_cap_and_names_what_never_answered()
    {
        runner.On("docker", Prefix + "down -v", 0).On("docker", Prefix + "up -d --wait", 0);

        Job reset = Service(_ => Task.FromResult<IReadOnlyList<Reachability>>([new("ordering", "", false, null)])).Start();

        int seen = 0;

        while (true)
        {
            await WaitUntilAsync(() => reset.Status.State == JobState.Exited || Waits(reset) > seen);

            if (reset.Status.State == JobState.Exited)
            {
                break;
            }

            seen = Waits(reset);
            time.Advance(ResetService.ReadinessInterval);
        }

        reset.Status.ExitCode.ShouldBe(1);
        Texts(reset)[^1].ShouldBe($"not ready after {ResetService.ReadinessCap.TotalSeconds:0} s: ordering");
    }

    [Fact]
    public void A_second_reset_while_one_runs_returns_the_running_one()
    {
        runner.OnLongRunning("docker", Prefix + "down -v");
        ResetService service = Service(Ready("gateway"));

        Job first = service.Start();

        service.Start().ShouldBeSameAs(first);
    }

    private static int Waits(Job reset) => Texts(reset).Count(t => t.StartsWith("waiting for:", StringComparison.Ordinal));

    private ResetService Service(Func<CancellationToken, Task<IReadOnlyList<Reachability>>> probe) =>
        new(new ComposeService(runner, Paths, time), probe, registry, Paths, time);

    private static Func<CancellationToken, Task<IReadOnlyList<Reachability>>> Ready(params string[] names) =>
        _ => Task.FromResult<IReadOnlyList<Reachability>>([.. names.Select(n => new Reachability(n, "", true, 200))]);

    private static List<string> Texts(Job job) => [.. job.Since(-1).Select(l => l.Text)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the reset never reached the awaited state");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class ResetEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Reset_without_the_typed_confirmation_is_refused()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/reset", new { confirm = "yes" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().ShouldBe("Confirmation required");
    }

    [Fact]
    public async Task Reset_with_the_typed_confirmation_starts_one_listed_job()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/stack/backend/reset", new { confirm = "down -v" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldBe("reset down -v up -d --wait readiness");

        HttpResponseMessage listed = await client.GetAsync($"/api/jobs/{job.GetProperty("id").GetString()}", TestContext.Current.CancellationToken);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
