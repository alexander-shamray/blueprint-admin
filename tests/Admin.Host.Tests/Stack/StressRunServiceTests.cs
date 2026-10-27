using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class StressRunServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");

    private static readonly string[] RunLines =
    [
        "currency: EUR",
        "running (01.0s), 05/50 VUs, 0 complete and 0 interrupted iterations",
        "journey   [   1% ] 05/50 VUs  01.0s/2m00.0s",
        "k6 exit: 99",
        "sampler: stopped, k6 wrote its results",
        "Knee: step 2 (50 VUs): failures, 23.4% of requests failed",
        @"Summary: artifacts\stress\20261027-150000\summary.md",
    ];

    private readonly FakeTimeProvider time = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner runner;
    private readonly ComposeGate composeGate = new();
    private readonly List<string> summariesRead = [];

    public StressRunServiceTests()
    {
        registry = new JobRegistry(time);
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task A_run_runs_every_block_in_the_documents_order_waits_the_minute_and_ends_with_k6s_verdict_and_the_knee()
    {
        Script(run: 99);
        StressRunService service = Service();

        Job run = service.Start().Job;
        await WaitUntilAsync(() => Texts(run).Any(t => t.StartsWith("== settle", StringComparison.Ordinal)));
        runner.Started.Count.ShouldBe(2);
        time.Advance(StressRunService.Settle);
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(99);
        runner.Started.Select(s => s.Arguments[^1]).ShouldBe([LoadRunService.K6Read, StressRunService.Up, LoadRunService.PrometheusReady, StressRunService.Run]);
        runner.Started.ShouldAllBe(s => s.FileName == "powershell" && s.WorkingDirectory == Paths.BackendDir);
        run.Spec.CommandLine.ShouldBe("stress-run k6 up settle prometheus run");
        Texts(run)[^1].ShouldBe($"k6 exited 99: {StressRunService.Verdicts[99]}. Knee: step 2 (50 VUs): failures, 23.4% of requests failed");
        service.View()!.Knee.ShouldBe("Knee: step 2 (50 VUs): failures, 23.4% of requests failed");
    }

    [Fact]
    public async Task The_summary_summarise_wrote_is_carried_into_the_job_from_the_clones_artifacts()
    {
        Script(run: 0);

        Job run = Service(settle: TimeSpan.Zero).Start().Job;
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        summariesRead.ShouldBe([Path.GetFullPath(Path.Combine(Paths.BackendDir, "artifacts", "stress", "20261027-150000", "summary.md"))]);
        List<string> texts = Texts(run);
        int header = texts.IndexOf(@"== summary (artifacts\stress\20261027-150000\summary.md)");
        header.ShouldBeGreaterThanOrEqualTo(0);
        texts[header + 1].ShouldBe("| Step | VUs |");
    }

    [Fact]
    public async Task A_summary_line_outside_the_clones_artifacts_is_not_read()
    {
        ScriptUntilRun();
        runner.On("powershell", Arguments(StressRunService.Run), 0, "k6 exit: 0", "Knee: none; every measured step held.", @"Summary: ..\..\elsewhere\summary.md");

        Job run = Service(settle: TimeSpan.Zero).Start().Job;
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        summariesRead.ShouldBeEmpty();
        Texts(run).ShouldContain(@"the summary ..\..\elsewhere\summary.md could not be read under the clone's artifacts/stress, so it is not carried here.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(107)]
    public async Task A_run_ends_with_k6s_code_and_says_what_the_document_says_it_means(int code)
    {
        Script(run: code);

        Job run = Service(settle: TimeSpan.Zero).Start().Job;
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(code);
        Texts(run)[^1].ShouldStartWith($"k6 exited {code}: {StressRunService.Verdicts[code]}. ");
    }

    [Fact]
    public async Task A_run_block_that_stops_before_k6_says_so_rather_than_reading_its_code_as_k6s()
    {
        ScriptUntilRun();
        runner.On("powershell", Arguments(StressRunService.Run), 1, "products: none usable in ordering.ProductPrices");

        Job run = Service(settle: TimeSpan.Zero).Start().Job;
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        Texts(run)[^1].ShouldBe("the run block exited 1 before k6 ran, saying \"products: none usable in ordering.ProductPrices\": no load was driven.");
        NoVerdictWasRead(run);
    }

    [Fact]
    public async Task Stop_in_the_run_block_on_a_stack_with_another_sql_password_is_still_a_stop_during_k6()
    {
        const string password = "Other_Pa55!";
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, "k6.exe v2.2.0")
            .On("powershell", Arguments(StressRunService.Up), 0, " Container commerce-gateway-1  Recreated")
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, LoadRunService.PrometheusYes)
            .OnLongRunning("powershell", string.Join(' ', LoadRunService.Spec(StressRunService.Run, Paths, password).Arguments), "running (01.0s), 05/50 VUs, 0 complete and 0 interrupted iterations");
        StressRunService service = new(runner, composeGate, registry, Paths, Options.Create(new AdminOptions { SqlPassword = password }), time, TimeSpan.Zero, _ => null);

        Job run = service.Start().Job;
        await WaitUntilAsync(() => runner.Started.Count == 4 && runner.LastStarted is { State: JobState.Running });
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Texts(run)[^1].ShouldBe(StressRunService.StoppedInRun);
    }

    [Fact]
    public async Task A_missing_k6_ends_the_run_at_its_first_block_before_the_stack_is_touched()
    {
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, "k6: not installed (winget install k6 --source winget)");

        Job run = Service(settle: TimeSpan.Zero).Start().Job;
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        runner.Started.Count.ShouldBe(1);
        Texts(run)[^1].ShouldBe("k6 said \"k6: not installed (winget install k6 --source winget)\"; the stress run stops here.");
    }

    [Fact]
    public async Task The_run_holds_the_compose_gate_so_an_up_beside_it_is_refused_and_a_second_run_is_the_one_going()
    {
        ScriptUntilRun();
        runner.OnLongRunning("powershell", Arguments(StressRunService.Run), "running (01.0s), 05/50 VUs, 0 complete and 0 interrupted iterations");
        StressRunService service = Service(settle: TimeSpan.Zero);

        Job run = service.Start().Job;
        await WaitUntilAsync(() => runner.Started.Count == 4);

        service.Start().Job.ShouldBeSameAs(run);
        Claim up = composeGate.Claim("up", () => throw new InvalidOperationException("an up started beside the stress run"));
        up.Kind.ShouldBe(StressRunService.Kind);
        up.Job.ShouldBeSameAs(run);

        await service.StopAsync();
        composeGate.Claim("up", () => registry.Create(new ProcessSpec("up", [], Paths.BackendDir))).Kind.ShouldBe("up");
    }

    [Fact]
    public async Task The_step_is_read_off_k6s_progress_lines_while_it_runs()
    {
        ScriptUntilRun();
        string[] lines =
        [
            .. Enumerable.Range(1, 3).Select(i => Tick(i, i * 3)),
            .. Enumerable.Range(4, StressStaircase.HoldTicks).Select(i => Tick(i, 10)),
        ];
        runner.OnLongRunning("powershell", Arguments(StressRunService.Run), lines);
        StressRunService service = Service(settle: TimeSpan.Zero);

        service.Start();
        await WaitUntilAsync(() => service.View()?.Progress is { Holding: true });

        service.View()!.Progress.ShouldBe(new StressProgress(10, 400, $"{3 + StressStaircase.HoldTicks:00}.0s", 1, true));
        await service.StopAsync();
    }

    [Fact]
    public async Task Stop_in_the_run_block_says_there_is_no_knee_and_neither_a_pass_nor_a_fail()
    {
        ScriptUntilRun();
        runner.OnLongRunning("powershell", Arguments(StressRunService.Run), "running (01.0s), 05/50 VUs, 0 complete and 0 interrupted iterations");
        StressRunService service = Service(settle: TimeSpan.Zero);

        Job run = service.Start().Job;
        await WaitUntilAsync(() => runner.LastStarted is { State: JobState.Running } k6 && k6.Spec.Arguments[^1] == StressRunService.Run);
        Job k6Job = runner.LastStarted!;

        (await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeSameAs(run);

        k6Job.Status.State.ShouldBe(JobState.Exited);
        run.Status.ShouldBe((JobState.Exited, -1));
        Texts(run)[^1].ShouldBe(StressRunService.StoppedInRun);
        Texts(run)[^1].ShouldContain("neither a pass nor a fail");
    }

    [Fact]
    public async Task Stop_during_the_settle_names_it_and_starts_nothing_more()
    {
        ScriptUntilRun();
        StressRunService service = Service();

        Job run = service.Start().Job;
        await WaitUntilAsync(() => Texts(run).Any(t => t.StartsWith("== settle", StringComparison.Ordinal)));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        run.Status.ShouldBe((JobState.Exited, -1));
        runner.Started.Count.ShouldBe(2);
        Texts(run)[^1].ShouldBe(StressRunService.StoppedBefore("settle"));
    }

    [Fact]
    public async Task Stop_with_no_run_going_ends_nothing()
    {
        (await Service().StopAsync()).ShouldBeNull();
    }

    [Fact]
    public void The_override_is_on_for_each_service_it_changes_whose_running_container_was_created_with_it()
    {
        ComposeStatus backend = ComposeStatus.Up(
        [
            new("gateway", "running", "healthy", null, [5000], ["docker-compose.yml", StressRunService.OverrideFile]),
            new("shipping-worker", "running", null, null, [], ["docker-compose.yml"]),
            new("notifications-worker", "exited", null, 0, [], ["docker-compose.yml", StressRunService.OverrideFile]),
            new("catalog-api", "running", "healthy", null, [], ["docker-compose.yml", StressRunService.OverrideFile]),
            new("ordering-api", "running", "healthy", null, []),
        ]);

        StressRunService.OnOverride(backend).ShouldBe(["gateway"]);
        StressRunService.OnOverride(ComposeStatus.Unreachable("docker did not answer")).ShouldBeEmpty();
    }

    private static void NoVerdictWasRead(Job run) => Texts(run).ShouldNotContain(t => t.StartsWith("k6 exited", StringComparison.Ordinal));

    private StressRunService Service(TimeSpan? settle = null) =>
        new(runner, composeGate, registry, Paths, Options.Create(new AdminOptions()), time, settle ?? StressRunService.Settle, path =>
        {
            summariesRead.Add(path);

            return ["| Step | VUs |", "| 1 | 10 |"];
        });

    private void Script(int run)
    {
        ScriptUntilRun();
        runner.On("powershell", Arguments(StressRunService.Run), run, RunLines);
    }

    private void ScriptUntilRun() =>
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, "k6.exe v2.2.0 (commit/00a9a1b7f5, go1.26.5, windows/amd64)")
            .On("powershell", Arguments(StressRunService.Up), 0, " Container commerce-gateway-1  Recreated")
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, LoadRunService.PrometheusYes);

    private static string Tick(int second, int users) => $"running ({second:00}.0s), {users:000}/400 VUs, 0 complete and 0 interrupted iterations";

    private static string Arguments(string block) => string.Join(' ', LoadRunService.Arguments(block));

    private static List<string> Texts(Job job) => [.. job.Since(-1).Select(l => l.Text)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the stress run never reached the awaited state");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class StressStaircaseTests
{
    [Fact]
    public void A_count_that_holds_for_the_hold_ticks_is_the_next_step_and_a_ramp_between_is_not()
    {
        StressStaircase staircase = new();

        Feed(staircase, [1, 4, 7, 10]);
        staircase.Current.ShouldBe(new StressProgress(10, 400, "04.0s", 0, false));

        Feed(staircase, Enumerable.Repeat(10, StressStaircase.HoldTicks - 1), from: 5);
        staircase.Current!.Holding.ShouldBeTrue();
        staircase.Current.Step.ShouldBe(1);

        Feed(staircase, [20, 30, 40, 50], from: 20);
        staircase.Current.ShouldBe(new StressProgress(50, 400, "23.0s", 1, false));

        Feed(staircase, Enumerable.Repeat(50, StressStaircase.HoldTicks), from: 24);
        staircase.Current.ShouldBe(new StressProgress(50, 400, "33.0s", 2, true));
    }

    [Fact]
    public void Lines_that_are_not_k6s_progress_leave_the_reading_as_it_was()
    {
        StressStaircase staircase = new();

        staircase.Observe("journey   [   7% ] 01/12 VUs  01.0s/14.0s");
        staircase.Observe("currency: EUR");
        staircase.Current.ShouldBeNull();

        staircase.Observe("running (1m10.0s), 012/400 VUs, 9 complete and 0 interrupted iterations");
        staircase.Observe("journey   [  10% ] 012/400 VUs  1m10.0s/17m30s");
        staircase.Current.ShouldBe(new StressProgress(12, 400, "1m10.0s", 0, false));
    }

    [Fact]
    public void No_users_is_never_a_hold()
    {
        StressStaircase staircase = new();

        Feed(staircase, Enumerable.Repeat(0, StressStaircase.HoldTicks * 2));

        staircase.Current.ShouldBe(new StressProgress(0, 400, $"{StressStaircase.HoldTicks * 2:00}.0s", 0, false));
    }

    private static void Feed(StressStaircase staircase, IEnumerable<int> users, int from = 1)
    {
        int second = from;

        foreach (int count in users)
        {
            staircase.Observe($"running ({second++:00}.0s), {count:000}/400 VUs, 0 complete and 0 interrupted iterations");
        }
    }
}

/// <summary>A host of its own: a fake stress run claims its ComposeGate and puts its stack on the override, which another class's Up would meet.</summary>
public sealed class StressRunEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task A_fake_stress_run_shows_its_step_and_the_override_refuses_an_up_and_its_stop_leaves_the_override_for_the_up_to_undo()
    {
        JsonElement idle = await GatewayAsync();
        HttpResponseMessage response = await client.PostAsync("/api/stack/stress-run", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("commandLine").GetString().ShouldBe("stress-run k6 up settle prometheus run");

        JsonElement stack = await WaitForStackAsync(s => s.GetProperty("stressRun").GetProperty("progress").ValueKind == JsonValueKind.Object);
        stack.GetProperty("stressRun").GetProperty("job").GetProperty("id").GetString().ShouldBe(job.GetProperty("id").GetString());
        stack.GetProperty("stressOverride").EnumerateArray().Select(s => s.GetString()).ShouldBe(StressRunService.OverrideServices, ignoreOrder: true);
        (await GatewayAsync()).GetProperty("requestRate").GetDouble().ShouldBeGreaterThan(idle.GetProperty("requestRate").GetDouble());

        (await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync("/api/stack/load-run", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        HttpResponseMessage stop = await client.PostAsync("/api/stack/stress-run/stop", null, TestContext.Current.CancellationToken);
        stop.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stop.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("exitCode").GetInt32().ShouldBe(-1);
        (await StackAsync()).GetProperty("stressOverride").GetArrayLength().ShouldBe(3);

        HttpResponseMessage up = await client.PostAsync("/api/stack/backend/up", null, TestContext.Current.CancellationToken);
        up.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForStackAsync(s => s.GetProperty("stressOverride").GetArrayLength() == 0);
    }

    [Fact]
    public async Task Stop_with_no_run_going_is_a_conflict()
    {
        HttpResponseMessage response = await client.PostAsync("/api/stack/stress-run/stop", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private Task<JsonElement> StackAsync() => client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);

    private async Task<JsonElement> WaitForStackAsync(Func<JsonElement, bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            JsonElement stack = await StackAsync();

            if (condition(stack))
            {
                return stack;
            }

            DateTime.UtcNow.ShouldBeLessThan(until, "the stack never reached the awaited state");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private async Task<JsonElement> GatewayAsync()
    {
        JsonElement health = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/health", TestContext.Current.CancellationToken);

        return health.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "Gateway.Api");
    }
}
