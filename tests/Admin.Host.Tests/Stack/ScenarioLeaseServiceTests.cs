using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Stack;

public sealed class ScenarioLeaseServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Prefix = $"compose -f {Paths.ComposeFile} ";
    private static readonly string Override = ComposeService.ShortTimeoutsFile(Paths.ComposeFile);

    private const string Running = """{"Service":"inventory-api","State":"running","Health":"healthy","ExitCode":0,"Publishers":[]}""";
    private const string Exited = """{"Service":"inventory-api","State":"exited","Health":"","ExitCode":0,"Publishers":[]}""";
    private const string OrderingRunning = """{"Service":"ordering-api","State":"running","Health":"healthy","ExitCode":0,"Publishers":[]}""";

    private readonly string journal = Path.Combine(Path.GetTempPath(), "blueprint-admin-tests", $"lease-{Guid.NewGuid():N}.json");
    private readonly FakeTimeProvider time = new();
    private readonly ComposeGate gate = new();
    private readonly JobRegistry registry;
    private readonly FakeProcessRunner fake;
    private readonly TestRunner runner;
    private readonly ScenarioLeaseService leases;
    private readonly List<bool> journalAtStart = [];

    public ScenarioLeaseServiceTests()
    {
        registry = new JobRegistry(time);
        fake = new FakeProcessRunner(registry)
            .On("docker", Prefix + "stop inventory-api", 0, " Container commerce-inventory-api-1  Stopped")
            .On("docker", Prefix + "up -d --no-deps --wait inventory-api", 0, " Container commerce-inventory-api-1  Healthy")
            .On("docker", $"compose -f {Paths.ComposeFile} -f {Override} up -d --no-deps --wait ordering-api", 0, " Container commerce-ordering-api-1  Healthy")
            .On("docker", Prefix + "up -d --no-deps --wait ordering-api", 0, " Container commerce-ordering-api-1  Healthy")
            .On("docker", Prefix + "ps -a --format json", 0, Running, OrderingRunning);
        runner = new TestRunner(fake, registry) { OnStart = spec => journalAtStart.Add(File.Exists(journal)) };
        ComposeService compose = new(runner, Paths, time);
        leases = new ScenarioLeaseService(compose, gate, journal, time, NullLogger<ScenarioLeaseService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        leases.Dispose();
        await fake.DisposeAsync();

        // A test that is refused before anything is written never creates the directory, and File.Delete does not forgive that.
        if (File.Exists(journal))
        {
            File.Delete(journal);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<AcquireResult> Acquire(string change = ScenarioControl.StopInventory, int seconds = 120, CancellationToken? token = null) =>
        leases.AcquireAsync([change], TimeSpan.FromSeconds(seconds), "a run", token ?? Token);

    private Task<AcquireResult> AcquireBoth(int seconds = 120) =>
        leases.AcquireAsync([ScenarioControl.ShortWaits, ScenarioControl.StopInventory], TimeSpan.FromSeconds(seconds), "a run", Token);

    [Fact]
    public async Task A_lease_stops_the_service_after_the_journal_is_on_disk_and_holds_the_gate()
    {
        AcquireResult result = await Acquire();

        result.Outcome.ShouldBe(AcquireOutcome.Held);
        result.Lease!.Services.ShouldBe(["inventory-api"]);
        result.Lease.Changes.ShouldBe([ScenarioControl.StopInventory]);
        result.Lease.Label.ShouldBe("a run");
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "stop inventory-api");
        journalAtStart.First().ShouldBeTrue("the journal was not written before the first command of the change");
        File.Exists(journal).ShouldBeTrue();
        leases.Current!.Id.ShouldBe(result.Lease.Id);
        gate.Claim("up", () => throw new InvalidOperationException("no job may start under a lease")).Kind.ShouldBe(ComposeGate.LeaseKind);
    }

    [Fact]
    public async Task The_short_waits_change_recreates_ordering_with_the_override_and_restores_without_it()
    {
        (await Acquire(ScenarioControl.ShortWaits)).Outcome.ShouldBe(AcquireOutcome.Held);
        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Restored);

        string[] lines = [.. runner.Started.Select(s => string.Join(' ', s.Arguments))];
        lines.ShouldContain($"compose -f {Paths.ComposeFile} -f {Override} up -d --no-deps --wait ordering-api");
        lines.ShouldContain(Prefix + "up -d --no-deps --wait ordering-api");
    }

    [Fact]
    public async Task A_lease_of_two_changes_applies_them_in_order_and_restores_the_last_first()
    {
        AcquireResult held = await AcquireBoth();

        held.Outcome.ShouldBe(AcquireOutcome.Held);
        held.Lease!.Services.ShouldBe(["ordering-api", "inventory-api"]);
        held.Lease.Title.ShouldBe("The saga's waits shortened + Inventory stopped");
        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Restored);

        string[] lines = [.. runner.Started.Where(s => !s.Arguments.Contains("ps")).Select(s => string.Join(' ', s.Arguments))];
        lines.ShouldBe(
        [
            $"compose -f {Paths.ComposeFile} -f {Override} up -d --no-deps --wait ordering-api",
            Prefix + "stop inventory-api",
            Prefix + "up -d --no-deps --wait inventory-api",
            Prefix + "up -d --no-deps --wait ordering-api",
        ]);
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_change_that_fails_undoes_both_and_leaves_no_lease()
    {
        runner.FailIf = spec => spec.Arguments.Contains("stop");

        AcquireResult result = await AcquireBoth();

        result.Outcome.ShouldBe(AcquireOutcome.Failed);
        result.Detail!.ShouldContain("The changes were undone.");
        string[] lines = [.. runner.Started.Where(s => !s.Arguments.Contains("ps")).Select(s => string.Join(' ', s.Arguments))];
        lines.ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        lines[^1].ShouldBe(Prefix + "up -d --no-deps --wait ordering-api");
        leases.Current.ShouldBeNull();
        File.Exists(journal).ShouldBeFalse();
    }

    private Task<AddResult> Add(string change = ScenarioControl.StopInventory, CancellationToken? token = null) =>
        leases.AddChangeAsync(change, token ?? Token);

    private string[] Commands() =>
        [.. runner.Started.Where(s => !s.Arguments.Contains("ps")).Select(s => string.Join(' ', s.Arguments))];

    [Fact]
    public async Task A_change_added_to_a_held_lease_is_journaled_before_it_runs_and_the_deadline_does_not_move()
    {
        AcquireResult held = await Acquire(ScenarioControl.ShortWaits, seconds: 300);
        string? journalWhenStopStarted = null;
        runner.OnStart = spec =>
        {
            if (spec.Arguments.Contains("stop"))
            {
                journalWhenStopStarted = File.ReadAllText(journal);
            }
        };
        time.Advance(TimeSpan.FromSeconds(40));

        AddResult added = await Add();

        added.Outcome.ShouldBe(AddOutcome.Held);
        added.Lease!.Id.ShouldBe(held.Lease!.Id);
        added.Lease.Changes.ShouldBe([ScenarioControl.ShortWaits, ScenarioControl.StopInventory]);
        added.Lease.Services.ShouldBe(["ordering-api", "inventory-api"]);
        added.Lease.ExpiresAt.ShouldBe(held.Lease.ExpiresAt);
        journalWhenStopStarted.ShouldNotBeNull().ShouldContain("stop-inventory");
        Commands().ShouldContain(Prefix + "stop inventory-api");
        leases.Current!.Changes.ShouldBe([ScenarioControl.ShortWaits, ScenarioControl.StopInventory]);
    }

    [Fact]
    public async Task A_change_added_later_is_undone_first_and_the_earlier_one_after_it()
    {
        await Acquire(ScenarioControl.ShortWaits);
        await Add();

        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Restored);

        Commands().ShouldBe(
        [
            $"compose -f {Paths.ComposeFile} -f {Override} up -d --no-deps --wait ordering-api",
            Prefix + "stop inventory-api",
            Prefix + "up -d --no-deps --wait inventory-api",
            Prefix + "up -d --no-deps --wait ordering-api",
        ]);
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_change_off_the_table_or_already_in_the_lease_is_refused_and_runs_nothing()
    {
        await Acquire(ScenarioControl.StopInventory);
        int before = runner.Started.Count;

        (await Add("nothing-else")).Outcome.ShouldBe(AddOutcome.UnknownChange);
        AddResult twice = await Add(ScenarioControl.StopInventory);

        twice.Outcome.ShouldBe(AddOutcome.UnknownChange);
        twice.Detail!.ShouldContain("already holds");
        runner.Started.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_change_added_with_no_lease_held_is_refused()
    {
        AddResult result = await Add();

        result.Outcome.ShouldBe(AddOutcome.NothingHeld);
        runner.Started.ShouldBeEmpty();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_change_added_to_a_lease_whose_restore_failed_is_refused()
    {
        await Acquire(ScenarioControl.ShortWaits);
        runner.FailIf = spec => spec.Arguments.Contains("--wait") && spec.Arguments.Contains("up");
        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Failed);
        runner.FailIf = null;
        int before = runner.Started.Count;

        AddResult result = await Add();

        result.Outcome.ShouldBe(AddOutcome.NothingHeld);
        result.Detail!.ShouldContain("being restored");
        runner.Started.Count.ShouldBe(before);
    }

    [Fact]
    public async Task An_added_change_that_fails_undoes_every_change_the_lease_made()
    {
        await Acquire(ScenarioControl.ShortWaits);
        runner.FailIf = spec => spec.Arguments.Contains("stop");

        AddResult result = await Add();

        result.Outcome.ShouldBe(AddOutcome.Failed);
        result.Detail!.ShouldContain("The changes were undone.");
        string[] lines = Commands();
        lines.ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        lines[^1].ShouldBe(Prefix + "up -d --no-deps --wait ordering-api");
        leases.Current.ShouldBeNull();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task An_added_change_that_never_finishes_is_ended_then_the_lease_is_undone()
    {
        await Acquire(ScenarioControl.ShortWaits, seconds: 600);
        runner.Hang = spec => spec.Arguments.Contains("stop");

        Task<AddResult> adding = Add();
        await WaitUntilAsync(() => runner.Started.Any(s => s.Arguments.Contains("stop")));
        time.Advance(ScenarioLeaseService.JobCap);
        AddResult result = await adding;

        result.Outcome.ShouldBe(AddOutcome.Failed);
        result.Detail!.ShouldContain("did not finish within 180 seconds");
        runner.Stopped.ShouldHaveSingleItem().Spec.Arguments.ShouldContain("stop");
        leases.Current.ShouldBeNull();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_request_cancelled_mid_added_change_is_undone_before_the_cancellation_ends_it()
    {
        await Acquire(ScenarioControl.ShortWaits);
        using CancellationTokenSource cancel = new();
        runner.OnStart = spec =>
        {
            if (spec.Arguments.Contains("stop"))
            {
                cancel.Cancel();
            }
        };
        runner.Hang = spec => spec.Arguments.Contains("stop");

        await Should.ThrowAsync<OperationCanceledException>(() => Add(token: cancel.Token));

        runner.Stopped.ShouldHaveSingleItem().Spec.Arguments.ShouldContain("stop");
        Commands().ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        leases.Current.ShouldBeNull();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_journal_holding_an_added_change_is_restored_whole_at_the_next_start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(
            journal,
            $$"""{"id":"abc","changes":["short-waits","stop-inventory"],"label":"a run","since":"{{time.GetUtcNow():O}}","expiresAt":"{{time.GetUtcNow().AddMinutes(5):O}}"}""",
            Token);

        await leases.ReconcileAsync();

        Commands().ShouldBe([Prefix + "up -d --no-deps --wait inventory-api", Prefix + "up -d --no-deps --wait ordering-api"]);
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_restore_that_fails_on_the_last_change_keeps_the_first_unrestored_and_the_lease()
    {
        await AcquireBoth();
        runner.FailIf = spec => spec.Arguments.Contains("up") && spec.Arguments.Contains("inventory-api");

        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Failed);

        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldNotContain(Prefix + "up -d --no-deps --wait ordering-api");
        leases.Current.ShouldNotBeNull();
        File.Exists(journal).ShouldBeTrue();
    }

    [Fact]
    public async Task A_restore_starts_the_service_again_removes_the_journal_and_frees_the_gate()
    {
        await Acquire();

        RestoreResult result = await leases.RestoreAsync();

        result.Outcome.ShouldBe(RestoreOutcome.Restored);
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
        bool started = false;
        gate.Claim("up", () =>
        {
            started = true;

            return registry.Create(new ProcessSpec("docker", [], "/"));
        });
        started.ShouldBeTrue();
    }

    [Fact]
    public async Task A_restore_with_nothing_held_is_harmless()
    {
        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.NothingHeld);
        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_lease_is_refused_naming_the_first()
    {
        await Acquire();

        AcquireResult second = await Acquire(ScenarioControl.ShortWaits);

        second.Outcome.ShouldBe(AcquireOutcome.Busy);
        second.Busy!.Kind.ShouldBe(ComposeGate.LeaseKind);
        runner.Started.ShouldNotContain(s => s.Arguments.Contains("ordering-api"));
    }

    [Fact]
    public async Task A_lease_is_refused_while_another_compose_job_runs_and_starts_nothing()
    {
        Job running = registry.Create(new ProcessSpec("docker", ["compose", "up"], "/"));
        gate.Claim("up", () => running);

        AcquireResult result = await Acquire();

        result.Outcome.ShouldBe(AcquireOutcome.Busy);
        result.Busy!.Kind.ShouldBe("up");
        runner.Started.ShouldBeEmpty();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task No_change_and_the_same_change_twice_are_refused_before_anything_runs()
    {
        (await leases.AcquireAsync([], TimeSpan.FromSeconds(60), null, Token)).Outcome.ShouldBe(AcquireOutcome.UnknownChange);
        (await leases.AcquireAsync([ScenarioControl.StopInventory, ScenarioControl.StopInventory], TimeSpan.FromSeconds(60), null, Token))
            .Outcome.ShouldBe(AcquireOutcome.UnknownChange);

        runner.Started.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nothing-else", 60)]
    [InlineData(ScenarioControl.StopInventory, 0)]
    [InlineData(ScenarioControl.StopInventory, 601)]
    public async Task A_change_off_the_table_and_a_hold_off_its_bounds_are_refused_before_anything_runs(string change, int seconds)
    {
        AcquireResult result = await Acquire(change, seconds);

        result.Outcome.ShouldBeOneOf(AcquireOutcome.UnknownChange, AcquireOutcome.BadHold);
        runner.Started.ShouldBeEmpty();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_change_that_fails_is_undone_and_leaves_no_lease()
    {
        runner.FailIf = spec => spec.Arguments.Contains("stop");

        AcquireResult result = await Acquire();

        result.Outcome.ShouldBe(AcquireOutcome.Failed);
        result.Detail!.ShouldContain("The changes were undone.");
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_request_that_is_cancelled_mid_change_is_undone_before_the_cancellation_ends_it()
    {
        using CancellationTokenSource cancel = new();
        runner.OnStart = spec =>
        {
            if (spec.Arguments.Contains("stop"))
            {
                cancel.Cancel();
            }
        };
        runner.Hang = spec => spec.Arguments.Contains("stop");

        await Should.ThrowAsync<OperationCanceledException>(() => Acquire(token: cancel.Token));

        // The change is ended before it is undone, so the restore never runs beside the command it undoes.
        runner.Stopped.ShouldHaveSingleItem().Spec.Arguments.ShouldContain("stop");
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_restore_that_fails_keeps_the_lease_the_journal_and_the_gate_and_says_why_then_a_retry_ends_it()
    {
        await Acquire();
        runner.FailIf = spec => spec.Arguments.Contains("--wait") && spec.Arguments.Contains("inventory-api") && spec.Arguments.Contains("up");

        RestoreResult failed = await leases.RestoreAsync();

        failed.Outcome.ShouldBe(RestoreOutcome.Failed);
        leases.Current!.RestoreError!.ShouldContain("exited with 1");
        File.Exists(journal).ShouldBeTrue();
        gate.Claim("down", () => throw new InvalidOperationException("the gate must stay held")).Kind.ShouldBe(ComposeGate.LeaseKind);

        runner.FailIf = null;

        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Restored);
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_change_that_never_finishes_is_ended_then_undone_and_its_lease_is_not_held_past_the_hold()
    {
        runner.Hang = spec => spec.Arguments.Contains("stop");

        Task<AcquireResult> acquiring = Acquire(seconds: 5);
        await WaitUntilAsync(() => runner.Started.Any(s => s.Arguments.Contains("stop")));
        leases.Current!.ExpiresAt.ShouldBe(time.GetUtcNow().AddSeconds(5));

        time.Advance(ScenarioLeaseService.JobCap);
        AcquireResult result = await acquiring;

        result.Outcome.ShouldBe(AcquireOutcome.Failed);
        result.Detail!.ShouldContain("did not finish within 180 seconds");
        runner.Stopped.ShouldHaveSingleItem().Spec.Arguments.ShouldContain("stop");
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
        leases.Current.ShouldBeNull();
        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_lease_whose_restore_failed_is_tried_again_on_its_own_until_it_works()
    {
        await Acquire();
        runner.FailIf = spec => spec.Arguments.Contains("--wait") && spec.Arguments.Contains("up");

        (await leases.RestoreAsync()).Outcome.ShouldBe(RestoreOutcome.Failed);
        leases.Current.ShouldNotBeNull();

        time.Advance(ScenarioLeaseService.RetryAfter);
        await WaitUntilAsync(() => leases.Current is { RestoreError: not null } && runner.Started.Count(s => s.Arguments.Contains("up")) >= 2);
        leases.Current.ShouldNotBeNull("a retry that fails again keeps the lease");

        runner.FailIf = null;
        time.Advance(ScenarioLeaseService.RetryAfter);
        await WaitUntilAsync(() => leases.Current is null);

        File.Exists(journal).ShouldBeFalse();
    }

    [Fact]
    public async Task A_restore_whose_service_is_not_running_afterwards_is_a_failure_not_a_success()
    {
        await Acquire();
        runner.PsLines = [Exited, OrderingRunning];

        RestoreResult result = await leases.RestoreAsync();

        result.Outcome.ShouldBe(RestoreOutcome.Failed);
        result.Detail!.ShouldContain("is not running after the restore");
        File.Exists(journal).ShouldBeTrue();
    }

    [Fact]
    public async Task A_lease_restores_itself_at_its_deadline()
    {
        await Acquire(seconds: 90);

        time.Advance(TimeSpan.FromSeconds(89));
        leases.Current.ShouldNotBeNull();

        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => leases.Current is null);

        File.Exists(journal).ShouldBeFalse();
        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldContain(Prefix + "up -d --no-deps --wait inventory-api");
    }

    [Fact]
    public async Task A_journal_a_dead_host_left_is_restored_at_the_next_start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(
            journal,
            $$"""{"id":"abc","change":"stop-inventory","label":"a run","since":"{{time.GetUtcNow():O}}","expiresAt":"{{time.GetUtcNow().AddMinutes(2):O}}"}""",
            Token);

        await leases.ReconcileAsync();

        runner.Started.Select(s => string.Join(' ', s.Arguments)).ShouldBe([Prefix + "up -d --no-deps --wait inventory-api", Prefix + "ps -a --format json"]);
        File.Exists(journal).ShouldBeFalse();
        leases.Current.ShouldBeNull();
        gate.Claim("up", () => registry.Create(new ProcessSpec("docker", [], "/"))).Kind.ShouldBe("up");
    }

    [Fact]
    public async Task A_start_with_no_journal_does_nothing()
    {
        await leases.ReconcileAsync();

        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_journal_naming_a_change_this_host_does_not_know_is_left_for_a_person()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        await File.WriteAllTextAsync(journal, """{"id":"abc","change":"stop-everything","since":"2026-10-16T00:00:00Z","expiresAt":"2026-10-16T00:05:00Z"}""", Token);

        await leases.ReconcileAsync();

        runner.Started.ShouldBeEmpty();
        File.Exists(journal).ShouldBeTrue();
    }

    [Fact]
    public void Every_change_on_the_table_restores_with_the_one_line_for_its_service()
    {
        ComposeService compose = new(new FakeProcessRunner(registry), Paths, time);

        foreach (ScenarioChange change in ScenarioControl.Table.Values)
        {
            string[] arguments = [.. change.Restore(compose).Spec.Arguments];

            arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "up", "-d", "--no-deps", "--wait", change.Service]);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Token);
        }

        condition().ShouldBeTrue();
    }

    /// <summary>Wraps the fake runner to see a start as it happens, to fail or hang chosen commands, and to change what ps says.</summary>
    private sealed class TestRunner(FakeProcessRunner inner, JobRegistry registry) : IProcessRunner
    {
        private readonly List<ProcessSpec> started = [];

        public Action<ProcessSpec>? OnStart { get; set; }

        public Func<ProcessSpec, bool>? FailIf { get; set; }

        public Func<ProcessSpec, bool>? Hang { get; set; }

        public string[]? PsLines { get; set; }

        public IReadOnlyList<ProcessSpec> Started => [.. started];

        public Job Start(ProcessSpec spec)
        {
            started.Add(spec);
            OnStart?.Invoke(spec);

            if (Hang?.Invoke(spec) == true)
            {
                return registry.Create(spec);
            }

            if (FailIf?.Invoke(spec) == true)
            {
                Job failed = registry.Create(spec);
                failed.Append(OutputStream.Stderr, "fake: forced failure");
                failed.MarkExited(1);

                return failed;
            }

            if (PsLines is { } lines && spec.Arguments.Contains("ps"))
            {
                Job ps = registry.Create(spec);

                foreach (string line in lines)
                {
                    ps.Append(OutputStream.Stdout, line);
                }

                ps.MarkExited(0);

                return ps;
            }

            return inner.Start(spec);
        }

        public List<Job> Stopped { get; } = [];

        public Task StopAsync(Job job, CancellationToken cancellationToken)
        {
            Stopped.Add(job);

            return inner.StopAsync(job, cancellationToken);
        }
    }
}
