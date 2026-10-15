using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class ProjectionRebuildServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Prefix = $"compose -f {Paths.ComposeFile} ";
    private static readonly string ListQueues = Prefix + "exec -T rabbitmq rabbitmqctl list_queues name messages --formatter json";
    private const string Tool = "run --project tools/bff-replay";

    private readonly FakeTimeProvider time = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner runner;

    public ProjectionRebuildServiceTests()
    {
        registry = new JobRegistry(time);
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task A_repair_runs_the_tool_alone_in_the_backend_clone_with_the_six_connections_and_then_watches_the_queue()
    {
        runner.On("dotnet", Tool, 0, "Sent 0 event(s) to bff-order-events.").On("docker", ListQueues, 0, Queues(0));

        Job job = Service().Start(RebuildMode.Repair);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        Texts(job).ShouldBe(
        [
            "$ dotnet run --project tools/bff-replay",
            "Sent 0 event(s) to bff-order-events.",
            "bff-order-events: 0 messages (drained). Trust the projection now.",
        ]);

        ProcessSpec spec = runner.Started.Single(s => s.FileName == "dotnet");
        spec.WorkingDirectory.ShouldBe(Paths.BackendDir);
        spec.Environment!.Keys.ShouldBe(
        [
            "ConnectionStrings__Bff",
            "ConnectionStrings__RabbitMq",
            "ConnectionStrings__CatalogOutbox",
            "ConnectionStrings__OrderingOutbox",
            "ConnectionStrings__PaymentsOutbox",
            "ConnectionStrings__ShippingOutbox",
        ]);
        runner.Started.Any(s => s.Arguments.Contains("stop")).ShouldBeFalse("a repair leaves the BFF running");
    }

    [Fact]
    public async Task A_rebuild_stops_the_bff_then_runs_the_tool_with_reset_then_starts_the_bff_then_watches_the_queue()
    {
        runner.On("docker", Prefix + "stop web-bff", 0, "Stopped")
            .On("dotnet", Tool + " -- --reset", 0, "Reset: deleted")
            .On("docker", Prefix + "start web-bff", 0, "Started")
            .On("docker", ListQueues, 0, Queues(0));

        Job job = Service().Start(RebuildMode.Rebuild);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        Texts(job).ShouldBe(
        [
            $"$ docker {Prefix}stop web-bff",
            "Stopped",
            "$ dotnet run --project tools/bff-replay -- --reset",
            "Reset: deleted",
            $"$ docker {Prefix}start web-bff",
            "Started",
            "bff-order-events: 0 messages (drained). Trust the projection now.",
        ]);
    }

    [Fact]
    public async Task A_bff_that_will_not_stop_ends_the_rebuild_before_the_tool_deletes_anything()
    {
        runner.OnFailing("docker", Prefix + "stop web-bff", 1, "no such service").On("dotnet", Tool + " -- --reset", 0, "Reset: deleted");

        Job job = Service().Start(RebuildMode.Rebuild);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        runner.Started.Select(s => s.FileName).ShouldBe(["docker"]);
    }

    [Fact]
    public async Task A_tool_that_fails_still_has_the_bff_started_again_and_the_run_exits_non_zero_without_watching()
    {
        runner.On("docker", Prefix + "stop web-bff", 0)
            .OnFailing("dotnet", Tool + " -- --reset", 3, "SqlException")
            .On("docker", Prefix + "start web-bff", 0, "Started")
            .On("docker", ListQueues, 0, Queues(0));

        Job job = Service().Start(RebuildMode.Rebuild);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        runner.Started.Select(s => s.Arguments[^1]).ShouldBe(["web-bff", "--reset", "web-bff"]);
        Texts(job).ShouldContain("The tool did not finish; the BFF is started again and the projection may be partial. Read the tool's lines above before another run.");
        Texts(job).ShouldNotContain(t => t.Contains("drained", StringComparison.Ordinal));
        runner.Started.Any(s => s.Arguments.Contains("rabbitmqctl")).ShouldBeFalse("a failed run does not watch the queue");
    }

    [Fact]
    public async Task A_queue_still_holding_messages_at_the_cap_is_said_so_and_the_run_still_exits_zero()
    {
        runner.On("dotnet", Tool, 0, "Sent 24 event(s) to bff-order-events.").On("docker", ListQueues, 0, Queues(5));

        Job job = Service().Start(RebuildMode.Repair);
        int waits = 0;

        while (true)
        {
            await WaitUntilAsync(() => job.Status.State == JobState.Exited || Waits(job) > waits);

            if (job.Status.State == JobState.Exited)
            {
                break;
            }

            waits = Waits(job);
            time.Advance(ProjectionRebuildService.DrainInterval);
        }

        job.Status.ExitCode.ShouldBe(0);
        Texts(job)[^1].ShouldBe(
            $"bff-order-events: 5 message(s) still waiting after {ProjectionRebuildService.DrainCap.TotalSeconds:0} s; watch the Broker screen and trust the projection once it is empty.");
        Waits(job).ShouldBe((int)(ProjectionRebuildService.DrainCap / ProjectionRebuildService.DrainInterval));
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_is_said_so_and_does_not_fail_a_run_that_succeeded()
    {
        runner.On("dotnet", Tool, 0, "Sent 0 event(s) to bff-order-events.").OnFailing("docker", ListQueues, 1, "service rabbitmq is not running");

        Job job = Service().Start(RebuildMode.Repair);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        Texts(job)[^1].ShouldStartWith("bff-order-events: the broker did not answer (");
    }

    [Fact]
    public async Task A_queue_the_broker_does_not_declare_is_said_so()
    {
        runner.On("dotnet", Tool, 0).On("docker", ListQueues, 0, "[", """{"name":"ordering-commands","messages":0}""", "]");

        Job job = Service().Start(RebuildMode.Repair);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Texts(job)[^1].ShouldBe("bff-order-events: queue not declared; watch the Broker screen.");
    }

    [Fact]
    public void A_second_run_while_one_is_going_returns_the_running_one()
    {
        runner.OnLongRunning("dotnet", Tool);
        ProjectionRebuildService service = Service();

        Job first = service.Start(RebuildMode.Repair);

        service.Start(RebuildMode.Rebuild).ShouldBeSameAs(first);
    }

    [Fact]
    public void The_plan_states_every_limit_and_the_typed_confirmation()
    {
        ProjectionPlan plan = ProjectionRebuildService.Plan();

        plan.Queue.ShouldBe("bff-order-events");
        plan.RebuildConfirm.ShouldBe("bff-replay --reset");
        plan.Limits.Count.ShouldBe(ProjectionRebuildService.Limits.Length);
        plan.Limits.ShouldAllBe(l => l.Length > 0);
    }

    private ProjectionRebuildService Service() => new(
        new ComposeService(runner, Paths, time),
        runner,
        new BrokerService(new ComposeService(runner, Paths, time), Options.Create(new AdminOptions())),
        registry,
        Paths,
        Options.Create(new AdminOptions()),
        time);

    private static string[] Queues(long bffDepth) =>
    [
        "[",
        $$"""{"name":"bff-order-events","messages":{{bffDepth}}}""",
        """,{"name":"ordering-catalog-events","messages":0}""",
        "]",
    ];

    private static int Waits(Job job) => Texts(job).Count(t => t.EndsWith("message(s) waiting", StringComparison.Ordinal));

    private static List<string> Texts(Job job) => [.. job.Since(-1).Select(l => l.Text)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the run never reached the awaited state");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class ProjectionRebuildEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task The_plan_lists_the_limits_before_anything_runs()
    {
        JsonElement plan = await client.GetFromJsonAsync<JsonElement>("/api/broker/projection", TestContext.Current.CancellationToken);

        plan.GetProperty("queue").GetString().ShouldBe("bff-order-events");
        plan.GetProperty("rebuildConfirm").GetString().ShouldBe("bff-replay --reset");
        plan.GetProperty("limits").EnumerateArray().Select(l => l.GetString()).ShouldBe(ProjectionRebuildService.Limits.Select(l => l.Text));
    }

    [Fact]
    public async Task A_repair_needs_no_confirmation_and_replays_the_recorded_run()
    {
        HttpResponseMessage response = await client.PostAsync("/api/broker/projection/repair", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldBe("bff-replay");

        string output = await FinishedOutputAsync(job.GetProperty("id").GetString()!);

        output.ShouldContain("Skipped 24 event(s) the BFF's inbox had handled.");
        output.ShouldContain("bff-order-events: 0 messages (drained). Trust the projection now.");
        output.ShouldNotContain("web-bff");
    }

    [Fact]
    public async Task A_rebuild_without_the_typed_confirmation_is_refused()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/broker/projection/rebuild", new { confirm = "yes" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("title").GetString().ShouldBe("Confirmation required");
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("bff-replay --reset");
    }

    [Fact]
    public async Task A_confirmed_rebuild_stops_runs_and_starts_the_bff_in_that_order()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/broker/projection/rebuild", new { confirm = "bff-replay --reset" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldBe("bff-replay --reset");

        string output = await FinishedOutputAsync(job.GetProperty("id").GetString()!);

        int stop = output.IndexOf("stop web-bff", StringComparison.Ordinal);
        int reset = output.IndexOf("Reset: the order rows", StringComparison.Ordinal);
        int start = output.IndexOf("start web-bff", StringComparison.Ordinal);

        stop.ShouldBeGreaterThan(-1);
        reset.ShouldBeGreaterThan(stop);
        start.ShouldBeGreaterThan(reset);
        output.ShouldContain("Sent 24 event(s) to bff-order-events.");
    }

    private async Task<string> FinishedOutputAsync(string id)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(15);

        while (true)
        {
            JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

            if (view.GetProperty("summary").GetProperty("state").GetString() == "Exited")
            {
                return string.Join('\n', view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
            }

            DateTime.UtcNow.ShouldBeLessThan(until, "the job never exited");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
