# Known limits, jobs: stoppable follow and quiet reads — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop on the Logs screen ends the host's `docker compose logs -f`, not
only the browser's stream, and so does leaving the screen. The Compose reads
behind the Stack and Broker screens (`ps`, `exec`) no longer fill the job
registry, so the Broker screen's auto-refresh can re-read exchanges and
permissions as well as queues.

**Architecture:** `LogFollower` gains `StopAsync(jobId)`, which stops the
follow job it names only while that job is still the current one. It is
exposed as `POST /api/logs/follow/{id}/stop`. `ProcessSpec` gains an init-only
`Listed` flag. `JobRegistry.Create` does not keep an unlisted job, and
`ComposeService` starts `ps` and `exec` unlisted. The Logs page calls the stop
endpoint from Stop and from its destroy hook. The Broker page's auto-refresh
tick becomes a `refresh()`.

**Tech Stack:** as phase 5: .NET SDK 10.0.302, C# 14, minimal APIs, xunit.v3,
Shouldly, `Microsoft.AspNetCore.Mvc.Testing`,
`Microsoft.Extensions.Time.Testing`; Angular 22.1.6, Vitest 4.1.11 via
`ng test`, Playwright 1.63.0. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`:
- §5.2: jobs, the registry and the trim past 50.
- §5.3: `Exec`.
- §5.5: Broker.
- §5.10: `POST /logs/follow`.
- §6: the Logs and Broker rows.

The plan index is `2026-09-19-known-limits-index.md`.

---

## What the code does today (read on 2026-09-19 at `e8e4f57`; cite, do not restate elsewhere)

**F1: Stop on the Logs screen is browser-only.**
- `LogsPage.stop()` (`src/Admin.Web/src/app/features/logs/logs-page.ts`)
  unsubscribes and resets two signals. Nothing reaches the host.
- `LogFollower` (`src/Admin.Host/Stack/LogFollower.cs`) holds `current` and
  stops it only inside `StartAsync`, when the next Follow arrives.
- `ProcessRunner`'s disposal is the only other thing that ends it.
- The page's destroy hook only unsubscribes too, so leaving the Logs screen
  also leaves a `logs -f` running.

**F2: every Compose read is a registered job.**
- `ComposeService.Run` (`src/Admin.Host/Compose/ComposeService.cs`) starts
  every command through `IProcessRunner.Start`.
- Both runners call `JobRegistry.Create` on every start:
  `ProcessRunner.cs:61` and `FakeProcessRunner.cs:58`.
- `JobRegistry` trims exited jobs past `KeepExited = 50`, oldest first.
- The Stack screen polls `GET /api/stack` every 3 s (`timer(0, 3000)` in
  `stack-page.ts`), and every poll runs `ps`. That alone pushes 50 exited
  jobs through the registry in 150 s.
- The Broker screen's auto-refresh adds one `exec` per 5 s tick. The API and
  Scenario screens' drain watch adds more.
- **What that costs a person:**
  - `GET /api/stack` reports the frontend's job through
    `FrontendSupervisor.Status()`, which holds the `Job` itself. But "Show
    output" (`StackPage.showFrontendOutput`) opens
    `/api/jobs/{id}/stream`, which looks the id up in the registry.
  - So an `npm start` that died stops being readable within about two and a
    half minutes of the Stack screen being open. That output is what says
    why it died.
  - The same applies to a finished `up` or `down`.
  - `broker-page.ts`'s `AUTO_REFRESH_MS` comment gives this as the reason
    auto-refresh reads queues only.

**F3: nothing in the SPA reads `GET /api/jobs`.**
- `HostClient.job(id)` and `SseClient.follow(jobId)` are the only job
  calls, and both go by id.
- A `ps` or `exec` job's id is never handed to a client. `CompleteAsync`
  reads the `Job` object directly.

## Decisions this plan makes where the spec is silent, or where it moves

- **A stop names the follow job; there is no generic job stop.** The
  endpoint is `POST /api/logs/follow/{id}/stop`.
  - It ends the job only if the id is `LogFollower`'s current job and that
    job is still running. It answers **204 in every case**, so it is
    idempotent.
  - Naming the job is what makes a late request harmless. The Stop request
    and the next Follow request travel on separate connections and can
    reach the host in either order. A Stop that arrives after the next
    Follow has started names the job that Follow already stopped, and ends
    nothing.
  - A current-job stop without an id would end the new follow in that
    order.
  - `POST /api/jobs/{id}/stop` was the obvious alternative and is refused.
    - It would end `compose up -d --wait` half way.
    - It would end `down -v` between volumes.
    - It would end the `npm start` that `FrontendSupervisor` owns, behind
      the supervisor's back.
    - None of those has a reason to be killed from the Logs screen, and
      the host has no way to tell that caller from another.
- **The one-follow rule stays, and the page no longer leans on it alone.**
  `LogFollower.StartAsync` still stops the previous follow, but only once
  the next request reaches the host, and three orderings defeat that as the
  page's only defence:
  - The replacement request fails before it reaches the host: the previous
    job runs on, and the page has already let go of it.
  - A cancelled request still reaches the host after its replacement: it
    stops the replacement's job and starts one the page never reads.
  - The response is lost: the host started a job whose id the page never
    learns.

  So a page has **at most one follow request out**. Follow is disabled
  while one is out, including after a Stop that is waiting for the answer,
  and `follow()` refuses rather than cancels. And `follow()` **stops the
  previous job by name** before it asks for the next one. By name is what
  makes that stop race-free: arriving after the next Follow has started, it
  names a job that Follow already stopped, and ends nothing. The first two
  orderings are closed by those two rules. The third is not: a job whose id
  never reached the page is ended by the next Follow, from any tab, and
  README says so.
- **Stop while the follow request is still out lets it answer, then stops
  the job it names.**
  - Today such a Stop cancels the request (`logs-page.spec.ts`, "clicking it
    cancels the request").
  - A cancelled request cannot stop a job the host has already started.
    Once `LogFollower.StartAsync` has run `FollowLogs`, the aborted
    connection changes nothing, and the job runs until the next Follow.
  - The two tests that pinned the cancellation are rewritten, not deleted:
    what they guarded is that no stream opens, and that still holds.
  - The wait outlives a second `stop()`. Leaving the screen after such a
    Stop calls `stop()` again, and a second call that detached the request
    would drop the only id that can end the host's job. The request stays
    subscribed until it answers, whatever calls `stop()` meanwhile.
- **Leaving the Logs screen stops the host job too.**
  - Nothing re-attaches to it. The screen's lines and its job id are gone
    with the component, so returning shows an empty screen, and a
    `logs -f` nobody reads is the leak F1 describes.
  - The stop request is not tied to the component's lifetime. It is fired
    from `DestroyRef.onDestroy`, and `HttpClient` completes it after the
    component is gone.
- **`ps` and `exec` are unlisted jobs, not listed-but-filtered ones.** Spec
  §5.2 says one-shot commands are jobs "so that their output is visible
  the same way". For `up` and `down` that is true, and they stay listed.
  For `ps` and `exec` it has never been true: their output reaches a
  screen only as the view the caller parses (`ComposeStatus`,
  `QueuesView`, …), whose `error` carries the stderr line (F3).
  - They stay `Job`s, so the 30-second bound, `StopAsync` and host-shutdown
    kill (`ProcessRunner`'s own `processes` map, not the registry) are
    unchanged.
  - The registry simply does not keep them.
  - Filtering them out of `GET /api/jobs` instead would still let them
    count against `KeepExited` and evict the jobs a person reopens (F2).
    That eviction is the harm; the crowded list is only its symptom.
  - The flag lives on `ProcessSpec` because `IProcessRunner.Start(spec)` is
    the one door both runners share. Only `ComposeService` decides what is
    a read.
- **Broker auto-refresh re-reads all three, as one `refresh()` per tick.**
  - With the reads unlisted, three `exec`s per tick cost three short
    processes and nothing that lasts. The reason `AUTO_REFRESH_MS` gave for
    queues-only is gone.
  - `refresh()` is already non-re-entrant, so a tick while any read is out
    does nothing. That makes the separate `queuesInFlight` guard redundant,
    and it is removed.
  - The cost: the Refresh button is disabled for the length of each
    automatic read. That is accurate, since a read is out.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`.
  `TreatWarningsAsErrors`; IDE0055, IDE0065 and IDE0161 fail the build.
  **No column alignment** of `=` or `=>`. No `#pragma`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or
  editing `.cs` files and before every build or test, run
  `dotnet format whitespace BlueprintAdmin.slnx` from the repository root.
