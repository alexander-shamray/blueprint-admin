using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class DeadLetterServiceTests : IAsyncDisposable
{
    private const string Queue = "ordering-catalog-events_error";

    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private static readonly string SetPassword = Exec + "change_password dead-letter-operator ";
    private static readonly string ClearPassword = Exec + "clear_password dead-letter-operator";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeProcessRunner runner;
    private readonly DeadLetterService service;
    private readonly string recordDir = Path.Combine(Path.GetTempPath(), $"dead-letter-tests-{Guid.NewGuid():N}");

    public DeadLetterServiceTests()
    {
        JobRegistry registry = new(time);
        runner = new FakeProcessRunner(registry);
        service = new DeadLetterService(new ComposeService(runner, Paths, time), runner, registry, Paths, recordDir, time);
    }

    public async ValueTask DisposeAsync()
    {
        await runner.DisposeAsync();

        if (Directory.Exists(recordDir))
        {
            Directory.Delete(recordDir, recursive: true);
        }
    }

    private FakeProcessRunner Password() => runner.On("docker", SetPassword, 0, "Changing password").On("docker", ClearPassword, 0, "Clearing password");

    [Fact]
    public async Task An_inspection_gives_the_operator_a_password_runs_the_tool_with_it_and_takes_it_away()
    {
        Password().On("py", DeadLetterRecordings.Prefix + "inspect ", 0, DeadLetterRecordings.Inspect);

        DeadLetterInspection inspection = (await service.InspectAsync(Queue, TestContext.Current.CancellationToken))!;

        inspection.Error.ShouldBeNull();
        inspection.Endpoint.ShouldBe("ordering-catalog-events");
        inspection.Kind.ShouldBe("error");
        DeadLetterMessage message = inspection.Messages.ShouldHaveSingleItem();
        message.MessageId.ShouldBe(DeadLetterRecordings.FaultedId);
        message.CorrelationId.ShouldBe("fake-corr-0002");
        message.MessageType.ShouldBe("urn:message:Common.Contracts.Catalog.V1:PriceChanged");
        message.FaultExceptionType.ShouldBe("System.InvalidOperationException");
        message.Redelivered.ShouldBeTrue();

        IReadOnlyList<ProcessSpec> started = runner.Started;
        started.Select(s => s.FileName).ShouldBe(["docker", "py", "docker"]);
        string password = started[0].Arguments[^1];
        password.Length.ShouldBe(32);
        started[1].Arguments.ShouldBe(["-3.12", DeadLetterService.Tool, "inspect", Queue, "--limit", "20", "--json"]);
        started[1].WorkingDirectory.ShouldBe(Paths.BackendDir);
        started[1].Environment!["DEAD_LETTERS_PASSWORD"].ShouldBe(password);
        started[1].Secrets.ShouldContain(password);
        started[1].Arguments.ShouldNotContain(password);
        string.Join(' ', started[2].Arguments).ShouldEndWith("clear_password dead-letter-operator");

        // The operator caused all three, so each is listed for the job list and the transcript, the password scrubbed.
        started.ShouldAllBe(s => s.Listed);
        started[0].CommandLine.ShouldEndWith("change_password dead-letter-operator <scrubbed>");
        started[0].CommandLine.ShouldNotContain(password);
    }

    [Fact]
    public async Task A_set_cancelled_while_it_waits_is_still_followed_by_a_clear()
    {
        runner.OnLongRunning("docker", SetPassword).On("docker", ClearPassword, 0, "Clearing password");
        using CancellationTokenSource cancel = new();

        Task<DeadLetterInspection?> inspecting = service.InspectAsync(Queue, cancel.Token);
        await WaitForAsync(() => runner.Started.Any(s => s.Arguments.Contains("change_password")));
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(inspecting);
        runner.Started[^1].Arguments.ShouldContain("clear_password");
        runner.Started.ShouldNotContain(s => s.FileName == "py");
    }

    [Fact]
    public async Task Each_run_has_a_password_of_its_own()
    {
        Password().On("py", DeadLetterRecordings.Prefix + "inspect ", 0, DeadLetterRecordings.Inspect);

        await service.InspectAsync(Queue, TestContext.Current.CancellationToken);
        await service.InspectAsync(Queue, TestContext.Current.CancellationToken);

        string[] passwords = [.. runner.Started.Where(s => s.Arguments.Contains("change_password")).Select(s => s.Arguments[^1])];
        passwords.Length.ShouldBe(2);
        passwords[0].ShouldNotBe(passwords[1]);
    }

    [Fact]
    public async Task A_tool_that_refuses_is_the_inspections_error_and_the_password_is_still_taken_away()
    {
        Password().OnFailing("py", DeadLetterRecordings.Prefix + "inspect ", 2, "dead_letters: the broker refused the credential");

        DeadLetterInspection inspection = (await service.InspectAsync(Queue, TestContext.Current.CancellationToken))!;

        inspection.Error.ShouldBe("dead_letters: the broker refused the credential");
        inspection.Messages.ShouldBeEmpty();
        runner.Started[^1].Arguments.ShouldContain("clear_password");
    }

    [Fact]
    public async Task A_password_that_cannot_be_set_runs_no_tool_and_is_still_cleared_in_case_it_landed()
    {
        runner.OnFailing("docker", SetPassword, 1, "Error: user dead-letter-operator does not exist").On("docker", ClearPassword, 0, "Clearing password");

        DeadLetterInspection inspection = (await service.InspectAsync(Queue, TestContext.Current.CancellationToken))!;

        inspection.Error!.ShouldStartWith("The operator's password could not be set: Error: user dead-letter-operator does not exist");
        runner.Started.ShouldNotContain(s => s.FileName == "py");
        runner.Started[^1].Arguments.ShouldContain("clear_password");
    }

    [Fact]
    public async Task A_password_that_cannot_be_cleared_is_said_with_the_line_that_clears_it()
    {
        runner.On("docker", SetPassword, 0, "Changing password")
            .OnFailing("docker", ClearPassword, 1, "Error: the broker went away")
            .On("py", DeadLetterRecordings.Prefix + "inspect ", 0, DeadLetterRecordings.Inspect);

        DeadLetterInspection inspection = (await service.InspectAsync(Queue, TestContext.Current.CancellationToken))!;

        inspection.Messages.ShouldHaveSingleItem();
        string error = inspection.Error.ShouldNotBeNull();
        error.ShouldContain("The operator's password could not be cleared (Error: the broker went away); if it was set, it still is");
        error.ShouldContain("rabbitmqctl clear_password dead-letter-operator");
    }

    [Fact]
    public async Task A_dry_run_names_the_messages_passes_no_execute_and_writes_no_file()
    {
        Password().On("py", DeadLetterRecordings.Prefix + "replay ", 0, rest => DeadLetterRecordings.Move(["replay", .. rest]));

        Job job = service.Start(DeadLetterVerb.Replay, Queue, [DeadLetterRecordings.FaultedId], execute: false)!;
        (await job.Completion).ShouldBe(0);

        ProcessSpec tool = runner.Started.Single(s => s.FileName == "py");
        tool.Arguments.ShouldBe(["-3.12", DeadLetterService.Tool, "replay", Queue, "--message-id", DeadLetterRecordings.FaultedId]);
        string[] output = [.. job.Since(-1).Select(l => l.Text)];
        output[0].ShouldBe("$ docker compose exec rabbitmq rabbitmqctl change_password dead-letter-operator <scrubbed>");
        output.ShouldContain(l => l.StartsWith("would-replay", StringComparison.Ordinal) && l.Contains("-> ordering-catalog-events"));
        output[^1].ShouldBe("$ docker compose exec rabbitmq rabbitmqctl clear_password dead-letter-operator");
        string password = runner.Started[0].Arguments[^1];
        output.ShouldNotContain(l => l.Contains(password));
        Directory.Exists(recordDir).ShouldBeFalse();
    }

    [Fact]
    public async Task The_real_run_records_each_message_and_audits_it_under_the_record_directory()
    {
        Password().On("py", DeadLetterRecordings.Prefix + "discard ", 0, rest => DeadLetterRecordings.Move(["discard", .. rest]));

        Job job = service.Start(DeadLetterVerb.Discard, Queue, [DeadLetterRecordings.FaultedId], execute: true)!;
        (await job.Completion).ShouldBe(0);

        string record = Path.Combine(recordDir, $"20261015-120000-{Queue}-discard.record.jsonl");
        ProcessSpec tool = runner.Started.Single(s => s.FileName == "py");
        tool.Arguments.ShouldBe(
        [
            "-3.12", DeadLetterService.Tool, "discard", Queue, "--message-id", DeadLetterRecordings.FaultedId,
            "--execute", "--record", record, "--audit-log", Path.Combine(recordDir, "audit.jsonl"),
        ]);
        Directory.Exists(recordDir).ShouldBeTrue();
        job.Since(-1).ShouldContain(l => l.Text == $"Each message is written to {record} before it leaves {Queue}; keep that file.");
    }

    [Fact]
    public async Task An_unrecorded_line_is_kept_beside_the_record_and_named_as_the_last_copy()
    {
        const string Unrecorded = """unrecorded {"queue": "ordering-catalog-events_error", "message": {"payload": "e30="}}""";
        Password().OnFailing("py", DeadLetterRecordings.Prefix + "replay ", 1, Unrecorded);

        Job job = service.Start(DeadLetterVerb.Replay, Queue, [DeadLetterRecordings.FaultedId], execute: true)!;
        (await job.Completion).ShouldBe(1);

        string kept = Path.Combine(recordDir, $"20261015-120000-{Queue}-replay.unrecorded.jsonl");
        (await File.ReadAllTextAsync(kept, TestContext.Current.CancellationToken))
            .ShouldBe("""{"queue": "ordering-catalog-events_error", "message": {"payload": "e30="}}""" + "\n");
        job.Since(-1).ShouldContain(l => l.Stream == OutputStream.Stderr && l.Text.Contains($"last copy of a message the tool could not record; it is kept in {kept}"));
        runner.Started[^1].Arguments.ShouldContain("clear_password");
    }

    [Fact]
    public async Task An_unrecorded_line_that_cannot_be_kept_is_said_and_the_tool_is_followed_to_its_end()
    {
        const string Unrecorded = """unrecorded {"queue": "ordering-catalog-events_error", "message": {}}""";
        Password().OnFailing("py", DeadLetterRecordings.Prefix + "replay ", 1, Unrecorded, "replayed       later-line");

        // A directory where the file would go makes the append fail.
        string kept = Path.Combine(recordDir, $"20261015-120000-{Queue}-replay.unrecorded.jsonl");
        Directory.CreateDirectory(kept);

        Job job = service.Start(DeadLetterVerb.Replay, Queue, [DeadLetterRecordings.FaultedId], execute: true)!;
        (await job.Completion).ShouldBe(1);

        string[] output = [.. job.Since(-1).Select(l => l.Text)];
        output.ShouldContain(l => l.Contains($"{kept} could not be written") && l.EndsWith("copy the line now.", StringComparison.Ordinal));
        output.ShouldContain("replayed       later-line");
        output[^1].ShouldEndWith("clear_password dead-letter-operator");
    }

    [Fact]
    public async Task One_run_at_a_time_holds_the_operator_and_the_next_waits_for_it_to_end()
    {
        Password().OnLongRunning("py", DeadLetterRecordings.Prefix + "replay ");

        Job first = service.Start(DeadLetterVerb.Replay, Queue, [DeadLetterRecordings.FaultedId], execute: false)!;
        await WaitForAsync(() => runner.Started.Any(s => s.FileName == "py"));

        service.Start(DeadLetterVerb.Discard, Queue, [DeadLetterRecordings.FaultedId], execute: false).ShouldBeNull();
        (await service.InspectAsync(Queue, TestContext.Current.CancellationToken)).ShouldBeNull();

        await runner.StopAsync(runner.LastStarted!, TestContext.Current.CancellationToken);
        await first.Completion;
        runner.Started[^1].Arguments.ShouldContain("clear_password");
        await WaitForAsync(() => service.Start(DeadLetterVerb.Replay, Queue, [DeadLetterRecordings.FaultedId], execute: false) is not null);
    }

    [Theory]
    [InlineData("ordering-catalog-events_error", true)]
    [InlineData("bff-order-events_skipped", true)]
    [InlineData("ordering-catalog-events", false)]
    [InlineData("_error", false)]
    [InlineData("Ordering_error", false)]
    [InlineData("ordering-catalog-events_error --all", false)]
    [InlineData("ordering-catalog-events_error\n", false)]
    public void Only_an_endpoints_parked_queue_is_handed_to_the_tool(string queue, bool parked) =>
        DeadLetterService.IsParkedQueue(queue).ShouldBe(parked);

    [Theory]
    [InlineData("0199b2a0-5c1d-7e2f-8a3b-4c5d6e7f8a90", true)]
    [InlineData("--execute", false)]
    [InlineData("-x", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("abc\n", false)]
    public void A_message_id_never_reads_as_a_flag(string id, bool accepted) => DeadLetterService.IsMessageId(id).ShouldBe(accepted);

    [Fact]
    public void A_document_of_another_schema_is_said_rather_than_read()
    {
        DeadLetterInspection inspection = DeadLetterService.Parse(Queue, ["""{"schema": 2, "command": "inspect", "messages": []}"""]);

        inspection.Error!.ShouldContain("not schema 1");
    }

    [Fact]
    public void A_refusal_document_is_the_inspections_error()
    {
        DeadLetterInspection inspection = DeadLetterService.Parse(Queue, ["""{"schema": 1, "command": "inspect", "error": "no such queue"}"""]);

        inspection.Error.ShouldBe("no such queue");
    }

    /// <summary>Asks until the condition holds, at most two seconds; each call is one ask, so a condition may start something.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the condition did not hold within two seconds");
    }
}
