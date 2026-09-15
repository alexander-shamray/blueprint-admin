# Blueprint admin, phase 4: Broker — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Broker screen that lists RabbitMQ's queues (depth, `_error` queues marked), exchanges and permissions through `rabbitmqctl` inside the Compose `rabbitmq` container, with refresh, auto-refresh and a drained indicator for `ordering-catalog-events`; and the same indicator on the API screen after a successful `PublishProduct` — in the real mode and in FakePlatform mode.

**Architecture:** `ComposeService` gains `ExecAsync`, a one-shot `exec` run to completion with the same 30-second bound `PsAsync` already has (the wait moves into one private method both use). A new `Admin.Host.Broker` namespace holds a pure `RabbitCtlJson` parser, `BrokerService` (three listings plus `IsDrained`) and `BrokerEndpoints` (`GET /api/broker/queues|exchanges|permissions`). The queues answer carries the projection's drain state, so both screens read one endpoint. FakePlatform replays three recorded `rabbitmqctl --formatter json` outputs. The SPA gets typed client calls, a shared `DrainedIndicator`, a Broker screen at `/broker`, and a drain watch on the API screen.

**Tech Stack:** as phase 3 — .NET SDK 10.0.302, C# 14, minimal APIs, `System.Text.Json` (no new packages), xunit.v3, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`; Angular 22.1.x, Vitest via `ng test`, Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` — §2.1 (broker inspection commands), §5.3 (`Exec`), §5.5 BrokerService, §5.10 (`/broker/*`), §6 (Broker screen; the API screen's drained indicator), §9, §10, §12 phase 4. The Trace screen (phase 5) is out of scope.

**Backend facts this plan relies on** (read from the sibling clone on 2026-09-15; cite, do not restate elsewhere):

- Broker inspection, workspace `run-locally.md` lines 69–79: `docker compose -f deploy/compose/docker-compose.yml exec rabbitmq rabbitmqctl list_queues name messages`, `list_exchanges name type`, `list_permissions`; error queues are `<endpoint>_error`; "After publishing a product, wait until `ordering-catalog-events` drains before placing an order for that id — Ordering prices from the projection, not from Catalog's HTTP API."
- The Compose service is `rabbitmq` (`deploy/compose/infrastructure.yml`), built from `deploy/compose/rabbitmq/Dockerfile` on `rabbitmq:4.1-management-alpine`.
- The projection queue is `Ordering.Infrastructure.Messaging.DependencyInjection.CatalogEventsQueue` = `ordering-catalog-events`. Ordering's other receive endpoints: `CommandsQueue` `ordering-commands`, `FulfilmentSagaQueue` `ordering-fulfilment-saga`, `StockEventsQueue` `ordering-stock-events`; the delayed scheduler declares `ordering-fulfilment-saga_delay` of type `x-delayed-message` (Dockerfile comment). Catalog's contracts: `Common.Contracts.Catalog.V1` `ProductPublished`, `PriceChanged`, `ProductDiscontinued`.
- Catalog's one write is `WithName("PublishProduct")` in `src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs`.
- `rabbitmqctl <list> --formatter json`, measured on `rabbitmq:4.1-management-alpine` with the backend's `definitions.json` loaded: stdout is one JSON array spread over lines — `[`, then the first row, then each further row on its own line **led by a comma**, then `]`; an empty listing is `[` and `]`. Keys are the column names. `list_exchanges name type` includes the default exchange with name `""`. `list_permissions` rows are `{"user","configure","write","read"}` for vhost `/`; with the definitions loaded the users are `catalog-svc` and `ordering-svc`.

**Decisions this plan makes where the spec is silent:**

- **The drain state rides on `GET /api/broker/queues`** as `projection`, rather than a fourth endpoint: §5.10 lists three broker endpoints, and both screens need the queue list's freshness anyway. `BrokerService.IsDrained(queues, name)` is the §5.5 method, pure over a listing.
- **Drained means the queue exists and holds zero messages.** `messages` counts ready plus unacknowledged, so a message mid-consume is not drained. A queue that is not declared is *not declared*, which is its own state: Ordering has not started, so it cannot price anything.
- **A broker that does not answer is a state** (§9): every listing carries `reachable` and `error`, as `ComposeStatus` does. Output that is not a JSON array is unreachable with "could not be parsed", never a 500.
- **`rabbitmqctl` runs with a 30-second bound**, like `ps`; the job is stopped when it does not answer.
- **Auto-refresh re-reads queues only, every 5 seconds**, with `exhaustMap` as the Stack screen does. Exchanges and permissions change only with topology, and each read is a `docker compose exec` job in the registry; three every five seconds would push Up/Down jobs out of the last fifty in under two minutes.
- **The API screen watches the drain after a 2xx `PublishProduct`**, polling queues every 2 seconds until drained, for at most 2 minutes, and stops when the editor moves on (select, restore, a new send) or the screen is left. Only the operation named `PublishProduct` triggers it: it is Catalog's one write and the only call that feeds the projection.
- **Error queues are marked in text as well as colour** (`[error]`), as the Logs screen marks stderr, and the drain states carry `[drained]`, `[waiting]`, `[not declared]`.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`. `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or editing `.cs` files and before every `dotnet build`/`dotnet test`, run `dotnet format whitespace BlueprintAdmin.slnx` from the repo root. Run `dotnet format BlueprintAdmin.slnx --verify-no-changes` before each commit.
- Test names are sentences with underscores.
- The host listens on `127.0.0.1:5300` only; never add a listener, CORS header or credential store (spec §8).
- Nothing is invented that the backend owns: every hard-coded platform fact carries a comment naming its owner file and symbol, as in "Backend facts" above.
- FakePlatform is first-class: every new endpoint works against the fakes and the Playwright smoke covers the Broker screen.
- The repository root is the worktree `C:\dev\ashamray\blueprint-admin-phase-4-broker`. All `dotnet` commands run from it; all `npm` commands from `src/Admin.Web` inside it. Before claiming a task done: `dotnet test BlueprintAdmin.slnx` green, and for SPA tasks `npm run lint`, `npm test`, `npm run build` green.
- Commit messages use `feat:`/`fix:`/`test:`/`docs:` prefixes and end with the attribution trailer the session provides. Before every commit, `git branch --show-current` must print `feat/phase-4-broker`.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Compose/CommandOutput.cs                    Task 1  one-shot result
src/Admin.Host/Compose/ComposeService.cs                   Task 1  ExecAsync; shared CompleteAsync
tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs      Task 1
src/Admin.Host/Broker/BrokerContracts.cs                   Task 2  wire records
src/Admin.Host/Broker/RabbitCtlJson.cs                     Task 2  formatter parser
src/Admin.Host/Broker/BrokerService.cs                     Task 2  listings + IsDrained
tests/Admin.Host.Tests/Broker/RabbitCtlJsonTests.cs        Task 2
tests/Admin.Host.Tests/Broker/BrokerServiceTests.cs        Task 2
src/Admin.Host/Broker/BrokerEndpoints.cs                   Task 3
src/Admin.Host/Fakes/fixtures/rabbitmq-{queues,exchanges,permissions}.json  Task 3
src/Admin.Host/Fakes/FakePlatformScripts.cs                Task 3  three exec scripts
src/Admin.Host/Admin.Host.csproj                           Task 3  embed fixtures
src/Admin.Host/Program.cs                                  Task 3  register + MapBroker
tests/Admin.Host.Tests/Broker/BrokerEndpointTests.cs       Task 3
src/Admin.Web/src/app/core/host/host-types.ts              Task 4
src/Admin.Web/src/app/core/host/host-client.ts(+spec)      Task 4
src/Admin.Web/src/app/shared/drained-indicator/drained-indicator.ts(+spec) Task 4
src/Admin.Web/src/app/features/broker/broker-page.{ts,html,css,spec.ts}    Task 4
src/Admin.Web/src/app/app.routes.ts, app.html, app.spec.ts Task 4  route + nav
src/Admin.Web/src/app/features/api/api-page.{ts,html,spec.ts}              Task 5
src/Admin.Web/e2e/broker.spec.ts, e2e/api.spec.ts          Task 6
README.md, CLAUDE.md, spec §5.5/§5.10                      Task 6
```

## Task 0 (controller, not a subagent): branch

- [x] `git worktree add ../blueprint-admin-phase-4-broker -b feat/phase-4-broker main` at `b738b82`; `npm ci` in `src/Admin.Web`; commit this plan: `git add docs/superpowers/plans/2026-09-15-blueprint-admin-phase-4-broker.md && git commit -m "docs: phase 4 broker implementation plan"`.

---

### Task 1: ComposeService.ExecAsync — a one-shot exec run to completion

**Files:**
- Create: `src/Admin.Host/Compose/CommandOutput.cs`
- Modify: `src/Admin.Host/Compose/ComposeService.cs`
- Test: `tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `Job.Completion`, `Job.Since(-1)`, `OutputLine`, `FakeProcessRunner.On/OnLongRunning` (existing).
- Produces:
  - `public sealed record CommandOutput(IReadOnlyList<string> Stdout, string? Error)` with `static Answered(IReadOnlyList<string> stdout)` and `static Failed(string error)`. `Error` is null exactly when the command exited 0.
  - `ComposeService.ExecAsync(string service, string[] args, CancellationToken cancellationToken) : Task<CommandOutput>`