- C# wraps at 120 columns, prose at 80. British spelling. A comment says why
  and cites the owner, with no history and no PR named.
- Test names are sentences with underscores.
- Angular: standalone components, `inject()` at field level, signals for
  state. `npm ci`, never `npm install`.
- The host listens on loopback only. Never add a listener, a CORS header or a
  credential store (spec §8).
- FakePlatform is first-class. The stop endpoint works against
  `FakeProcessRunner` (whose `StopAsync` marks the job exited -1), and the
  Playwright smoke covers the Logs and Broker screens' new behaviour.
- Checks before claiming a task done, from the repository root:
  - `bash .claude/scripts/host-checks.sh all` for host tasks.
  - `bash .claude/scripts/npm-checks.sh all` for SPA tasks: lint, test and
    build.
  - `npm run e2e` in `src/Admin.Web` for Task 5. It needs the host under
    FakePlatform; CI's `smoke` job runs it too.
- Commits are semantic and present-tense, and end with the session's
  attribution trailer.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Jobs/ProcessSpec.cs                         Task 1  Listed
src/Admin.Host/Jobs/JobRegistry.cs                         Task 1  unlisted not kept
src/Admin.Host/Compose/ComposeService.cs                   Task 1  ps/exec unlisted
tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs            Task 1
tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs      Task 1
tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs          Task 1
src/Admin.Host/Stack/LogFollower.cs                        Task 2  StopAsync
src/Admin.Host/Stack/StackEndpoints.cs                     Task 2  the endpoint
tests/Admin.Host.Tests/Stack/LogFollowerTests.cs           Task 2
tests/Admin.Host.Tests/Stack/StackEndpointTests.cs         Task 2
src/Admin.Web/src/app/core/host/host-client.ts(+spec)      Task 3  stopFollow
src/Admin.Web/src/app/features/logs/logs-page.ts(+spec)    Task 3
src/Admin.Web/src/app/features/broker/broker-page.{ts,html,spec.ts}  Task 4
src/Admin.Web/e2e/logs.spec.ts, e2e/broker.spec.ts         Task 5
spec §5.2/§5.3/§5.10/§6, README.md                         Task 5
```

## Task 0 (controller, not a subagent): branch

- [ ] From a clean, current `main`, run `/branch` for
  `feat(logs)/stoppable-follow`. It cuts a sibling worktree
  `../blueprint-admin-stoppable-follow` and moves the session into it. Then
  run `npm ci` in `src/Admin.Web` there.

---

### Task 1: `ps` and `exec` are jobs the registry does not keep

**Files:**
- Modify: `src/Admin.Host/Jobs/ProcessSpec.cs`
- Modify: `src/Admin.Host/Jobs/JobRegistry.cs:5-26`
- Modify: `src/Admin.Host/Compose/ComposeService.cs:19-26,103`
- Modify: `src/Admin.Host/Fakes/FakeProcessRunner.cs` (a `LastStarted` seam)
- Test: `tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs`
- Test: `tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs`
- Test: `tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs`

**Interfaces:**
- Produces: `ProcessSpec.Listed` (`bool`, init-only, default `true`).
  `JobRegistry.Create(spec)` returns an unlisted job without keeping it:
  `Find(id)` is `null` for it and `All()` omits it. `ComposeService.Exec`
  and `PsAsync` start unlisted jobs. `Up`, `Down` and `FollowLogs` are
  unchanged.

- [ ] **Step 1: Write the failing registry test**

Append to `JobRegistryTests`:

```csharp
    [Fact]
    public void An_unlisted_job_is_not_kept_and_does_not_count_against_the_keep_limit()
    {
        FakeTimeProvider time = new();
        JobRegistry registry = new(time);
        Job kept = registry.Create(Spec);
        kept.MarkExited(1);

        for (int i = 0; i < JobRegistry.KeepExited + 1; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Job read = registry.Create(Spec with { Listed = false });
            read.MarkExited(0);

            registry.Find(read.Id).ShouldBeNull();
        }

        registry.Find(kept.Id).ShouldBeSameAs(kept);
        registry.All().ShouldHaveSingleItem().ShouldBeSameAs(kept);
    }
