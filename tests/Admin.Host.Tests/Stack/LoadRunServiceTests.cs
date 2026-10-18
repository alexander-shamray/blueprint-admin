using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class LoadRunServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");

    private readonly FakeTimeProvider time = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner runner;

    public LoadRunServiceTests()
    {
        registry = new JobRegistry(time);
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task A_run_runs_every_block_in_the_documents_order_and_ends_with_k6s_exit_and_its_verdict()
    {
        Script(run: 0);

        Job run = Service().Start();
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        runner.Started.Select(s => s.Arguments[^1]).ShouldBe(LoadRunService.Steps.Select(s => s.Block));
        runner.Started.ShouldAllBe(s => s.FileName == "powershell" && s.WorkingDirectory == Paths.BackendDir && s.Arguments[0] == "-NoProfile");
        run.Spec.CommandLine.ShouldBe("load-run k6 prometheus slo-run token product restock run");
        Texts(run).Where(t => t.StartsWith("== ", StringComparison.Ordinal)).Select(t => t.Split(' ')[1])
            .ShouldBe(["k6", "prometheus", "slo-run", "token", "product", "restock", "run"]);
        Texts(run)[^1].ShouldBe($"k6 exited 0: {LoadRunService.Verdicts[0]}");
    }

    [Fact]
    public async Task Each_steps_lines_are_carried_under_a_line_naming_it_and_its_own_job()
    {
        Script(run: 0);

        Job run = Service().Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Job product = registry.All().Single(j => j.Spec.Arguments is [.., var block] && block == LoadRunService.Product);
        List<string> texts = Texts(run);
        int header = texts.IndexOf($"== product (job {product.Id})");
        header.ShouldBeGreaterThanOrEqualTo(0);
        texts[(header + 1)..(header + 5)].ShouldBe(["--- product ---", "5eed0000-0000-0000-0000-000000000001", "--- currency ---", "EUR"]);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(107)]
    [InlineData(105)]
    public async Task A_run_that_k6_failed_exits_with_k6s_code_and_says_what_the_document_says_it_means(int code)
    {
        Script(run: code);

        Job run = Service().Start();
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(code);
        Texts(run)[^1].ShouldBe($"k6 exited {code}: {LoadRunService.Verdicts[code]}");
    }

    [Fact]
    public async Task A_code_the_document_does_not_read_is_said_to_be_k6s_own()
    {
        Script(run: 1);

        Job run = Service().Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Texts(run)[^1].ShouldBe("k6 exited 1: k6's own code, which run-locally.md does not read");
    }

    [Fact]
    public async Task A_missing_k6_ends_the_run_at_its_first_block_with_the_reads_own_line()
    {
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, "k6: not installed (winget install k6 --source winget)");

        Job run = Service().Start();
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        runner.Started.ShouldHaveSingleItem();
        Texts(run)[^1].ShouldBe("k6 said \"k6: not installed (winget install k6 --source winget)\"; the load run stops here.");
    }

    [Fact]
    public async Task A_prometheus_that_does_not_answer_ends_the_run_before_the_client_is_touched()
    {
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, LoadRunRecordings.K6)
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, "prometheus ready: no (Unable to connect to the remote server)");

        Job run = Service().Start();
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        runner.Started.Count.ShouldBe(2);
        Texts(run)[^1].ShouldBe("prometheus said \"prometheus ready: no (Unable to connect to the remote server)\"; the load run stops here.");
    }

    [Fact]
    public async Task A_block_that_exits_non_zero_ends_the_run_there_with_its_code()
    {
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, LoadRunRecordings.K6)
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, LoadRunRecordings.Prometheus)
            .On("powershell", Arguments(LoadRunService.SloClient), 0, "slo-run: already there")
            .On("powershell", Arguments(LoadRunService.TokenCheck), 1, "permission: orders:write", "token check: fail");

        Job run = Service().Start();
        int exitCode = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        runner.Started.Count.ShouldBe(4);
        Texts(run)[^1].ShouldBe("token exited with 1; the load run stops here.");
    }

    [Fact]
    public async Task A_second_run_while_one_goes_returns_the_one_going_and_starts_nothing()
    {
        ScriptUntilRun();
        runner.OnLongRunning("powershell", Arguments(LoadRunService.Run), "running (0m01.0s), 00/20 VUs, 1 complete and 0 interrupted iterations");
        LoadRunService service = Service();

        Job first = service.Start();
        await WaitUntilAsync(() => runner.Started.Count == LoadRunService.Steps.Count);

        service.Start().ShouldBeSameAs(first);
        runner.Started.Count.ShouldBe(LoadRunService.Steps.Count);
        service.Current.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task Stop_ends_the_block_going_and_says_teardown_did_not_run()
    {
        ScriptUntilRun();
        runner.OnLongRunning("powershell", Arguments(LoadRunService.Run), "running (0m01.0s), 00/20 VUs, 1 complete and 0 interrupted iterations");
        LoadRunService service = Service();

        Job run = service.Start();
        await WaitUntilAsync(() => runner.LastStarted is { State: JobState.Running } k6 && k6.Spec.Arguments[^1] == LoadRunService.Run);
        Job k6Job = runner.LastStarted!;

        Job? stopped = await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        stopped.ShouldBeSameAs(run);
        k6Job.Status.State.ShouldBe(JobState.Exited);
        run.Status.ShouldBe((JobState.Exited, -1));
        Texts(run)[^1].ShouldBe(LoadRunService.StoppedInRun);
    }

    [Fact]
    public async Task Stop_before_k6_names_the_step_it_ended_and_says_no_load_was_driven()
    {
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, Recorded(LoadRunRecordings.K6))
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, Recorded(LoadRunRecordings.Prometheus))
            .On("powershell", Arguments(LoadRunService.SloClient), 0, Recorded(LoadRunRecordings.SloClient))
            .OnLongRunning("powershell", Arguments(LoadRunService.TokenCheck), "permission: inventory:admin,orders:write");
        LoadRunService service = Service();

        Job run = service.Start();
        await WaitUntilAsync(() => runner.LastStarted is { State: JobState.Running } token && token.Spec.Arguments[^1] == LoadRunService.TokenCheck);

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        run.Status.ShouldBe((JobState.Exited, -1));
        runner.Started.Count.ShouldBe(4);
        Texts(run)[^1].ShouldBe("stopped at the token step, before k6 started: no load was driven and no order was placed.");
    }

    [Fact]
    public async Task Stop_with_no_run_going_ends_nothing()
    {
        Script(run: 0);
        LoadRunService service = Service();

        (await service.StopAsync()).ShouldBeNull();

        Job run = service.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        (await service.StopAsync()).ShouldBeNull();
        run.Status.ExitCode.ShouldBe(0);
    }

    private LoadRunService Service() => new(runner, registry, Paths);

    /// <summary>Every block as the live run recorded it, the run's exit being <paramref name="run"/>.</summary>
    private void Script(int run)
    {
        ScriptUntilRun();
        runner.On("powershell", Arguments(LoadRunService.Run), run, Recorded(LoadRunRecordings.Run));
    }

    private void ScriptUntilRun() =>
        runner.On("powershell", Arguments(LoadRunService.K6Read), 0, Recorded(LoadRunRecordings.K6))
            .On("powershell", Arguments(LoadRunService.PrometheusReady), 0, Recorded(LoadRunRecordings.Prometheus))
            .On("powershell", Arguments(LoadRunService.SloClient), 0, Recorded(LoadRunRecordings.SloClient))
            .On("powershell", Arguments(LoadRunService.TokenCheck), 0, Recorded(LoadRunRecordings.TokenCheck))
            .On("powershell", Arguments(LoadRunService.Product), 0, Recorded(LoadRunRecordings.Product))
            .On("powershell", Arguments(LoadRunService.Restock), 0, Recorded(LoadRunRecordings.Restock));

    private static string Arguments(string block) => string.Join(' ', LoadRunService.Arguments(block));

    private static string[] Recorded(string recording) => recording.ReplaceLineEndings("\n").Split('\n');

    private static List<string> Texts(Job job) => [.. job.Since(-1).Select(l => l.Text)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the load run never reached the awaited state");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class LoadRunEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task A_load_run_against_the_fakes_is_one_listed_job_the_stack_view_names_and_it_passes()
    {
        HttpResponseMessage response = await client.PostAsync("/api/stack/load-run", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        string id = job.GetProperty("id").GetString()!;
        job.GetProperty("commandLine").GetString().ShouldBe("load-run k6 prometheus slo-run token product restock run");

        JsonElement view = await WaitForExitAsync(id);
        view.GetProperty("summary").GetProperty("exitCode").GetInt32().ShouldBe(0);
        view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).ShouldContain("k6 exit: 0");

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);
        stack.GetProperty("loadRun").GetProperty("id").GetString().ShouldBe(id);
    }

    [Fact]
    public async Task Stop_with_no_run_going_is_a_conflict()
    {
        HttpResponseMessage response = await client.PostAsync("/api/stack/load-run/stop", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private Task<JsonElement> WaitForExitAsync(string id) => LoadRunStripTests.WaitForExitAsync(client, id);
}

/// <summary>A host of its own: a fake run puts its strip under load, which a run by another test in the class would already have.</summary>
public sealed class LoadRunStripTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task The_strip_reads_the_recording_made_under_load_once_a_fake_run_has_run()
    {
        JsonElement idle = await GatewayAsync();

        HttpResponseMessage response = await client.PostAsync("/api/stack/load-run", null, TestContext.Current.CancellationToken);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        await WaitForExitAsync(client, job.GetProperty("id").GetString()!);
        JsonElement loaded = await GatewayAsync();

        idle.GetProperty("requestRate").GetDouble().ShouldBeLessThan(1);
        loaded.GetProperty("requestRate").GetDouble().ShouldBeGreaterThan(2);
    }

    private async Task<JsonElement> GatewayAsync()
    {
        JsonElement health = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/health", TestContext.Current.CancellationToken);

        return health.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "Gateway.Api");
    }

    internal static async Task<JsonElement> WaitForExitAsync(HttpClient client, string id)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);

            if (view.GetProperty("summary").GetProperty("state").GetString() == "Exited")
            {
                return view;
            }

            DateTime.UtcNow.ShouldBeLessThan(until, "the load run never exited");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