- [ ] **Step 1: Write the failing tests** — append to `ComposeServiceTests`:

```csharp
    [Fact]
    public async Task ExecAsync_returns_the_stdout_lines_when_the_command_exits_zero()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl list_queues", 0, "[", "]");

        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldBeNull();
        output.Stdout.ShouldBe(["[", "]"]);
        runner.Started.Single().Arguments.ShouldBe(["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues"]);
    }

    [Fact]
    public async Task ExecAsync_fails_with_the_exit_code_when_the_command_wrote_no_stderr()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq", 1);

        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldBe("docker compose exec rabbitmq exited with 1");
        output.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecAsync_fails_with_the_last_stderr_line_when_the_command_cannot_start()
    {
        // No script: the fake exits 127 with a stderr line, the shape of a service that is not running.
        CommandOutput output = await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        output.Error.ShouldNotBeNull().ShouldStartWith("fake: no script for docker compose");
    }

    [Fact]
    public async Task ExecAsync_stops_the_job_and_fails_when_it_does_not_answer_within_30_seconds()
    {
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq");

        Task<CommandOutput> execTask = Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        CommandOutput output = await execTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        output.Error.ShouldBe("docker compose exec rabbitmq did not answer within 30 seconds");
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task ExecAsync_stops_the_job_when_the_caller_cancels()
    {
        runner.OnLongRunning("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq");
        using CancellationTokenSource cts = new();

        Task<CommandOutput> execTask = Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => execTask);
        registry.All().Single().State.ShouldBe(JobState.Exited);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~ComposeServiceTests"`
Expected: build FAILS — `CommandOutput` and `ExecAsync` do not exist.

- [ ] **Step 3: Implement**

`src/Admin.Host/Compose/CommandOutput.cs`:

```csharp
namespace Admin.Host.Compose;

/// <summary>A one-shot Compose command run to completion: its stdout when it exited 0, otherwise why it did not.</summary>
public sealed record CommandOutput(IReadOnlyList<string> Stdout, string? Error)
{
    public static CommandOutput Answered(IReadOnlyList<string> stdout) => new(stdout, null);

    public static CommandOutput Failed(string error) => new([], error);
}
```

`src/Admin.Host/Compose/ComposeService.cs` — replace `PsTimeout` and `PsAsync` with the following, keep `Up`, `Down`, `FollowLogs`, `Exec` and `Run` as they are, and add `ExecAsync` after `Exec`:

```csharp
    private static readonly TimeSpan OneShotTimeout = TimeSpan.FromSeconds(30);

    public Task<CommandOutput> ExecAsync(string service, string[] args, CancellationToken cancellationToken) =>
        CompleteAsync(Exec(service, args), $"docker compose exec {service}", cancellationToken);

    public async Task<ComposeStatus> PsAsync(CancellationToken cancellationToken)
    {
        CommandOutput output = await CompleteAsync(Run("ps", "-a", "--format", "json"), "docker compose ps", cancellationToken);

        if (output.Error is not null)
        {
            return ComposeStatus.Unreachable(output.Error);
        }

        try
        {
            return ComposeStatus.Up(ComposePsParser.Parse(output.Stdout));
        }
        catch (JsonException ex)
        {
            return ComposeStatus.Unreachable($"docker compose ps output could not be parsed: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits at most 30 seconds for a one-shot command. One that does not answer is stopped and
    /// reported rather than awaited, so a stuck daemon costs a screen one slow answer, not a hang.
    /// </summary>
    private async Task<CommandOutput> CompleteAsync(Job job, string name, CancellationToken cancellationToken)
    {
        int exitCode;

        try
        {
            exitCode = await job.Completion.WaitAsync(OneShotTimeout, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            await runner.StopAsync(job, cancellationToken);

            return CommandOutput.Failed($"{name} did not answer within 30 seconds");
        }
        catch (OperationCanceledException)
        {
            // The caller's own token cancelled the wait, not the timeout: stop the
            // orphaned process with a fresh token so the stop itself is not
            // cancelled, then let the cancellation propagate as cancellation, not
            // as a failed command.
            await runner.StopAsync(job, CancellationToken.None);

            throw;
        }

        IReadOnlyList<OutputLine> lines = job.Since(-1);

        if (exitCode != 0)
        {
            string? lastError = lines.LastOrDefault(l => l.Stream == OutputStream.Stderr)?.Text;

            return CommandOutput.Failed(lastError ?? $"{name} exited with {exitCode}");
        }

        return CommandOutput.Answered([.. lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)]);
    }
```

- [ ] **Step 4: Run all host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx`
Expected: PASS, including the existing `Ps_*` tests unchanged (their messages "docker compose ps exited with 1" and "did not answer within 30 seconds" are preserved).

- [ ] **Step 5: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Host/Compose/CommandOutput.cs src/Admin.Host/Compose/ComposeService.cs tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs
git commit -m "feat(host): ComposeService.ExecAsync runs an exec to completion within 30 s, sharing ps's wait"
```

---

### Task 2: BrokerService — queues, exchanges, permissions and IsDrained

**Files:**
- Create: `src/Admin.Host/Broker/BrokerContracts.cs`, `src/Admin.Host/Broker/RabbitCtlJson.cs`, `src/Admin.Host/Broker/BrokerService.cs`
- Test: `tests/Admin.Host.Tests/Broker/RabbitCtlJsonTests.cs`, `tests/Admin.Host.Tests/Broker/BrokerServiceTests.cs`