```

- [ ] **Step 2: Write the failing Compose and FakePlatform tests**

Append to `ComposeServiceTests`:

```csharp
    [Fact]
    public async Task However_many_ps_and_exec_reads_run_an_exited_job_stays_findable()
    {
        runner.On("docker", $"compose -f {Paths.ComposeFile} ps -a --format json", 0)
            .On("docker", $"compose -f {Paths.ComposeFile} exec -T rabbitmq", 0, "[", "]");
        Job down = Service.Down(wipeVolumes: false);

        for (int i = 0; i < JobRegistry.KeepExited + 1; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Service.PsAsync(TestContext.Current.CancellationToken);
            await Service.ExecAsync("rabbitmq", ["rabbitmqctl", "list_queues"], TestContext.Current.CancellationToken);
        }

        registry.All().ShouldHaveSingleItem().ShouldBeSameAs(down);
    }
```

`Down` has no script here, so the fake exits it 127 at once. It is an exited,
listed job: exactly what an `npm start` that died looks like to the registry.

Append to `FakePlatformTests`:

```csharp
    [Fact]
    public async Task Stack_and_broker_reads_are_not_listed_among_the_jobs()
    {
        await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);
        await client.GetFromJsonAsync<JsonElement>("/api/broker/queues", TestContext.Current.CancellationToken);

        JsonElement jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs", TestContext.Current.CancellationToken);

        jobs.EnumerateArray().Select(j => j.GetProperty("commandLine").GetString()!)
            .ShouldAllBe(c => !c.Contains(" ps ") && !c.Contains(" exec "));
    }
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test BlueprintAdmin.slnx`
Expected: the build fails with CS0117, `'ProcessSpec' does not contain a
definition for 'Listed'`. The tests cannot run until Step 4.

- [ ] **Step 4: Implement**

Replace the body of `src/Admin.Host/Jobs/ProcessSpec.cs`:

```csharp
namespace Admin.Host.Jobs;

/// <summary>What to run and where. Arguments are passed as a list, never joined into a shell string.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public string CommandLine => Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";

    /// <summary>
    /// Whether <see cref="JobRegistry"/> keeps the job, so that <c>/api/jobs</c> lists it and a screen can reopen
    /// its output by id. False for a read whose output reaches a screen only as the view its caller parses: the
    /// <c>ps</c> and <c>exec</c> that <c>ComposeService</c> runs (spec §5.2).
    /// </summary>
    public bool Listed { get; init; } = true;
}
```

In `src/Admin.Host/Jobs/JobRegistry.cs`, change the summary's first sentence
and `Create`:

```csharp
/// <summary>
/// Every listed job the host has started (<see cref="ProcessSpec.Listed"/>), in memory for the host's lifetime,
/// exited ones trimmed past the last fifty. The trim runs whenever a job is created or exits, so a host that stops
/// creating jobs still lets go of old ones.
/// </summary>
public sealed class JobRegistry(TimeProvider time)
{
    public const int KeepExited = 50;

    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly Lock trimGate = new();

