using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Runbooks;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Runbooks;

public sealed class GameDayServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private const string Prefix = "-3.12 tools/game-day/game_day.py ";

    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public GameDayServiceTests()
    {
        runner = new FakeProcessRunner(new JobRegistry(time));
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_catalogue_is_the_tools_document_with_each_phases_confirmation_spelled_for_the_request()
    {
        RunbookCatalogue catalogue = GameDayService.Parse(RunbookRecordings.Catalogue.Split('\n'));

        catalogue.Error.ShouldBeNull();
        catalogue.Runbooks.Select(r => r.Runbook).ShouldBe(["contact-refused", "error-rate", "outbox-growth", "stuck-saga"]);
        catalogue.Runbooks.Single(r => r.Runbook == "contact-refused").Phases.Single().ConfirmWith.ShouldBe("contact-refused");
        catalogue.Runbooks.Single(r => r.Runbook == "outbox-growth").Phases.Single().ConfirmWith.ShouldBe(GameDayService.CauseConfirm);
        catalogue.Runbooks.Single(r => r.Runbook == "error-rate").Phases.Select(p => p.Alert).ShouldBe(["ErrorRateGateway", "ErrorRateService"]);

        RunbookEntry unscripted = catalogue.Runbooks.Single(r => r.Runbook == "stuck-saga");
        unscripted.Script.ShouldBeFalse();
        unscripted.Phases.ShouldBeEmpty();
        unscripted.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("none", null)]
    [InlineData("confirm", "cause")]
    [InlineData("typed", "latency")]
    [InlineData("a-word-the-tool-adds-later", "latency")]
    public void A_confirmation_the_tool_does_not_know_is_taken_as_the_strictest(string confirmation, string? expected)
    {
        GameDayService.ConfirmationFor(confirmation, "latency").ShouldBe(expected);
    }

    [Fact]
    public void A_name_that_is_a_path_a_flag_or_not_lower_case_is_not_a_runbook()
    {
        GameDayService.IsRunbookName("outbox-growth").ShouldBeTrue();
        GameDayService.IsRunbookName("../x").ShouldBeFalse();
        GameDayService.IsRunbookName("--restore").ShouldBeFalse();
        GameDayService.IsRunbookName("Latency").ShouldBeFalse();
        GameDayService.IsRunbookName("latency\n").ShouldBeFalse();
    }

    [Fact]
    public async Task The_listing_runs_the_tool_as_a_read_in_the_backend_clone()
    {
        runner.On("py", Prefix + "--list --json", 0, "[]");

        RunbookCatalogue catalogue = await Service().ListAsync(Token);

        catalogue.Error.ShouldBeNull();
        catalogue.Runbooks.ShouldBeEmpty();
        ProcessSpec spec = runner.Started.ShouldHaveSingleItem();
        spec.WorkingDirectory.ShouldBe(Paths.BackendDir);
        spec.Listed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_listing_that_is_not_json_or_a_tool_that_fails_is_said_rather_than_thrown()
    {
        runner.On("py", Prefix + "--list --json", 0, "Traceback (most recent call last):");
        (await Service().ListAsync(Token)).Error!.ShouldContain("--list --json");

        FakeProcessRunner failing = new(new JobRegistry(time));
        failing.OnFailing("py", Prefix + "--list --json", 1, "No such file");
        RunbookCatalogue refused = await Service(failing).ListAsync(Token);

        refused.Error.ShouldBe("No such file");
        refused.Runbooks.ShouldBeEmpty();
        await failing.DisposeAsync();
    }

    [Fact]
    public async Task A_cause_is_the_tools_cause_flag_and_a_second_phase_is_named_by_its_alert()
    {
        runner.On("py", Prefix, 0, "game day: cause OK in 0m05s");

        Job first = Service().Start("error-rate", GameDayMode.Cause, null).Job;
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Job second = Service().Start("error-rate", GameDayMode.Cause, "ErrorRateService").Job;
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldBe(
        [
            "-3.12 tools/game-day/game_day.py error-rate --cause",
            "-3.12 tools/game-day/game_day.py error-rate --cause --phase ErrorRateService",
        ]);
    }

    [Fact]
    public async Task A_restore_never_carries_a_phase_and_a_cause_still_going_refuses_the_next_job()
    {
        runner.OnLongRunning("py", Prefix + "latency --cause");
        runner.On("py", Prefix + "latency --restore", 0, "game day: restore OK in 0m04s");
        ComposeGate gate = new();
        GameDayService service = Service(gate: gate);

        Claim cause = service.Start("latency", GameDayMode.Cause, null);
        Claim refused = service.Start("latency", GameDayMode.Restore, "ignored");

        refused.ShouldBe(cause);
        refused.Kind.ShouldBe("game day cause latency");

        await runner.StopAsync(cause.Job, Token);
        Job restore = service.Start("latency", GameDayMode.Restore, "ignored").Job;
        await restore.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        runner.Started[^1].Arguments.ShouldBe(["-3.12", "tools/game-day/game_day.py", "latency", "--restore"]);
    }

    [Theory]
    [InlineData("../latency")]
    [InlineData("--x")]
    [InlineData("Latency")]
    public void A_name_that_is_not_a_runbook_never_reaches_the_reader(string name)
    {
        // A reader that answers every name: only the name check in Text can return null here, not an absent file.
        Service(read: _ => "leak").Text(name).ShouldBeNull();
    }

    [Fact]
    public void A_runbook_is_read_from_the_clone_and_a_name_that_is_not_one_reads_nothing()
    {
        string clone = Directory.CreateTempSubdirectory("admin-runbooks-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(clone, "docs", "runbooks"));
            File.WriteAllText(Path.Combine(clone, "docs", "runbooks", "latency.md"), "# Runbook");
            GameDayService service = Service(read: GameDayService.ReadFromClone(Paths with { BackendDir = clone }));

            service.Text("latency").ShouldBe("# Runbook");
            service.Text("missing").ShouldBeNull();
            service.Text("../latency").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(clone, recursive: true);
        }
    }

    private GameDayService Service(FakeProcessRunner? process = null, ComposeGate? gate = null, Func<string, string?>? read = null) =>
        new(process ?? runner, gate ?? new ComposeGate(), Paths, read ?? (_ => null), time);
}