**Interfaces:**
- Consumes: `ComposeService.ExecAsync(string, string[], CancellationToken) : Task<CommandOutput>` (Task 1).
- Produces (all `namespace Admin.Host.Broker`; JSON is camelCase by the host's defaults):
  - `public sealed record BrokerQueue(string Name, long? Messages, bool IsErrorQueue)`
  - `public sealed record ProjectionDrain(string Queue, bool Found, long? Messages, bool Drained)`
  - `public sealed record QueuesView(bool Reachable, string? Error, IReadOnlyList<BrokerQueue> Queues, ProjectionDrain Projection)`
  - `public sealed record BrokerExchange(string Name, string Type)`
  - `public sealed record ExchangesView(bool Reachable, string? Error, IReadOnlyList<BrokerExchange> Exchanges)`
  - `public sealed record BrokerPermission(string User, string Configure, string Write, string Read)`
  - `public sealed record PermissionsView(bool Reachable, string? Error, IReadOnlyList<BrokerPermission> Permissions)`
  - `public static class RabbitCtlJson { IReadOnlyList<T> Parse<T>(IReadOnlyList<string> stdoutLines); }` — throws `JsonException` when there is no array
  - `public sealed class BrokerService(ComposeService compose)` with consts `Service = "rabbitmq"`, `ProjectionQueue = "ordering-catalog-events"`, `ErrorSuffix = "_error"`; `Task<QueuesView> QueuesAsync(CancellationToken)`, `Task<ExchangesView> ExchangesAsync(CancellationToken)`, `Task<PermissionsView> PermissionsAsync(CancellationToken)`, `static ProjectionDrain IsDrained(IReadOnlyList<BrokerQueue> queues, string queue)`

- [ ] **Step 1: Write the failing parser tests**

`tests/Admin.Host.Tests/Broker/RabbitCtlJsonTests.cs`:

```csharp
using System.Text.Json;
using Admin.Host.Broker;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class RabbitCtlJsonTests
{
    private sealed record Row(string Name, string Type);

    [Fact]
    public void A_streamed_array_with_comma_led_rows_parses_to_every_row()
    {
        IReadOnlyList<Row> rows = RabbitCtlJson.Parse<Row>(
        [
            "[",
            """{"name":"amq.topic","type":"topic"}""",
            """,{"name":"","type":"direct"}""",
            "]",
        ]);

        rows.ShouldBe([new Row("amq.topic", "topic"), new Row("", "direct")]);
    }

    [Fact]
    public void An_empty_listing_parses_to_no_rows()
    {
        RabbitCtlJson.Parse<Row>(["[", "]"]).ShouldBeEmpty();
    }

    [Fact]
    public void Lines_before_the_array_are_not_part_of_it()
    {
        IReadOnlyList<Row> rows = RabbitCtlJson.Parse<Row>(["Listing exchanges for vhost / ...", "[", """{"name":"amq.fanout","type":"fanout"}""", "]"]);

        rows.Single().Name.ShouldBe("amq.fanout");
    }

    [Fact]
    public void Output_with_no_array_is_a_json_exception()
    {
        Should.Throw<JsonException>(() => RabbitCtlJson.Parse<Row>(["Error: unable to perform an operation on node"]));
    }
}
```

- [ ] **Step 2: Write the failing service tests**

`tests/Admin.Host.Tests/Broker/BrokerServiceTests.cs`:

```csharp
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class BrokerServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private readonly FakeTimeProvider time = new();
    private readonly FakeProcessRunner runner;

    public BrokerServiceTests()
    {
        runner = new FakeProcessRunner(new JobRegistry(time));
    }

    private BrokerService Service => new(new ComposeService(runner, Paths, time));

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    [Fact]
    public async Task Queues_run_list_queues_name_messages_as_json_inside_the_rabbitmq_container()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", "]");

        await Service.QueuesAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.ShouldBe(
            ["compose", "-f", Paths.ComposeFile, "exec", "-T", "rabbitmq", "rabbitmqctl", "list_queues", "name", "messages", "--formatter", "json"]);
    }

    [Fact]
    public async Task Queues_are_listed_by_name_with_error_queues_marked()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"ordering-commands","messages":0}""",
            """,{"name":"ordering-catalog-events_error","messages":1}""",
            """,{"name":"ordering-catalog-events","messages":3}""",
            "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeTrue();
        view.Queues.ShouldBe(
        [
            new BrokerQueue("ordering-catalog-events", 3, false),
            new BrokerQueue("ordering-catalog-events_error", 1, true),
            new BrokerQueue("ordering-commands", 0, false),
        ]);
    }

    [Fact]
    public async Task The_projection_is_waiting_while_its_queue_holds_messages()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"ordering-catalog-events","messages":3}""", "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", true, 3, false));
    }

    [Fact]
    public async Task The_projection_is_drained_when_its_queue_holds_no_messages()
    {
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"ordering-catalog-events","messages":0}""", "]");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", true, 0, true));
    }

    [Fact]
    public void A_queue_that_is_not_declared_is_not_drained()
    {
        BrokerService.IsDrained([new BrokerQueue("ordering-commands", 0, false)], "ordering-catalog-events")
            .ShouldBe(new ProjectionDrain("ordering-catalog-events", false, null, false));
    }

    [Fact]
    public async Task Queues_are_unreachable_with_the_error_when_the_container_does_not_answer()
    {
        // No script: the fake exits 127 with a stderr line, as compose exec does for a stopped service.
        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldStartWith("fake: no script for docker compose");
        view.Queues.ShouldBeEmpty();
        view.Projection.ShouldBe(new ProjectionDrain("ordering-catalog-events", false, null, false));
    }

    [Fact]
    public async Task Output_that_is_not_a_json_array_is_unreachable_not_an_exception()
    {
        runner.On("docker", Exec + "list_queues", 0, "Error: unable to perform an operation on node");

        QueuesView view = await Service.QueuesAsync(TestContext.Current.CancellationToken);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("rabbitmqctl list_queues output could not be parsed");
    }

    [Fact]
    public async Task Exchanges_are_listed_with_their_type_including_the_default_exchange()
    {
        runner.On("docker", Exec + "list_exchanges", 0,
            "[", """{"name":"amq.topic","type":"topic"}""", """,{"name":"","type":"direct"}""", "]");

        ExchangesView view = await Service.ExchangesAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.TakeLast(5).ShouldBe(["list_exchanges", "name", "type", "--formatter", "json"]);
        view.Reachable.ShouldBeTrue();
        view.Exchanges.ShouldBe([new BrokerExchange("", "direct"), new BrokerExchange("amq.topic", "topic")]);
    }

    [Fact]
    public async Task Permissions_are_listed_per_user()
    {
        runner.On("docker", Exec + "list_permissions", 0,
            "[",
            """{"user":"ordering-svc","configure":"^(ordering-)","write":"^(ordering-)","read":"^(ordering-)"}""",
            """,{"user":"catalog-svc","configure":"^(MassTransit:)","write":"^(MassTransit:)","read":"^(MassTransit:)"}""",
            "]");

        PermissionsView view = await Service.PermissionsAsync(TestContext.Current.CancellationToken);

        runner.Started.Single().Arguments.TakeLast(3).ShouldBe(["list_permissions", "--formatter", "json"]);
        view.Permissions.Select(p => p.User).ShouldBe(["catalog-svc", "ordering-svc"]);
        view.Permissions[0].ShouldBe(new BrokerPermission("catalog-svc", "^(MassTransit:)", "^(MassTransit:)", "^(MassTransit:)"));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~Admin.Host.Tests.Broker"`
Expected: build FAILS — namespace `Admin.Host.Broker` does not exist.

- [ ] **Step 4: Implement**

`src/Admin.Host/Broker/BrokerContracts.cs`:

```csharp
namespace Admin.Host.Broker;

/// <summary>A queue and its depth. <see cref="Messages"/> is ready plus unacknowledged, as rabbitmqctl reports it.</summary>
public sealed record BrokerQueue(string Name, long? Messages, bool IsErrorQueue);

/// <summary>Whether a queue has caught up: declared and empty. A queue that is not declared is not drained.</summary>
public sealed record ProjectionDrain(string Queue, bool Found, long? Messages, bool Drained);

/// <summary>The queue listing. A broker that does not answer is a state, not an exception (spec §9).</summary>
public sealed record QueuesView(bool Reachable, string? Error, IReadOnlyList<BrokerQueue> Queues, ProjectionDrain Projection);

public sealed record BrokerExchange(string Name, string Type);

public sealed record ExchangesView(bool Reachable, string? Error, IReadOnlyList<BrokerExchange> Exchanges);

public sealed record BrokerPermission(string User, string Configure, string Write, string Read);

public sealed record PermissionsView(bool Reachable, string? Error, IReadOnlyList<BrokerPermission> Permissions);
```

`src/Admin.Host/Broker/RabbitCtlJson.cs`:

```csharp
using System.Text.Json;

namespace Admin.Host.Broker;

/// <summary>
/// <c>rabbitmqctl ... --formatter json</c> prints one array over several lines, every row after the
/// first led by a comma (measured on rabbitmq:4.1-management-alpine, the base of the backend's
/// deploy/compose/rabbitmq/Dockerfile). Anything printed before the line that opens it is not part of it.
/// </summary>
public static class RabbitCtlJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<T> Parse<T>(IReadOnlyList<string> stdoutLines)
    {
        int start = -1;

        for (int i = 0; i < stdoutLines.Count; i++)
        {
            if (stdoutLines[i].TrimStart().StartsWith('['))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            throw new JsonException("no JSON array in the output");
        }

        return JsonSerializer.Deserialize<List<T>>(string.Join('\n', stdoutLines.Skip(start)), Options)
            ?? throw new JsonException("the output was null");
    }
}
```

`src/Admin.Host/Broker/BrokerService.cs`:

```csharp
using System.Text.Json;
using Admin.Host.Compose;

namespace Admin.Host.Broker;

/// <summary>
/// The three broker inspections run-locally.md gives, through rabbitmqctl inside the Compose rabbitmq
/// container (spec §5.5): the management UI on 15672 has no login, so the console does not use it.
/// </summary>
public sealed class BrokerService(ComposeService compose)
{
    /// <summary>The Compose service, owned by the backend's deploy/compose/infrastructure.yml.</summary>
    public const string Service = "rabbitmq";

    /// <summary>
    /// Ordering's price projection endpoint, the backend's
    /// Ordering.Infrastructure.Messaging.DependencyInjection.CatalogEventsQueue. run-locally.md says to
    /// wait for it to drain before ordering a product just published.
    /// </summary>
    public const string ProjectionQueue = "ordering-catalog-events";

    /// <summary>run-locally.md: error queues are named <c>&lt;endpoint&gt;_error</c>.</summary>
    public const string ErrorSuffix = "_error";

    public async Task<QueuesView> QueuesAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<QueueRow>? rows, string? error) = await ListAsync<QueueRow>(["list_queues", "name", "messages"], cancellationToken);

        if (rows is null)
        {
            return new QueuesView(false, error, [], new ProjectionDrain(ProjectionQueue, false, null, false));
        }

        List<BrokerQueue> queues = [.. rows
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new BrokerQueue(r.Name, r.Messages, r.Name.EndsWith(ErrorSuffix, StringComparison.Ordinal)))];

        return new QueuesView(true, null, queues, IsDrained(queues, ProjectionQueue));
    }

    public async Task<ExchangesView> ExchangesAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<BrokerExchange>? rows, string? error) = await ListAsync<BrokerExchange>(["list_exchanges", "name", "type"], cancellationToken);

        return rows is null
            ? new ExchangesView(false, error, [])
            : new ExchangesView(true, null, [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal)]);
    }

    public async Task<PermissionsView> PermissionsAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<BrokerPermission>? rows, string? error) = await ListAsync<BrokerPermission>(["list_permissions"], cancellationToken);

        return rows is null
            ? new PermissionsView(false, error, [])
            : new PermissionsView(true, null, [.. rows.OrderBy(r => r.User, StringComparer.Ordinal)]);
    }

    public static ProjectionDrain IsDrained(IReadOnlyList<BrokerQueue> queues, string queue)
    {
        BrokerQueue? found = queues.FirstOrDefault(q => q.Name == queue);

        return new ProjectionDrain(queue, found is not null, found?.Messages, found?.Messages == 0);
    }

    private async Task<(IReadOnlyList<T>? Rows, string? Error)> ListAsync<T>(string[] command, CancellationToken cancellationToken)
    {
        CommandOutput output = await compose.ExecAsync(Service, ["rabbitmqctl", .. command, "--formatter", "json"], cancellationToken);

        if (output.Error is not null)
        {
            return (null, output.Error);
        }

        try
        {
            return (RabbitCtlJson.Parse<T>(output.Stdout), null);
        }
        catch (JsonException ex)
        {
            return (null, $"rabbitmqctl {command[0]} output could not be parsed: {ex.Message}");
        }
    }

    private sealed record QueueRow(string Name, long? Messages);
}
```

- [ ] **Step 5: Run all host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx`
Expected: PASS. If an analyser rejects a construct (for example CA1859 on a return type), change the code, not the analyser policy, and say so in the commit body.

- [ ] **Step 6: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Host/Broker tests/Admin.Host.Tests/Broker
git commit -m "feat(host): BrokerService lists queues, exchanges and permissions through rabbitmqctl and says whether ordering-catalog-events drained"
```

---

### Task 3: Broker endpoints and the FakePlatform broker

**Files:**
- Create: `src/Admin.Host/Broker/BrokerEndpoints.cs`
- Create: `src/Admin.Host/Fakes/fixtures/rabbitmq-queues.json`, `rabbitmq-exchanges.json`, `rabbitmq-permissions.json`
- Modify: `src/Admin.Host/Fakes/FakePlatformScripts.cs`, `src/Admin.Host/Admin.Host.csproj`, `src/Admin.Host/Program.cs`
- Test: `tests/Admin.Host.Tests/Broker/BrokerEndpointTests.cs`

**Interfaces:**
- Consumes: `BrokerService` (Task 2), `AdminHostFactory` (existing, FakePlatform on).
- Produces: `GET /api/broker/queues` → `QueuesView`; `GET /api/broker/exchanges` → `ExchangesView`; `GET /api/broker/permissions` → `PermissionsView`; all 200 with camelCase JSON. `FakePlatformScripts.FixtureLines(string logicalName) : IReadOnlyList<string>`. Fake broker content: 5 queues (one `_error` with 1 message, `ordering-catalog-events` with 0), 15 exchanges, 2 permission rows.

- [ ] **Step 1: Write the failing endpoint tests**

`tests/Admin.Host.Tests/Broker/BrokerEndpointTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class BrokerEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public BrokerEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Queues_lists_the_fake_brokers_queues_with_the_error_queue_marked_and_the_projection_drained()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement[] queues = [.. view.GetProperty("queues").EnumerateArray()];
        queues.Length.ShouldBe(5);
        JsonElement error = queues.Single(q => q.GetProperty("isErrorQueue").GetBoolean());
        error.GetProperty("name").GetString().ShouldBe("ordering-catalog-events_error");
        error.GetProperty("messages").GetInt64().ShouldBe(1);
        JsonElement projection = view.GetProperty("projection");
        projection.GetProperty("queue").GetString().ShouldBe("ordering-catalog-events");
        projection.GetProperty("drained").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Exchanges_lists_the_fake_brokers_exchanges_with_their_types()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/exchanges", TestContext.Current.CancellationToken);

        JsonElement[] exchanges = [.. view.GetProperty("exchanges").EnumerateArray()];
        exchanges.Length.ShouldBe(15);
        exchanges.ShouldContain(e => e.GetProperty("name").GetString() == "ordering-fulfilment-saga_delay" && e.GetProperty("type").GetString() == "x-delayed-message");
    }

    [Fact]
    public async Task Permissions_lists_the_two_service_users()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/broker/permissions", TestContext.Current.CancellationToken);

        view.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("user").GetString()).ShouldBe(["catalog-svc", "ordering-svc"]);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~BrokerEndpointTests"`
Expected: FAIL — each GET answers 404 (the `/api/{**catchAll}` route).

- [ ] **Step 3: Record the fixtures**

These are the formatter's measured shape (see "Backend facts"). `rabbitmq-permissions.json` is the measured output verbatim; the other two list the backend's own names.

`src/Admin.Host/Fakes/fixtures/rabbitmq-queues.json`:

```
[
{"name":"ordering-catalog-events","messages":0}
,{"name":"ordering-catalog-events_error","messages":1}
,{"name":"ordering-commands","messages":0}
,{"name":"ordering-fulfilment-saga","messages":0}
,{"name":"ordering-stock-events","messages":0}
]
```

`src/Admin.Host/Fakes/fixtures/rabbitmq-exchanges.json`:

```
[
{"name":"amq.topic","type":"topic"}
,{"name":"","type":"direct"}
,{"name":"amq.direct","type":"direct"}
,{"name":"amq.rabbitmq.trace","type":"topic"}
,{"name":"amq.match","type":"headers"}
,{"name":"amq.headers","type":"headers"}
,{"name":"amq.fanout","type":"fanout"}
,{"name":"ordering-catalog-events","type":"fanout"}
,{"name":"ordering-commands","type":"fanout"}
,{"name":"ordering-fulfilment-saga","type":"fanout"}
,{"name":"ordering-fulfilment-saga_delay","type":"x-delayed-message"}
,{"name":"ordering-stock-events","type":"fanout"}
,{"name":"Common.Contracts.Catalog.V1:ProductPublished","type":"fanout"}
,{"name":"Common.Contracts.Catalog.V1:PriceChanged","type":"fanout"}
,{"name":"Common.Contracts.Catalog.V1:ProductDiscontinued","type":"fanout"}
]
```

`src/Admin.Host/Fakes/fixtures/rabbitmq-permissions.json`:

```
[
{"user":"catalog-svc","configure":"^(Common\\.Contracts(\\.Catalog\\.V1:|:)|MassTransit:)","write":"^(Common\\.Contracts(\\.Catalog\\.V1:|:)|MassTransit:)","read":"^(Common\\.Contracts\\.Catalog\\.V1:|MassTransit:)"}
,{"user":"ordering-svc","configure":"^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|Ordering\\.Infrastructure\\.Messaging:|MassTransit:)","write":"^(ordering-|inventory-commands|payments-commands|Common\\.Contracts(\\.Ordering\\.V1:|:)|Ordering\\.Infrastructure\\.Messaging:|MassTransit:)","read":"^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|Ordering\\.Infrastructure\\.Messaging:|MassTransit:)"}
]
```

`src/Admin.Host/Admin.Host.csproj` — add beside the existing three `EmbeddedResource` items:

```xml
    <EmbeddedResource Include="Fakes\fixtures\rabbitmq-queues.json" LogicalName="rabbitmq-queues.json" />
    <EmbeddedResource Include="Fakes\fixtures\rabbitmq-exchanges.json" LogicalName="rabbitmq-exchanges.json" />
    <EmbeddedResource Include="Fakes\fixtures\rabbitmq-permissions.json" LogicalName="rabbitmq-permissions.json" />
```

- [ ] **Step 4: Script the fake broker and map the endpoints**

`src/Admin.Host/Fakes/FakePlatformScripts.cs` — in `Script`, after the `down` script and before `.OnLongRunning("docker", prefix + "logs -f ...`, add:

```csharp
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_queues name messages --formatter json", 0, [.. FixtureLines("rabbitmq-queues.json")])
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_exchanges name type --formatter json", 0, [.. FixtureLines("rabbitmq-exchanges.json")])
            .On("docker", prefix + "exec -T rabbitmq rabbitmqctl list_permissions --formatter json", 0, [.. FixtureLines("rabbitmq-permissions.json")])
```

and replace `ComposePsLines` with:

```csharp
    public static IReadOnlyList<string> ComposePsLines() => FixtureLines("compose-ps.jsonl");

    /// <summary>A recorded output embedded under its logical name, one entry per non-empty line.</summary>
    public static IReadOnlyList<string> FixtureLines(string logicalName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
```

`src/Admin.Host/Broker/BrokerEndpoints.cs`:

```csharp
namespace Admin.Host.Broker;

public static class BrokerEndpoints
{
    public static IEndpointRouteBuilder MapBroker(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/broker/queues", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.QueuesAsync(cancellationToken)));

        app.MapGet("/api/broker/exchanges", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.ExchangesAsync(cancellationToken)));

        app.MapGet("/api/broker/permissions", async (BrokerService broker, CancellationToken cancellationToken) =>
            TypedResults.Ok(await broker.PermissionsAsync(cancellationToken)));

        return app;
    }
}
```

`src/Admin.Host/Program.cs`: add `using Admin.Host.Broker;` in sorted position; after `builder.Services.AddSingleton<LogFollower>();` add `builder.Services.AddSingleton<BrokerService>();`; after `app.MapApi();` add `app.MapBroker();`.

- [ ] **Step 5: Run all host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx`
Expected: PASS, including the existing `FakePlatformTests` (`The_fixture_lists_thirteen_services` still reads `compose-ps.jsonl`).

- [ ] **Step 6: Smoke the real host in fake mode**

Run: `dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true` in the background, then `curl -s http://127.0.0.1:5300/api/broker/queues`; stop the host.
Expected: JSON with `"reachable":true`, five queues and `"projection":{"queue":"ordering-catalog-events","found":true,"messages":0,"drained":true}`.

- [ ] **Step 7: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Host/Broker/BrokerEndpoints.cs src/Admin.Host/Fakes src/Admin.Host/Admin.Host.csproj src/Admin.Host/Program.cs tests/Admin.Host.Tests/Broker/BrokerEndpointTests.cs
git commit -m "feat(host): /api/broker/queues, exchanges and permissions, with recorded rabbitmqctl output in FakePlatform"
```

---

### Task 4: The Broker screen

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`, `host-client.ts`, `host-client.spec.ts`
- Create: `src/Admin.Web/src/app/shared/drained-indicator/drained-indicator.ts`, `drained-indicator.spec.ts`
- Create: `src/Admin.Web/src/app/features/broker/broker-page.ts`, `broker-page.html`, `broker-page.css`, `broker-page.spec.ts`
- Modify: `src/Admin.Web/src/app/app.routes.ts`, `app.html`, `app.spec.ts`

**Interfaces:**
- Consumes: the three endpoints (Task 3).
- Produces:
  - Types `BrokerQueue { name: string; messages: number | null; isErrorQueue: boolean }`, `ProjectionDrain { queue: string; found: boolean; messages: number | null; drained: boolean }`, `QueuesView { reachable: boolean; error: string | null; queues: BrokerQueue[]; projection: ProjectionDrain }`, `BrokerExchange { name; type }`, `ExchangesView { reachable; error; exchanges }`, `BrokerPermission { user; configure; write; read }`, `PermissionsView { reachable; error; permissions }`.
  - `HostClient.brokerQueues(): Observable<QueuesView>`, `brokerExchanges(): Observable<ExchangesView>`, `brokerPermissions(): Observable<PermissionsView>`.
  - `DrainedIndicator` (selector `app-drained-indicator`, required input `projection: ProjectionDrain`), rendering `p.drained` with one of the texts `[drained]`, `[waiting]`, `[not declared]`.
  - `BrokerPage` at route `broker`, nav link "Broker" between Logs and API; `AUTO_REFRESH_MS = 5000`.

- [ ] **Step 1: Client — failing test, then types and calls**

Append to `host-client.spec.ts` inside the `describe`:

```ts
  it('reads the broker queues, exchanges and permissions', () => {
    client.brokerQueues().subscribe();
    const queues = http.expectOne('/api/broker/queues');
    expect(queues.request.method).toBe('GET');
    queues.flush({ reachable: true, error: null, queues: [], projection: { queue: 'ordering-catalog-events', found: false, messages: null, drained: false } });

    client.brokerExchanges().subscribe();
    const exchanges = http.expectOne('/api/broker/exchanges');
    expect(exchanges.request.method).toBe('GET');
    exchanges.flush({ reachable: true, error: null, exchanges: [] });

    client.brokerPermissions().subscribe();
    const permissions = http.expectOne('/api/broker/permissions');
    expect(permissions.request.method).toBe('GET');
    permissions.flush({ reachable: true, error: null, permissions: [] });
  });
```

Run: `npm test` — expected FAIL (`brokerQueues` is not a function).

Append to `host-types.ts`:

```ts
/** A queue and its depth; `messages` is ready plus unacknowledged, as rabbitmqctl reports it (spec §5.5). */
export interface BrokerQueue {
  name: string;
  messages: number | null;
  isErrorQueue: boolean;
}

/** Whether a queue has caught up: declared and empty. `ordering-catalog-events` is Ordering's price projection. */
export interface ProjectionDrain {
  queue: string;
  found: boolean;
  messages: number | null;
  drained: boolean;
}

export interface QueuesView {
  reachable: boolean;
  error: string | null;
  queues: BrokerQueue[];
  projection: ProjectionDrain;
}

export interface BrokerExchange {
  name: string;
  type: string;
}

export interface ExchangesView {
  reachable: boolean;
  error: string | null;
  exchanges: BrokerExchange[];
}

export interface BrokerPermission {
  user: string;
  configure: string;
  write: string;
  read: string;
}

export interface PermissionsView {
  reachable: boolean;
  error: string | null;
  permissions: BrokerPermission[];
}
```

In `host-client.ts` add the three types to the import and these methods before `job`:

```ts
  brokerQueues(): Observable<QueuesView> {
    return this.http.get<QueuesView>('/api/broker/queues');
  }

  brokerExchanges(): Observable<ExchangesView> {
    return this.http.get<ExchangesView>('/api/broker/exchanges');
  }

  brokerPermissions(): Observable<PermissionsView> {
    return this.http.get<PermissionsView>('/api/broker/permissions');
  }
```

Run: `npm test` — expected PASS.

- [ ] **Step 2: DrainedIndicator — failing test**

`src/Admin.Web/src/app/shared/drained-indicator/drained-indicator.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { ProjectionDrain } from '../../core/host/host-types';
import { DrainedIndicator } from './drained-indicator';

function render(projection: ProjectionDrain): string {
  const fixture = TestBed.createComponent(DrainedIndicator);
  fixture.componentRef.setInput('projection', projection);
  fixture.detectChanges();
  return (fixture.nativeElement.textContent as string).replace(/\s+/g, ' ').trim();
}

describe('DrainedIndicator', () => {
  it('says the projection has caught up when its queue is empty', () => {
    expect(render({ queue: 'ordering-catalog-events', found: true, messages: 0, drained: true }))
      .toBe("[drained] ordering-catalog-events is empty: Ordering's price projection has caught up.");
  });

  it('says why an order should wait while messages are queued', () => {
    expect(render({ queue: 'ordering-catalog-events', found: true, messages: 3, drained: false }))
      .toBe('[waiting] ordering-catalog-events holds 3 messages: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.');
  });

  it('says the queue is not declared when Ordering has not started', () => {
    expect(render({ queue: 'ordering-catalog-events', found: false, messages: null, drained: false }))
      .toBe('[not declared] ordering-catalog-events does not exist: Ordering has not started, so it cannot price an order.');
  });
});
```

Run: `npm test` — expected FAIL (cannot resolve `./drained-indicator`).

- [ ] **Step 3: DrainedIndicator — implement**

`src/Admin.Web/src/app/shared/drained-indicator/drained-indicator.ts`:

```ts
import { Component, input } from '@angular/core';
import { ProjectionDrain } from '../../core/host/host-types';

/**
 * run-locally.md: after publishing a product, wait until ordering-catalog-events drains before placing an
 * order for it, because Ordering prices from its projection, not from Catalog's HTTP API. The state is
 * given in words as well as colour.
 */
@Component({
  selector: 'app-drained-indicator',
  template: `
    @let p = projection();
    <p class="drained" [class.ok]="p.drained" [class.waiting]="!p.drained">
      @if (p.drained) {
        [drained] {{ p.queue }} is empty: Ordering's price projection has caught up.
      } @else if (!p.found) {
        [not declared] {{ p.queue }} does not exist: Ordering has not started, so it cannot price an order.
      } @else {
        [waiting] {{ p.queue }} holds {{ p.messages ?? 'some' }} {{ p.messages === 1 ? 'message' : 'messages' }}: Ordering prices orders from its projection, not from Catalog, so an order for a product just published may not find its price yet. Wait for the queue to drain.
      }
    </p>
  `,
  styles: `
    .drained { padding: 0.25rem 0.5rem; border-radius: 3px; }
    .ok { background: #dfd; }
    .waiting { background: #fec; }
  `,
})
export class DrainedIndicator {
  readonly projection = input.required<ProjectionDrain>();
}
```

Run: `npm test` — expected PASS.

- [ ] **Step 4: BrokerPage — failing tests**

`src/Admin.Web/src/app/features/broker/broker-page.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ExchangesView, PermissionsView, QueuesView } from '../../core/host/host-types';
import { BrokerPage } from './broker-page';

const queues: QueuesView = {
  reachable: true,
  error: null,
  queues: [
    { name: 'ordering-catalog-events', messages: 2, isErrorQueue: false },
    { name: 'ordering-catalog-events_error', messages: 1, isErrorQueue: true },
  ],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};

const exchanges: ExchangesView = {
  reachable: true,
  error: null,
  exchanges: [{ name: '', type: 'direct' }, { name: 'ordering-fulfilment-saga_delay', type: 'x-delayed-message' }],
};

const permissions: PermissionsView = {
  reachable: true,
  error: null,
  permissions: [{ user: 'catalog-svc', configure: '^(MassTransit:)', write: '^(MassTransit:)', read: '^(MassTransit:)' }],
};

function rows(fixture: { nativeElement: HTMLElement }, table: string): string[] {
  return (Array.from(fixture.nativeElement.querySelectorAll(`table.${table} tbody tr`)) as HTMLElement[])
    .map((r) => r.textContent?.replace(/\s+/g, ' ').trim() ?? '');
}

describe('BrokerPage', () => {
  let host: {
    brokerQueues: ReturnType<typeof vi.fn>;
    brokerExchanges: ReturnType<typeof vi.fn>;
    brokerPermissions: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    host = {
      brokerQueues: vi.fn(() => of(queues)),
      brokerExchanges: vi.fn(() => of(exchanges)),
      brokerPermissions: vi.fn(() => of(permissions)),
    };
    TestBed.configureTestingModule({ imports: [BrokerPage], providers: [{ provide: HostClient, useValue: host }] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function render() {
    const fixture = TestBed.createComponent(BrokerPage);
    fixture.detectChanges();
    return fixture;
  }

  it('lists queues with depth, marks error queues in text, and shows the drained indicator', () => {
    const fixture = render();

    expect(rows(fixture, 'queues')).toEqual(['ordering-catalog-events 2', '[error] ordering-catalog-events_error 1']);
    expect(fixture.nativeElement.querySelector('table.queues tr.error-queue')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-drained-indicator')?.textContent).toContain('[waiting]');
  });

  it('lists exchanges, naming the default exchange, and permissions per user', () => {
    const fixture = render();

    expect(rows(fixture, 'exchanges')).toEqual(['(default) direct', 'ordering-fulfilment-saga_delay x-delayed-message']);
    expect(rows(fixture, 'permissions')).toEqual(['catalog-svc ^(MassTransit:) ^(MassTransit:) ^(MassTransit:)']);
  });

  it('shows the broker error when rabbitmqctl did not answer, and no indicator', () => {
    host.brokerQueues.mockReturnValue(of({ ...queues, reachable: false, error: 'service "rabbitmq" is not running', queues: [] }));

    const fixture = render();

    expect(fixture.nativeElement.textContent).toContain('The broker did not answer: service "rabbitmq" is not running');
    expect(fixture.nativeElement.querySelector('app-drained-indicator')).toBeNull();
  });

  it('refresh reads all three again', () => {
    const fixture = render();
    (fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement).click();

    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerPermissions).toHaveBeenCalledTimes(2);
  });

  it('a failed read shows the host error and keeps the last queues on screen', () => {
    const fixture = render();
    host.brokerQueues.mockReturnValueOnce(throwError(() => ({ error: { title: 'Server error', detail: 'boom' } })));

    fixture.componentInstance.refresh();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('boom');
    expect(rows(fixture, 'queues').length).toBe(2);
  });

  it('auto-refresh re-reads queues every 5 seconds, only queues, until turned off', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).not.toHaveBeenCalled();

    fixture.componentInstance.setAutoRefresh(false);
    await vi.advanceTimersByTimeAsync(15000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
  });

  it('auto-refresh does not stack a read on one still in flight', async () => {
    vi.useFakeTimers();
    const slow = new Subject<QueuesView>();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerQueues.mockReturnValue(slow.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(15000);

    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
  });

  it('leaving the screen stops auto-refresh', async () => {
    vi.useFakeTimers();
    const fixture = render();
    fixture.componentInstance.setAutoRefresh(true);
    host.brokerQueues.mockClear();

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(15000);

    expect(host.brokerQueues).not.toHaveBeenCalled();
  });
});
```

Run: `npm test` — expected FAIL (cannot resolve `./broker-page`).

- [ ] **Step 5: BrokerPage — implement**

`src/Admin.Web/src/app/features/broker/broker-page.ts`:

```ts
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Observable, Subscription, catchError, exhaustMap, timer } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ExchangesView, PermissionsView, QueuesView } from '../../core/host/host-types';
import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';

/**
 * Queues only: exchanges and permissions change with topology, and every read is a docker compose exec job
 * the host keeps among its last fifty.
 */
export const AUTO_REFRESH_MS = 5000;

@Component({
  selector: 'app-broker-page',
  imports: [FormsModule, DrainedIndicator],
  templateUrl: './broker-page.html',
  styleUrl: './broker-page.css',
  // Keep the whitespace between adjacent <td>s so row text reads as separate cells, as the Stack screen does.
  preserveWhitespaces: true,
})
export class BrokerPage {
  private readonly host = inject(HostClient);
  private readonly destroyRef = inject(DestroyRef);
  private auto?: Subscription;

  readonly queues = signal<QueuesView | null>(null);
  readonly exchanges = signal<ExchangesView | null>(null);
  readonly permissions = signal<PermissionsView | null>(null);
  readonly autoRefresh = signal(false);
  /** A failed read; the last good listing stays on screen, and the next good queue read clears it. */
  readonly error = signal<string | null>(null);

  constructor() {
    this.destroyRef.onDestroy(() => this.auto?.unsubscribe());
    this.refresh();
  }

  refresh(): void {
    this.read(this.host.brokerQueues()).subscribe((view) => {
      this.error.set(null);
      this.queues.set(view);
    });
    this.read(this.host.brokerExchanges()).subscribe((view) => this.exchanges.set(view));
    this.read(this.host.brokerPermissions()).subscribe((view) => this.permissions.set(view));
  }

  /** `exhaustMap` skips a tick while a read is out: a slow rabbitmqctl must not queue reads behind it. */
  setAutoRefresh(on: boolean): void {
    this.autoRefresh.set(on);
    this.auto?.unsubscribe();
    this.auto = on
      ? timer(AUTO_REFRESH_MS, AUTO_REFRESH_MS)
          .pipe(exhaustMap(() => this.read(this.host.brokerQueues())))
          .subscribe((view) => {
            this.error.set(null);
            this.queues.set(view);
          })
      : undefined;
  }

  exchangeName(name: string): string {
    return name === '' ? '(default)' : name;
  }

  private read<T>(request: Observable<T>): Observable<T> {
    return request.pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError((e: unknown) => {
        this.error.set(this.describeError(e));
        return EMPTY;
      }),
    );
  }

  /** Prefers a problem-detail body's `detail`/`title`, else the HttpErrorResponse's own `message`. */
  private describeError(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
```

`src/Admin.Web/src/app/features/broker/broker-page.html`:

```html
<section class="actions">
  <button class="refresh" (click)="refresh()">Refresh</button>
  <label><input type="checkbox" [ngModel]="autoRefresh()" (ngModelChange)="setAutoRefresh($event)" /> Refresh queues every 5 s</label>
</section>

@if (error(); as e) {
  <p class="error" role="alert">The host did not answer: {{ e }}</p>
}

<h2>Queues</h2>
@if (queues(); as q) {
  @if (q.reachable) {
    <app-drained-indicator [projection]="q.projection" />
    <table class="queues">
      <thead><tr><th>Queue</th><th>Messages</th></tr></thead>
      <tbody>
        @for (queue of q.queues; track queue.name) {
          <tr [class.error-queue]="queue.isErrorQueue">
            <td>@if (queue.isErrorQueue) {<span class="marker">[error]</span> }{{ queue.name }}</td>
            <td>{{ queue.messages ?? '—' }}</td>
          </tr>
        }
      </tbody>
    </table>
  } @else {
    <p class="error">The broker did not answer: {{ q.error }}</p>
  }
} @else {
  <p>Loading…</p>
}

<h2>Exchanges</h2>
@if (exchanges(); as x) {
  @if (x.reachable) {
    <table class="exchanges">
      <thead><tr><th>Exchange</th><th>Type</th></tr></thead>
      <tbody>
        @for (exchange of x.exchanges; track exchange.name) {
          <tr>
            <td>{{ exchangeName(exchange.name) }}</td>
            <td>{{ exchange.type }}</td>
          </tr>
        }
      </tbody>
    </table>
  } @else {
    <p class="error">The broker did not answer: {{ x.error }}</p>
  }
}

<h2>Permissions</h2>
@if (permissions(); as p) {
  @if (p.reachable) {
    <table class="permissions">
      <thead><tr><th>User</th><th>Configure</th><th>Write</th><th>Read</th></tr></thead>
      <tbody>
        @for (permission of p.permissions; track permission.user) {
          <tr>
            <td>{{ permission.user }}</td>
            <td><code>{{ permission.configure }}</code></td>
            <td><code>{{ permission.write }}</code></td>
            <td><code>{{ permission.read }}</code></td>
          </tr>
        }
      </tbody>
    </table>
  } @else {
    <p class="error">The broker did not answer: {{ p.error }}</p>
  }
}
```

`src/Admin.Web/src/app/features/broker/broker-page.css`:

```css
.actions { display: flex; gap: 0.75rem; align-items: center; margin-bottom: 1rem; flex-wrap: wrap; }
h2 { font-size: 1rem; margin: 1rem 0 0.5rem; }
table { border-collapse: collapse; margin-bottom: 1rem; }
td, th { padding: 0.25rem 0.75rem; border-bottom: 1px solid #eee; text-align: left; vertical-align: top; }
tr.error-queue td { color: #b00; font-weight: 600; }
code { word-break: break-all; }
.error { color: #b00; }
```

Run: `npm test` — expected PASS.

- [ ] **Step 6: Route and nav**

`app.routes.ts` — add after the `logs` route:

```ts
  { path: 'broker', loadComponent: () => import('./features/broker/broker-page').then((m) => m.BrokerPage) },
```

`app.html` — add after the Logs link:

```html
    <a routerLink="/broker" routerLinkActive="active">Broker</a>
```

`app.spec.ts` — change `expect(nav.length).toBe(3);` to `expect(nav.length).toBe(4);`.

- [ ] **Step 7: Lint, test, build**

Run (in `src/Admin.Web`): `npm run lint; npm test; npm run build`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Web/src/app
git commit -m "feat(web): Broker screen with queues, error queues, exchanges, permissions, auto-refresh and the drained indicator"
```

---

### Task 5: The drained indicator on the API screen

**Files:**
- Modify: `src/Admin.Web/src/app/features/api/api-page.ts`, `api-page.html`, `api-page.spec.ts`

**Interfaces:**
- Consumes: `HostClient.brokerQueues()`, `QueuesView`, `ProjectionDrain`, `DrainedIndicator` (Task 4).
- Produces: `export const PUBLISH_OPERATION = 'PublishProduct'`, `DRAIN_POLL_MS = 2000`, `DRAIN_WATCH_MS = 120_000`; `ApiPage.projection: Signal<ProjectionDrain | null>`, `ApiPage.projectionError: Signal<string | null>`.

- [ ] **Step 1: Write the failing tests**

In `api-page.spec.ts`: import `QueuesView` from host-types; add `brokerQueues: ReturnType<typeof vi.fn>;` to the `host` type; in `beforeEach` add `brokerQueues: vi.fn(() => of(drainedView)),` to the object; add `afterEach(() => vi.useRealTimers());` after `beforeEach`; and define above `describe`:

```ts
const drainedView: QueuesView = {
  reachable: true, error: null, queues: [],
  projection: { queue: 'ordering-catalog-events', found: true, messages: 0, drained: true },
};

const waitingView: QueuesView = {
  ...drainedView,
  projection: { queue: 'ordering-catalog-events', found: true, messages: 2, drained: false },
};
```

Append these tests inside the `describe`:

```ts
  it('after a successful PublishProduct it watches ordering-catalog-events until it drains', async () => {
    vi.useFakeTimers();
    host.brokerQueues.mockReturnValueOnce(of(waitingView)).mockReturnValue(of(drainedView));
    const fixture = render();
    fixture.componentInstance.chooseIdentity('user:demo');
    fixture.componentInstance.select(catalog.operations[1]);

    fixture.componentInstance.send();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.response app-drained-indicator')?.textContent).toContain('[waiting]');

    await vi.advanceTimersByTimeAsync(2000);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.response app-drained-indicator')?.textContent).toContain('[drained]');

    await vi.advanceTimersByTimeAsync(10000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
  });

  it('reads no broker after a send that is not PublishProduct', async () => {
    vi.useFakeTimers();
    const fixture = render();
    fixture.componentInstance.select(catalog.operations[0]);

    fixture.componentInstance.send();
    await vi.advanceTimersByTimeAsync(5000);
    fixture.detectChanges();

    expect(host.brokerQueues).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('app-drained-indicator')).toBeNull();
  });

  it('reads no broker after a PublishProduct the platform refused', async () => {
    vi.useFakeTimers();
    host.proxy.mockReturnValue(of({ ...responded, status: 403 }));
    const fixture = render();
    fixture.componentInstance.select(catalog.operations[1]);

    fixture.componentInstance.send();
    await vi.advanceTimersByTimeAsync(5000);

    expect(host.brokerQueues).not.toHaveBeenCalled();
  });

  it('selecting another operation stops watching and hides the indicator', async () => {
    vi.useFakeTimers();
    host.brokerQueues.mockReturnValue(of(waitingView));
    const fixture = render();
    fixture.componentInstance.select(catalog.operations[1]);
    fixture.componentInstance.send();
    await vi.advanceTimersByTimeAsync(0);

    fixture.componentInstance.select(catalog.operations[0]);
    host.brokerQueues.mockClear();
    await vi.advanceTimersByTimeAsync(10000);
    fixture.detectChanges();

    expect(host.brokerQueues).not.toHaveBeenCalled();
    expect(fixture.componentInstance.projection()).toBeNull();
  });

  it('stops watching after two minutes even if the queue never drains', async () => {
    vi.useFakeTimers();
    host.brokerQueues.mockReturnValue(of(waitingView));
    const fixture = render();
    fixture.componentInstance.select(catalog.operations[1]);
    fixture.componentInstance.send();

    await vi.advanceTimersByTimeAsync(120_000);
    host.brokerQueues.mockClear();
    await vi.advanceTimersByTimeAsync(10000);

    expect(host.brokerQueues).not.toHaveBeenCalled();
    expect(fixture.componentInstance.projection()?.drained).toBe(false);
  });

  it('says so when the broker cannot be read after a publish', async () => {
    vi.useFakeTimers();
    host.brokerQueues.mockReturnValue(of({ ...drainedView, reachable: false, error: 'service "rabbitmq" is not running' }));
    const fixture = render();
    fixture.componentInstance.select(catalog.operations[1]);

    fixture.componentInstance.send();
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.response')?.textContent).toContain('Could not read the broker to say whether ordering-catalog-events has drained: service "rabbitmq" is not running');
    expect(fixture.nativeElement.querySelector('app-drained-indicator')).toBeNull();
  });
```

Run: `npm test` — expected the new tests FAIL (no indicator, `projection` undefined).

- [ ] **Step 2: Implement**

`api-page.ts`:

- Imports: add `EMPTY, catchError, exhaustMap, takeUntil, takeWhile, timer` to the `rxjs` import; add `ProjectionDrain` and `ProxyResult` is already imported; add `import { DrainedIndicator } from '../../shared/drained-indicator/drained-indicator';` and put `DrainedIndicator` in the component's `imports` beside `FormsModule`.
- After `export const UUID ...` add:

```ts
/**
 * Catalog's one write, `WithName("PublishProduct")` in the backend's Catalog.Api/Endpoints/ProductEndpoints.cs:
 * its event is what Ordering's price projection consumes from ordering-catalog-events (run-locally.md).
 */
export const PUBLISH_OPERATION = 'PublishProduct';
export const DRAIN_POLL_MS = 2000;
/** Each poll is a docker compose exec on the host; a queue that has not drained in two minutes needs the Broker screen. */
export const DRAIN_WATCH_MS = 120_000;
```

- Fields, after `private seq = 0;`: `private watchingProjection?: Subscription;`
- Signals, after `readonly customPassword = signal('');`:

```ts
  /** ordering-catalog-events after a successful publish, until it drains; null when not watching. */
  readonly projection = signal<ProjectionDrain | null>(null);
  readonly projectionError = signal<string | null>(null);
```

- In the constructor's `onDestroy` callback, first line: `this.watchingProjection?.unsubscribe();`
- In `select(...)` and `restore(...)`, right after `this.editorGeneration++;`: `this.stopWatchingProjection();`
- In `send()`, right after `this.result.set(null);`: `this.stopWatchingProjection();`
- In `send()`'s `next` handler, inside `if (current()) { ... }` after `this.pending.set(false);`:

```ts
          if (operation?.name === PUBLISH_OPERATION && result.outcome === 'responded' && result.status >= 200 && result.status < 300) {
            this.watchProjection();
          }
```

- Private methods, after `clearToken()`:

```ts
  /** Polls queues until the projection drains, the watch times out, or the editor moves on. */
  private watchProjection(): void {
    this.stopWatchingProjection();
    this.watchingProjection = timer(0, DRAIN_POLL_MS)
      .pipe(
        takeUntil(timer(DRAIN_WATCH_MS)),
        exhaustMap(() =>
          this.host.brokerQueues().pipe(
            catchError((e: unknown) => {
              this.projectionError.set(this.describe(e));
              return EMPTY;
            }),
          ),
        ),
        takeWhile((view) => !(view.reachable && view.projection.drained), true),
      )
      .subscribe((view) => {
        this.projectionError.set(view.reachable ? null : view.error);
        this.projection.set(view.reachable ? view.projection : null);
      });
  }

  private stopWatchingProjection(): void {
    this.watchingProjection?.unsubscribe();
    this.watchingProjection = undefined;
    this.projection.set(null);
    this.projectionError.set(null);
  }
```

`api-page.html` — inside the `@case ('responded')` section, after the `bodyError` line and before `</section>`:

```html
            @if (projection(); as p) {<app-drained-indicator [projection]="p" />}
            @if (projectionError(); as pe) {<p class="note">Could not read the broker to say whether ordering-catalog-events has drained: {{ pe }}</p>}
```

- [ ] **Step 3: Lint, test, build**

Run (in `src/Admin.Web`): `npm run lint; npm test; npm run build`
Expected: all green, including every pre-existing `ApiPage` test.

- [ ] **Step 4: Commit**

```bash
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Web/src/app/features/api
git commit -m "feat(web): after a successful PublishProduct the API screen watches ordering-catalog-events until it drains"
```

---

### Task 6: Playwright smoke and documents

**Files:**
- Create: `src/Admin.Web/e2e/broker.spec.ts`
- Modify: `src/Admin.Web/e2e/api.spec.ts`, `README.md`, `CLAUDE.md`, `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`

**Interfaces:**
- Consumes: everything above, against the FakePlatform host the Playwright config already starts.

- [ ] **Step 1: The Broker smoke**

`src/Admin.Web/e2e/broker.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

// Against the fake platform's recorded rabbitmqctl output (src/Admin.Host/Fakes/fixtures/rabbitmq-*.json):
// five queues, one of them an _error queue holding a message, ordering-catalog-events empty.
test('the broker screen lists queues, exchanges and permissions', async ({ page }) => {
  await page.goto('/broker');

  await expect(page.locator('table.queues tbody tr')).toHaveCount(5);
  await expect(page.locator('table.queues tbody tr.error-queue')).toHaveCount(1);
  await expect(page.locator('table.queues tbody tr.error-queue')).toContainText('[error] ordering-catalog-events_error');
  await expect(page.locator('app-drained-indicator')).toContainText('[drained] ordering-catalog-events');
  await expect(page.locator('table.exchanges tbody tr')).toHaveCount(15);
  await expect(page.locator('table.exchanges tbody tr', { hasText: 'ordering-fulfilment-saga_delay' })).toContainText('x-delayed-message');
  await expect(page.locator('table.permissions tbody tr')).toHaveCount(2);

  await page.getByRole('button', { name: 'Refresh' }).click();
  await expect(page.locator('table.queues tbody tr')).toHaveCount(5);
});
```

In `e2e/api.spec.ts`, after `await expect(page.locator('.response .status')).toHaveText('200');` that follows `selectOption('user:demo')`, add:

```ts
  // run-locally.md: wait for ordering-catalog-events to drain before ordering; the fake queue is empty.
  await expect(page.locator('.response app-drained-indicator')).toContainText('[drained]');
```

- [ ] **Step 2: Run the smoke**

Run (in `src/Admin.Web`): `npm run e2e`
Expected: every spec passes, `broker.spec.ts` included.

- [ ] **Step 3: Documents**

`README.md`:
- "What it does today": change "Phases 0 to 3" to "Phases 0 to 4", add a sentence that the Broker screen lists queues (error queues marked), exchanges and permissions through `rabbitmqctl` in the `rabbitmq` container, and that the API screen shows whether `ordering-catalog-events` has drained after a publish; change "Broker inspection and the event trace are the spec's later phases." to "The event trace is the spec's next phase."
- "Known limits": replace "No broker inspection or event trace yet." with "No event trace yet." and add "Broker reads are `docker compose exec` jobs, so they appear in `GET /api/jobs`; auto-refresh re-reads queues only."

`CLAUDE.md`, "Owners of facts" paragraph — after the sentence about `RunLocallyExamples.cs`, add: "The broker's Compose service and the projection queue name are copied, with their owners cited, in `src/Admin.Host/Broker/BrokerService.cs`; the operation that starts the API screen's drain watch is `PUBLISH_OPERATION` in `src/Admin.Web/src/app/features/api/api-page.ts`."

Spec amendments (the spec is amended where the code settled what it left open; keep the rest):
- §5.5: replace "and answers `IsDrained("ordering-catalog-events")` for the API screen's "wait for the projection" affordance." with "and answers `IsDrained(queues, "ordering-catalog-events")` — declared and holding no messages — which `GET /broker/queues` carries as `projection` for the Broker screen and the API screen's "wait for the projection" affordance. A broker that does not answer, or output that is not the formatter's JSON array, is `reachable: false` with the error, and each `rabbitmqctl` is bounded to 30 s like `ps`."
- §5.10: change the `/broker/*` row's "Does" cell from "§5.5" to "§5.5; `queues` also carries `projection`, the drain state of `ordering-catalog-events`".

- [ ] **Step 4: Full verification**

Run: `dotnet format BlueprintAdmin.slnx --verify-no-changes; dotnet test BlueprintAdmin.slnx`; in `src/Admin.Web`: `npm run lint; npm test; npm run build; npm run e2e`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git branch --show-current   # feat/phase-4-broker
git add src/Admin.Web/e2e README.md CLAUDE.md docs/superpowers/specs/2026-09-14-blueprint-admin-design.md
git commit -m "test(e2e): Broker screen and drained-indicator smoke; docs: phase 4 in README, CLAUDE.md and spec §5.5, §5.10"
```