    public Job Create(ProcessSpec spec)
    {
        Job job = new(Guid.CreateVersion7().ToString("N"), spec, time);

        // Kept, a read would count against KeepExited: the Stack screen's ps poll alone would push an exited
        // npm start, and the output that says why it died, out of reach within three minutes.
        if (!spec.Listed)
        {
            return job;
        }

        jobs[job.Id] = job;
        _ = job.Completion.ContinueWith(_ => Trim(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Trim();

        return job;
    }
```

In `src/Admin.Host/Compose/ComposeService.cs`, `Exec` and the `ps` in
`PsAsync` become reads, and `Run` gains two siblings:

```csharp
    public Job Exec(string service, params string[] args) => Read(["exec", "-T", service, .. args]);
```

```csharp
        CommandOutput output = await CompleteAsync(Read("ps", "-a", "--format", "json"), "docker compose ps", cancellationToken);
```

```csharp
    private Job Run(params string[] args) => runner.Start(Spec(args));

    /// <summary>
    /// A read: its output reaches a screen only as the view the caller parses, never by job id, so the registry
    /// does not keep it (<see cref="ProcessSpec.Listed"/>).
    /// </summary>
    private Job Read(params string[] args) => runner.Start(Spec(args) with { Listed = false });

    private ProcessSpec Spec(string[] args) => new("docker", ["compose", "-f", paths.ComposeFile, .. args], paths.BackendDir);
```

- [ ] **Step 5: Give the tests the jobs the registry no longer keeps**

Four `ComposeServiceTests` cases prove a cancelled or timed-out read was
stopped by reading its state back through `registry.All().Single()`: the
two `Ps_stops_the_job_…` cases and the two `ExecAsync_stops_the_job_…`
cases, the only tests that read a `ps` or `exec` job back that way. Once
`ps` and `exec` are unlisted the registry holds nothing, `Single()` throws,
and the cases fail for the wrong reason. `runner.Started` is no substitute:
it records the spec, which says what was asked, not whether the job was
stopped.

In `FakeProcessRunner`, beside `started`, keep the last job it started, and
only that one:

```csharp
    private Job? lastStarted;

    /// <summary>
    /// The most recent job started, listed or not: an unlisted job is out of the registry by design, and a
    /// test still needs its final state. One job, not a list: FakePlatform's runner is a singleton that
    /// starts a ps every Stack poll, and a list would keep each one, output ring and all, for the host's
    /// life — the retention the registry change removes.
    /// </summary>
    public Job? LastStarted
    {
        get
        {
            lock (gate)
            {
                return lastStarted;
            }
        }
    }
```

and in `Start`, inside the existing `lock (gate)`, after `started.Add(spec);`:

```csharp
            lastStarted = job;
```

In `ComposeServiceTests`, in each of the four cases, replace
`registry.All().Single().State.ShouldBe(JobState.Exited);` with:

```csharp
        runner.Started.Count.ShouldBe(1);
        runner.LastStarted.ShouldNotBeNull().State.ShouldBe(JobState.Exited);
        registry.All().ShouldBeEmpty();
```

The first line keeps what `Single()` asserted, that one job was started;
the last is the half the old assertion could not state: the job was
stopped, and the registry never kept it. `started` itself still grows by
one spec per start, as it does today; a spec is a few strings, not a job
with a 2,000-line ring.

- [ ] **Step 6: Run the host checks**

Run: `dotnet format whitespace BlueprintAdmin.slnx && bash .claude/scripts/host-checks.sh all`
Expected: all green, including the three new tests and the four rewritten
ones. `Exec_runs_inside_the_service_without_a_tty` and the other
`runner.Started` assertions pass unchanged: the fake records every spec it
starts, listed or not.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Host/Jobs/ProcessSpec.cs src/Admin.Host/Jobs/JobRegistry.cs src/Admin.Host/Compose/ComposeService.cs src/Admin.Host/Fakes/FakeProcessRunner.cs tests/Admin.Host.Tests/Jobs/JobRegistryTests.cs tests/Admin.Host.Tests/Compose/ComposeServiceTests.cs tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs
git commit -m "fix(jobs): keep the ps and exec reads out of the job registry" -m "<body: F2's arithmetic, and why unlisted rather than filtered>"
```

---

### Task 2: `POST /api/logs/follow/{id}/stop`

**Files:**
- Modify: `src/Admin.Host/Stack/LogFollower.cs`
- Modify: `src/Admin.Host/Stack/StackEndpoints.cs:35-36`
- Test: `tests/Admin.Host.Tests/Stack/LogFollowerTests.cs`
- Test: `tests/Admin.Host.Tests/Stack/StackEndpointTests.cs`

**Interfaces:**
- Consumes: `LogFollower.StartAsync(IReadOnlyList<string>, CancellationToken)`,
  `IProcessRunner.StopAsync(Job, CancellationToken)`.
- Produces: `LogFollower.StopAsync(string jobId, CancellationToken)`, which
  returns `Task`. `POST /api/logs/follow/{id}/stop` answers `204 No Content`
  always.

- [ ] **Step 1: Write the failing `LogFollower` tests**

Add to `LogFollowerTests`, above the `CancellingRunner` class:

```csharp
    [Fact]
    public async Task Stop_ends_the_follow_job_it_names()
    {
        using LogFollower follower = Follower();
        Job job = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync(job.Id, TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Exited);
    }

    [Fact]
    public async Task A_stop_naming_a_superseded_follow_leaves_the_current_one_running()
    {
        using LogFollower follower = Follower();
        Job first = await follower.StartAsync([], TestContext.Current.CancellationToken);
        Job second = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync(first.Id, TestContext.Current.CancellationToken);

        second.State.ShouldBe(JobState.Running);
    }

    [Fact]
    public async Task A_stop_naming_no_follow_job_ends_nothing()
    {
        using LogFollower follower = Follower();
        Job job = await follower.StartAsync([], TestContext.Current.CancellationToken);

        await follower.StopAsync("not-a-follow-job", TestContext.Current.CancellationToken);

        job.State.ShouldBe(JobState.Running);
    }

    private static LogFollower Follower()
    {
        FakeProcessRunner runner = new FakeProcessRunner(new JobRegistry(new FakeTimeProvider()))
            .OnLongRunning("docker", "compose", "gateway | started");

        return new LogFollower(new ComposeService(runner, Paths, TimeProvider.System), runner);
    }
```

- [ ] **Step 2: Write the failing endpoint tests**

Append to `StackEndpointTests`:

```csharp
    [Fact]
    public async Task Stopping_the_follow_ends_its_job()
    {
        HttpResponseMessage started = await client.PostAsJsonAsync("/api/logs/follow", new { services = GatewayOnly }, TestContext.Current.CancellationToken);
        string id = (await started.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        HttpResponseMessage stopped = await client.PostAsync($"/api/logs/follow/{id}/stop", null, TestContext.Current.CancellationToken);

        stopped.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", TestContext.Current.CancellationToken);
        job.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Exited");
    }

    [Fact]
    public async Task A_stop_that_reaches_the_host_after_the_next_follow_ends_nothing()
    {
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/logs/follow", new { services = GatewayOnly }, TestContext.Current.CancellationToken);
        string firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/logs/follow", new { services = GatewayOnly }, TestContext.Current.CancellationToken);
        string secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;

        HttpResponseMessage stopped = await client.PostAsync($"/api/logs/follow/{firstId}/stop", null, TestContext.Current.CancellationToken);

        stopped.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement secondNow = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{secondId}", TestContext.Current.CancellationToken);
        secondNow.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Running");
    }
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test BlueprintAdmin.slnx`
Expected: the build fails with CS1061, `'LogFollower' does not contain a
definition for 'StopAsync'`.

- [ ] **Step 4: Implement**

In `src/Admin.Host/Stack/LogFollower.cs`, extend the summary and add
`StopAsync` after `StartAsync`:

```csharp
/// <summary>
/// The Logs screen's one <c>docker compose logs -f</c>. Each Follow starts a new
/// long-running job, so starting a follow stops the previous one if it is still
/// running; the host keeps at most one follow job. Stop, and leaving the Logs
/// screen, end it by name through <see cref="StopAsync"/>.
/// </summary>
```

```csharp
    /// <summary>
    /// Stops the follow job <paramref name="jobId"/> names, and only while it is still the current one. The name is
    /// what makes a late Stop harmless: one that reaches the host after the next Follow names the job that Follow
    /// already stopped. There is deliberately no stop for any other job (spec §5.10).
    /// </summary>
    public async Task StopAsync(string jobId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is { State: JobState.Running } running && running.Id == jobId)
            {
                // Not the request's token, as in StartAsync: an aborted request must not leave the process half-stopped.
                await runner.StopAsync(running, CancellationToken.None);
            }
        }
        finally
        {
            gate.Release();
        }
    }
```

In `src/Admin.Host/Stack/StackEndpoints.cs`, after the `/api/logs/follow`
mapping:

```csharp
        app.MapPost("/api/logs/follow/{id}/stop", async (string id, LogFollower follower, CancellationToken cancellationToken) =>
        {
            await follower.StopAsync(id, cancellationToken);

            return TypedResults.NoContent();
        });
```

- [ ] **Step 5: Run the host checks**

Run: `dotnet format whitespace BlueprintAdmin.slnx && bash .claude/scripts/host-checks.sh all`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Host/Stack/LogFollower.cs src/Admin.Host/Stack/StackEndpoints.cs tests/Admin.Host.Tests/Stack/LogFollowerTests.cs tests/Admin.Host.Tests/Stack/StackEndpointTests.cs
git commit -m "feat(logs): stop the follow job by name" -m "<body: why by name, and why not POST /api/jobs/{id}/stop>"
```

---

### Task 3: Stop, and leaving the screen, end the host's follow

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-client.ts`
- Test: `src/Admin.Web/src/app/core/host/host-client.spec.ts`
- Modify: `src/Admin.Web/src/app/features/logs/logs-page.ts`
- Modify: `src/Admin.Web/src/app/features/logs/logs-page.html:8`
- Test: `src/Admin.Web/src/app/features/logs/logs-page.spec.ts`

**Interfaces:**
- Consumes: `POST /api/logs/follow/{id}/stop` → 204 (Task 2).
- Produces: `HostClient.stopFollow(jobId: string): Observable<void>`.

- [ ] **Step 1: Write the failing client test**

In `host-client.spec.ts`, after "follows logs for the named services":

```ts
  it('stops the follow job it names', () => {
    client.stopFollow('j 2').subscribe();

    const req = http.expectOne('/api/logs/follow/j%202/stop');
    expect(req.request.method).toBe('POST');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
```

- [ ] **Step 2: Rewrite and add the page tests**

In `logs-page.spec.ts`, the host double gains `stopFollow`:

```ts
  let host: { followLogs: ReturnType<typeof vi.fn>; stopFollow: ReturnType<typeof vi.fn> };
```

```ts
    host = { followLogs: vi.fn(() => of(summary('logs-1'))), stopFollow: vi.fn(() => of(undefined)) };
```

Replace "ignores a follow response that arrives after Stop" with:

```ts
  it('a Stop before the follow answers opens no stream and stops the job the answer names', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    sseFollow.mockClear();

    post.next(summary('late-1'));
    fixture.detectChanges();

    expect(sseFollow).not.toHaveBeenCalled();
    expect(host.stopFollow).toHaveBeenCalledWith('late-1');
    expect(fixture.componentInstance.following()).toBe(false);
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
  });
```

Replace "enables Stop while the follow request is in flight, and clicking it
cancels the request" with:

```ts
  it('enables Stop while the follow request is in flight, and clicking it stops the job the request starts', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.detectChanges();
    const stopButton = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;
    expect(stopButton.disabled).toBe(true);

    (fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(post.observed).toBe(true);
    expect(stopButton.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();

    stopButton.click();
    fixture.detectChanges();

    // Left to answer: a cancelled request cannot stop a job the host has already started.
    expect(post.observed).toBe(true);
    expect(stopButton.disabled).toBe(true);
    post.next(summary('late-3'));
    fixture.detectChanges();
    expect(sseFollow).not.toHaveBeenCalled();
    expect(host.stopFollow).toHaveBeenCalledWith('late-3');
    expect(fixture.nativeElement.querySelector('.live')).toBeNull();
  });
```

In "destroying the page while a follow is in flight opens no stream", add a
last line:

```ts
    expect(host.stopFollow).toHaveBeenCalledWith('late-2');
```

Replace "a second Follow replaces the first even if the first answers late"
with the rule that replaces it — one request out per page — since a second
Follow can no longer cancel the first:

```ts
  it('a Follow while a request is out sends nothing, and the first answer is followed', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.detectChanges();
    const followButton = fixture.nativeElement.querySelector('button.follow') as HTMLButtonElement;
    followButton.click();
    fixture.detectChanges();

    expect(followButton.disabled).toBe(true);
    fixture.componentInstance.follow();
    expect(host.followLogs).toHaveBeenCalledTimes(1);

    post.next(summary('job-a'));
    fixture.detectChanges();

    expect(sseFollow.mock.calls.map((c) => c[0])).toEqual(['job-a']);
    expect(followButton.disabled).toBe(false);
  });
```

Add, before the closing `});`:

```ts
  it('Stop ends the host job it is following and closes the stream', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next(line(0, 'gateway | started'));

    fixture.componentInstance.stop();

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(events.observed).toBe(false);
  });

  it('leaving the screen ends the host job it is following', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();

    fixture.destroy();

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
  });

  it('a new Follow stops the previous job by name before asking for the next', () => {
    const postB = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(postB.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    // Before B answers, and whether or not it ever does: a B that fails leaves no A behind.
    expect(host.stopFollow).toHaveBeenCalledWith('job-a');
    postB.error(new Error('refused'));
    expect(host.stopFollow).toHaveBeenCalledTimes(1);
  });

  it('leaving the screen after a Stop in flight still stops the job the late answer names', () => {
    const post = new Subject<JobSummary>();
    host.followLogs.mockReturnValueOnce(post.asObservable());

    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    fixture.destroy();

    expect(post.observed).toBe(true);
    post.next(summary('late-4'));

    expect(host.stopFollow).toHaveBeenCalledWith('late-4');
    expect(sseFollow).not.toHaveBeenCalled();
  });

  it('a stream that fails ends the host job it was reading, and keeps its own error', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();

    events.error(new Error('stream dropped'));

    expect(host.stopFollow).toHaveBeenCalledWith('logs-1');
    expect(fixture.componentInstance.error()).toBe('stream dropped');
    expect(fixture.componentInstance.following()).toBe(false);
  });

  it('a stop that fails keeps the job, so Stop stays enabled and tries it again', () => {
    host.stopFollow.mockReturnValueOnce(throwError(() => new Error('host gone')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.stop();
    fixture.detectChanges();
    const stopButton = fixture.nativeElement.querySelector('button.stop') as HTMLButtonElement;

    expect(stopButton.disabled).toBe(false);
    expect(fixture.componentInstance.error()).toBe('host gone');

    stopButton.click();
    fixture.detectChanges();

    expect(host.stopFollow.mock.calls.map((c) => c[0])).toEqual(['logs-1', 'logs-1']);
    expect(stopButton.disabled).toBe(true);
  });

  it('a slow stop of the previous job does not forget the next one', () => {
    const stopA = new Subject<void>();
    host.stopFollow.mockReturnValueOnce(stopA.asObservable());
    host.followLogs.mockReturnValueOnce(of(summary('job-a'))).mockReturnValueOnce(of(summary('job-b')));
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    fixture.componentInstance.follow();

    stopA.complete();

    expect(fixture.componentInstance.hostJob()).toBe('job-b');
  });

  it('a job that has exited is not stopped again', () => {
    const fixture = TestBed.createComponent(LogsPage);
    fixture.componentInstance.follow();
    events.next({ kind: 'exited', exitCode: 0 });

    fixture.componentInstance.stop();

    expect(host.stopFollow).not.toHaveBeenCalled();
  });
```

- [ ] **Step 3: Run them and see them fail**

Run: `bash .claude/scripts/npm-checks.sh all`
Expected: lint and the build fail on `stopFollow`, which does not exist on
`HostClient`. Once the method exists, the eight new page tests fail on
`stopFollow` never being called or on the Follow button never disabling,
and so do the four rewritten ones.

- [ ] **Step 4: Implement the client call**

In `host-client.ts`, after `followLogs`:

```ts
  /** Ends the follow job `jobId`; the host leaves a job that is no longer its current follow alone (spec §5.10). */
  stopFollow(jobId: string): Observable<void> {
    return this.http.post<void>(`/api/logs/follow/${encodeURIComponent(jobId)}/stop`, null);
  }
```

- [ ] **Step 5: Implement the page**

In `logs-page.ts`, change the rxjs import:

```ts
import { EMPTY, Subscription, switchMap } from 'rxjs';
```

Add a field after `private subscription?: Subscription;`:

```ts
  /** Stop came while the follow request was out: end the job the moment the answer names it. */
  private stopOnArrival = false;
```

and two signals after `pending`:

```ts
  /**
   * The host's follow job this screen started and has not seen end: Stop, and leaving the screen, end
   * it there (spec §5.10). Held until the host confirms the stop, so a stop that fails leaves Stop
   * enabled with the id to try again.
   */
  readonly hostJob = signal<string | null>(null);
```

```ts
  /**
   * The follow request is out, whether or not Stop has been pressed since. Only one may be out: a
   * second would cancel the first, and a cancelled request can still reach the host and start a
   * job that nothing here will ever learn the id of.
   */
  readonly requestOut = signal(false);
```

Replace the constructor, `follow()` and `stop()`:

```ts
  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }
```

```ts
  /**
   * A single subscription covers both stages: the `POST /api/logs/follow` and the SSE stream it
   * hands off to. The previous job is stopped by name before the next is asked for, rather than
   * left to the host's one-follow rule, which acts only once the next request arrives: a request
   * that fails on the way would otherwise leave it running. By name, a stop that lands after the
   * next follow has started ends nothing, because that follow already ended it.
   */
  follow(): void {
    if (this.requestOut()) {
      return;
    }
    this.detach();
    this.stopOnHost();
    this.stopOnArrival = false;
    this.error.set(null);
    this.lines.set([]);
    this.pending.set(true);
    this.requestOut.set(true);
    this.subscription = this.host
      .followLogs(this.selected())
      .pipe(
        switchMap((job) => {
          this.hostJob.set(job.id);
          this.requestOut.set(false);
          this.pending.set(false);
          if (this.stopOnArrival) {
            this.stopOnHost();
            return EMPTY;
          }
          this.following.set(true);
          return this.sse.follow(job.id);
        }),
      )
      .subscribe({
        next: (event) => {
          if (event.kind === 'line') {
            this.lines.update((all) => (all.length >= MAX_LINES ? [...all.slice(1), event.line] : [...all, event.line]));
          } else {
            this.hostJob.set(null);
            this.pending.set(false);
            this.following.set(false);
          }
        },
        error: (e: unknown) => {
          // Not yet following means the POST itself failed; already following means the SSE stream did.
          const fallback = this.following() ? undefined : 'The host refused the request.';
          this.error.set(this.describeError(e, fallback));
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          // A stream that failed leaves a job this screen can neither show nor, with Stop disabled,
          // end: it is the unread logs -f of leaving the screen, and ends the same way.
          this.stopOnHost();
        },
        complete: () => {
          this.requestOut.set(false);
          this.pending.set(false);
          this.following.set(false);
          this.stopOnHost();
        },
      });
  }

  /**
   * Ends the follow here and on the host. A follow request still out is left to answer rather
   * than cancelled: cancelling it cannot stop a job the host may already have started, and the
   * answer names the job to stop. That holds for every call until it answers, so leaving the
   * screen after a Stop does not detach the request and lose the id.
   */
  stop(): void {
    if (this.requestOut()) {
      this.stopOnArrival = true;
      this.pending.set(false);
      return;
    }
    this.detach();
    this.stopOnHost();
  }
```

Add, before `describeError`:

```ts
  private detach(): void {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.pending.set(false);
    this.following.set(false);
  }

  /**
   * Not tied to this screen's lifetime: it must still reach the host while the screen is being left.
   * The id is let go only when the host confirms, and only if it still names this job: a slow stop of
   * the previous follow must not forget the next one. A stop that fails keeps it, so Stop stays enabled
   * to try again, and its failure does not replace an error already shown, which explains the state.
   */
  private stopOnHost(): void {
    const id = this.hostJob();
    if (!id) {
      return;
    }
    this.host.stopFollow(id).subscribe({
      complete: () => this.hostJob.update((held) => (held === id ? null : held)),
      error: (e: unknown) => this.error.set(this.error() ?? this.describeError(e)),
    });
  }
```

Leaving the screen after a stop that failed has no one left to press Stop
again, and no retry is attempted: a retry loop outliving its screen is a
timer running for nobody. That job is the README residual below, ended by
the next Follow.

In `logs-page.html`, Follow disables while a request is out:

```html
    <button class="follow" [disabled]="requestOut()" (click)="follow()">Follow</button>
```

and Stop stays enabled while a host job is held:

```html
    <button class="stop" [disabled]="!(pending() || following() || hostJob())" (click)="stop()">Stop</button>
```

A Stop pressed while the request is out clears `pending` and holds no job
yet, so Stop disables at once while Follow stays disabled until the answer
arrives and its job is stopped. After a stream that failed, or a stop that
failed, `hostJob` is still set and Stop stays pressable.

- [ ] **Step 6: Run the SPA checks**

Run: `bash .claude/scripts/npm-checks.sh all`
Expected: lint, test and build are green, the four rewritten tests included.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Web/src/app/core/host/host-client.ts src/Admin.Web/src/app/core/host/host-client.spec.ts src/Admin.Web/src/app/features/logs/logs-page.ts src/Admin.Web/src/app/features/logs/logs-page.html src/Admin.Web/src/app/features/logs/logs-page.spec.ts
git commit -m "feat(logs): end the host's follow on Stop and when the screen is left" -m "<body: F1; why a Stop in flight waits for the answer>"
```

---

### Task 4: Broker auto-refresh re-reads all three

**Files:**
- Modify: `src/Admin.Web/src/app/features/broker/broker-page.ts`
- Modify: `src/Admin.Web/src/app/features/broker/broker-page.html:3`
- Test: `src/Admin.Web/src/app/features/broker/broker-page.spec.ts`

**Interfaces:**
- Consumes: Task 1. With the reads unlisted, a tick costs nothing that
  lasts.
- Produces: no new names. `AUTO_REFRESH_MS` stays exported at `5000`.

- [ ] **Step 1: Rewrite the tests that pin queues-only**

Replace "auto-refresh re-reads queues every 5 seconds, only queues, until
turned off" with:

```ts
  it('auto-refresh re-reads queues, exchanges and permissions every 5 seconds until turned off', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();
    host.brokerPermissions.mockClear();

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    await vi.advanceTimersByTimeAsync(5000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
    expect(host.brokerPermissions).toHaveBeenCalledTimes(2);

    fixture.componentInstance.setAutoRefresh(false);
    await vi.advanceTimersByTimeAsync(15000);
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(2);
  });
```

Replace "a failed exchanges read is not cleared by a later queues
auto-refresh success" with:

```ts
  it('a failed exchanges read is not cleared by a queues read that succeeds after it', async () => {
    vi.useFakeTimers();
    const fixture = render();
    // The tick's two reads answer when told to, exchanges failing first: a synchronous mock would
    // run the queues success first, and a queues path that cleared exchangesError would still pass.
    const slowQueues = new Subject<QueuesView>();
    const slowExchanges = new Subject<ExchangesView>();
    host.brokerQueues.mockReturnValue(slowQueues.asObservable());
    host.brokerExchanges.mockReturnValue(slowExchanges.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    slowExchanges.error({ error: { title: 'Server error', detail: 'exchanges boom' } });
    slowQueues.next(queues);
    slowQueues.complete();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('exchanges boom');
  });
```

Replace "refresh does not start a second queues read while an automatic one
is out, but still reads exchanges and permissions" with:

```ts
  it('Refresh is disabled while an automatic refresh is out, and starts nothing', async () => {
    vi.useFakeTimers();
    const fixture = render();
    host.brokerQueues.mockClear();
    host.brokerExchanges.mockClear();
    const slowQueues = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValue(slowQueues.asObservable());

    fixture.componentInstance.setAutoRefresh(true);
    await vi.advanceTimersByTimeAsync(5000);
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button.refresh') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    button.click();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);
    expect(host.brokerExchanges).toHaveBeenCalledTimes(1);

    slowQueues.next(queues);
    slowQueues.complete();
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
  });
```

Keep "auto-refresh does not stack a read on one still in flight", "leaving
the screen stops auto-refresh", "auto-refresh does not start while the
initial queues read is still out" and "reads queues again on the next tick
after a held read completes" unchanged. They describe the same guarantee,
now given by `refresh()` not being re-entrant.

- [ ] **Step 2: Run them and see them fail**

Run: `bash .claude/scripts/npm-checks.sh test`
Expected: the first rewritten test fails with `brokerExchanges` called 0
times, not 2. The other two fail on their counts and the disabled state.

- [ ] **Step 3: Implement**

In `broker-page.ts`, replace the `AUTO_REFRESH_MS` comment:

```ts
/**
 * Every read, not queues only: each is a `docker compose exec` the host does not keep among its jobs
 * (spec §5.2), so a tick costs three short processes and nothing that lasts.
 */
export const AUTO_REFRESH_MS = 5000;
```

Change the rxjs import to drop `exhaustMap`:

```ts
import { EMPTY, Observable, Subscription, catchError, finalize, tap, timer } from 'rxjs';
```

Delete the `queuesInFlight` field and the `queuesRead()` method. Replace the
error-signal comment's last clause, "(only queues auto-refreshes)", with
nothing, so the sentence ends "…is not silently hidden by another read's
success." Replace `refresh()` and `setAutoRefresh()`:

```ts
  /** While a read from an earlier call is still out, does nothing: the Refresh button is disabled meanwhile. */
  refresh(): void {
    if (this.refreshing()) return;
    this.refreshing.set(true);
    this.pendingReads = 3;
    this.withPendingCountdown(this.read(this.host.brokerQueues(), this.queuesError)).subscribe((view) => this.queues.set(view));
    this.withPendingCountdown(this.read(this.host.brokerExchanges(), this.exchangesError)).subscribe((view) => this.exchanges.set(view));
    this.withPendingCountdown(this.read(this.host.brokerPermissions(), this.permissionsError)).subscribe((view) => this.permissions.set(view));
  }

  /** A tick is a refresh(), so it starts nothing while an earlier refresh's reads are still out. */
  setAutoRefresh(on: boolean): void {
    this.autoRefresh.set(on);
    this.auto?.unsubscribe();
    this.auto = on ? timer(AUTO_REFRESH_MS, AUTO_REFRESH_MS).subscribe(() => this.refresh()) : undefined;
  }
```

In `broker-page.html`, line 3, change the label text from "Refresh queues every
5 s" to "Refresh every 5 s".

- [ ] **Step 4: Run the SPA checks**

Run: `bash .claude/scripts/npm-checks.sh all`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Admin.Web/src/app/features/broker/broker-page.ts src/Admin.Web/src/app/features/broker/broker-page.html src/Admin.Web/src/app/features/broker/broker-page.spec.ts
git commit -m "feat(broker): auto-refresh re-reads exchanges and permissions too" -m "<body: the reason for queues-only was the registry, which Task 1 removed>"
```

---

### Task 5: Playwright smoke and documents

**Files:**
- Modify: `src/Admin.Web/e2e/logs.spec.ts`
- Modify: `src/Admin.Web/e2e/broker.spec.ts`
- Modify: `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` (§5.2, §5.3, §5.10, §6)
- Modify: `README.md` ("Known limits")

- [ ] **Step 1: Extend the Logs smoke**

In `e2e/logs.spec.ts`, capture the follow job's id when Follow is clicked:

```ts
  const followed = page.waitForResponse((r) => r.url().endsWith('/api/logs/follow') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Follow' }).click();
  const { id } = (await (await followed).json()) as { id: string };
```

These three lines replace the existing Follow click. Replace the last two
lines with:

```ts
  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.live')).toHaveCount(0);
  // Stop reaches the host: the fake follow is long-running, so only the stop endpoint can end it.
  await expect
    .poll(async () => ((await (await page.request.get(`/api/jobs/${id}`)).json()) as { summary: { state: string } }).summary.state)
    .toBe('Exited');
```

- [ ] **Step 2: Extend the Broker smoke**

Append to the test in `e2e/broker.spec.ts`:

```ts
  const reread = page.waitForRequest('**/api/broker/permissions');
  await page.getByLabel('Refresh every 5 s').check();
  await reread;
```

- [ ] **Step 3: Run the smoke**

Start the host under FakePlatform with
`dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true`, then run
`npm run e2e` in `src/Admin.Web`.
Expected: all specs pass. Stop the host afterwards. If the host cannot run
here, say so, and let CI's `smoke` job carry it.

- [ ] **Step 4: Amend the spec**

§5.2 replaces:

```
Jobs are kept in memory for the life of the host, `Exited` jobs are trimmed
past the last 50. One-shot commands (`up`, `down`, `ps`, `exec`) are jobs too,
so that their output is visible the same way.
```

with:

```
Jobs are kept in memory for the life of the host, `Exited` jobs are trimmed
past the last 50. `up` and `down` are one-shot jobs kept the same way, so that
their output is visible the same way. `ps` and `exec` are jobs too — bounded,
stoppable and killed at shutdown like any other — but the registry does not
keep them (`ProcessSpec.Listed`): their output reaches a screen only as the
view the caller parses, and kept, the Stack screen's `ps` poll alone would push
an exited `npm start`, and the output that says why it died, past the last 50
within minutes.
```

§5.3 replaces:

```
- `Exec(service, args)`: `exec -T service args`, a one-shot job whose stdout
  the caller parses.
```

with:

```
- `Exec(service, args)`: `exec -T service args`, a one-shot job whose stdout
  the caller parses; like `Ps()`, a job the registry does not keep (§5.2).
```

§5.10 replaces the `POST /logs/follow` row with two rows:

```
| `POST /logs/follow` | body `{ services }`; stops the previous follow job if it is still running, starts one and returns it — the host keeps one |
| `POST /logs/follow/{id}/stop` | stops that follow job if it is still the current one; 204 either way. Named, so a Stop that reaches the host after the next Follow ends nothing. There is no generic job stop: it would reach `up`, `down -v` and the supervisor's `npm start` |
```

In §6, the Logs row's "Does" cell becomes `start, stop (the host's follow
job too, as does leaving the screen), clear`. The Broker row's becomes
`refresh, auto-refresh of all three`.

- [ ] **Step 5: Amend README's known limits**

Replace:

```
- The host keeps at most one `logs -f` job: Follow stops the previous one
  before starting the next. Stop on the Logs screen only closes the browser's
  stream; the job keeps running until the next Follow or until the host exits.
```

with:

```
- The host keeps at most one `logs -f` job, so a Follow in a second browser
  tab ends the first tab's. A follow whose answer never reached the page,
  or whose stop failed as the Logs screen was left, runs until the next
  Follow.
```

Delete:

```
- Broker reads are `docker compose exec` jobs, so they appear in
  `GET /api/jobs`; auto-refresh re-reads queues only.
```

If another known-limits plan has landed first, rebase these two edits onto
its README; the code does not move.

- [ ] **Step 6: Run every check**

Run: `bash .claude/scripts/host-checks.sh all && bash .claude/scripts/npm-checks.sh all`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Web/e2e/logs.spec.ts src/Admin.Web/e2e/broker.spec.ts docs/superpowers/specs/2026-09-14-blueprint-admin-design.md README.md
git commit -m "docs: say Stop ends the host's follow and reads are not kept as jobs" -m "<body: which spec sentences moved and why; the README line that stays>"
```

---

## Self-review

- **Coverage:** Limit 1 (Stop is browser-only) is lifted by Tasks 2, 3 and 5.
  Limit 2 (reads in `GET /api/jobs`; auto-refresh re-reads queues only) is
  lifted by Tasks 1, 4 and 5. The spec sentences that moved (§5.2, §5.3,
  §5.10, §6) and both README lines are in Task 5.
- **Names used across tasks:**
  - `ProcessSpec.Listed`: Tasks 1 and 5.
  - `LogFollower.StopAsync(string, CancellationToken)`: Task 2.
  - `POST /api/logs/follow/{id}/stop`: Tasks 2, 3 and 5.
  - `HostClient.stopFollow(jobId)`: Task 3.
  - `AUTO_REFRESH_MS`: Task 4.
- **Left as it is:**
  - Stop's 10-second give-up (spec §5.2, and the index's "Kept" list).
  - A second browser tab's Follow ending the first tab's. The one-follow
    rule is what keeps the host from leaking `logs -f` processes, and
    README keeps the line.
  - A follow whose response was lost. The page never learns its id, so
    only the next Follow ends it; closing that needs a host-side reaper
    for a follow nobody streams, which is a larger change than this
    limit asks for.
