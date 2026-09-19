# Known limits, frontend: install, and a client started by hand — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Stack screen runs `npm ci` in the frontend clone as a job with
visible output (Install), and neither Install nor Start runs while something
the console did not start answers on the client's port — in the real mode and
in FakePlatform mode.

**Architecture:** `FrontendSupervisor` gains a second job slot (`npm ci`) and
a port test injected as a delegate, the way `installed` already is. The port
test is a new `PlatformProbe.ClientAnswersAsync`, which reuses the probe's own
single-target request and its 2-second bound, so the Stack screen's `client`
chip and the refusal read the same URL. `FrontendEndpoints` maps
`POST /api/stack/frontend/install` and two new 409s on Start. FakePlatform's
client URL stops answering 200 unconditionally and answers only while the
scripted `npm start` runs, which is what a real `ng serve` does with its port —
without that, every fake Start would now be refused. The SPA adds an Install
button, the install job's state, and a warning when an unowned client answers.

**Tech Stack:** as phase 5 — .NET SDK 10.0.302, C# 14, minimal APIs,
`System.Text.Json` (no new packages), xunit.v3, Shouldly,
`Microsoft.AspNetCore.Mvc.Testing`; Angular 22.1.x, Vitest via `ng test`,
Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` —
§1 (property 1: the console runs the platform's own commands), §2.1 (the
process table, and the paragraph this plan reverses), §5.2 (Stop's 10 s
bound), §5.4 FrontendSupervisor, §5.10 Endpoints, §6 (Stack screen), §10.
The workspace's `run-locally.md` (beside the three clones) runs `npm ci` then
`npm start` in the frontend clone, which is what makes Install a console
operation under §1 rather than an invention. Index of the known-limits
plans: `2026-09-19-known-limits-index.md`.

---

## What the code says today (read on 2026-09-19 at `e8e4f57`; cite, do not restate elsewhere)

**F1 — Start refuses without `node_modules` and says the console will not
fix it.** `Frontend/FrontendEndpoints.cs` answers `NotInstalled` with 409
"Frontend dependencies not installed", detail `"… has no node_modules. Run
npm ci there; the console does not."`; `stack-page.html` prints the same
sentence. Spec §2.1: "`npm ci` is not a console operation. It is a one-time
prerequisite and the console reports its absence". CLAUDE.md: "It does
**not** run `npm ci` in the frontend clone."

**F2 — "One-time" is not true of `npm ci`.** `run-locally.md` runs it before
every `npm start` in a fresh clone, and a pull that moves the frontend's
lockfile needs it again. The console's own rule, **`npm ci`, never `npm
install`** (CLAUDE.md), is exactly why the console should run the right one:
`install` may rewrite a lockfile the frontend owns.

**F3 — Start never looks at the port.** `FrontendSupervisor.StartAsync`
checks its own job and `installed`, then starts. A client started by hand
gets a second `npm start`, which fails to bind and exits (the recording in
`FrontendSupervisorTests.A_start_that_exited_on_its_own_can_be_started_again`:
`"Port 5173 is already in use."`).

**F4 — The port is already probed, for the screen.** `Stack/PlatformProbe.cs`
probes `Join(o.ClientUrl, "/")` as the `client` entry, 2 s per target; a
refused connection is `Up = false, Status = null`, any HTTP answer carries its
status. `FrontendStatus`'s summary says whether the port answers "is the
`client` entry of the stack's reachability, not repeated here", and
`stack-page.ts`'s `clientUp` reads it. **So after a Stop that gave up at its
10 s bound (spec §5.2), the Stack screen already says whether the client
still answers** — the kept limit in README stays, and this plan makes Start
refuse in exactly that state too.

**F5 — FakePlatform's client always answers.** `FakePlatformHandler` falls
through to `FakeHttp.Json(OK, {"fake":true})` for the client URL, and
`FakePlatformTests.Every_surface_is_reachable_in_fake_mode` and
`e2e/stack.spec.ts` (`.reachability span.up` count 7) assert it. With F3
fixed and F5 unchanged, every fake Start would be refused.

**F6 — `PlatformProbe` is a typed `HttpClient`** (`AddHttpClient<PlatformProbe>`
in `Program.cs`), so it is transient; `FrontendSupervisor` is a singleton.

---

## Decisions this plan makes

- **Install is a console operation, and §2.1's paragraph is reversed** (F1,
  F2). The spec is amended in Task 4, with the reason, in the same PR.
- **"Answers" means any HTTP status, not a success.** A port that answers 404
  is still taken, and a second `ng serve` would still fail to bind it. The
  screen's chip keeps meaning "answers with a success"; the warning and the
  refusal use `status !== null`.
- **The port test is the last check, inside the supervisor's gate.** It is
  the only check that costs a request, and inside the gate two console clicks
  cannot race each other. **The race it cannot close:** a client started by
  hand that has not finished its first build is not listening yet, so it is
  not seen; whichever `ng serve` loses the port exits and says so in its
  output (F3). README keeps that as a known limit.
- **Its own running job is never "unowned".** Start checks its own job first
  (`AlreadyRunning`), so its own `ng serve` answering is never probed.
- **Install is refused while `npm start` runs, while an install runs, and
  while an unowned client answers**, because `npm ci` deletes and rewrites
  `node_modules` under whatever server reads it. **Start is refused while
  `npm ci` runs** for the same reason, before the `installed` check, which
  flips false and back during an install.
- **The fake client follows the scripted `npm start`** (F5): 200 while it
  runs, a refused connection otherwise. This is the fake telling the truth
  about a port, not a test seam.
- **The unowned-client refusal is covered by host tests, not Playwright.**
  FakePlatform has no client it did not start, and adding a switch for one
  would be config surface for a test. The host test drives it through the
  host's own composition (`PlatformProbe` → `FrontendSupervisor` → endpoint)
  by swapping only the probe's primary handler; the SPA spec covers the
  warning. Playwright covers Install and the fake port following the job.
- **An `npm ci` cannot be stopped from the console.** It ends by itself or
  with the host. Start and Install wait for it; README says so.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`.
  `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No
  column alignment** of `=` or `=>`. No `#pragma`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or
  editing `.cs` files and before every `dotnet build`/`dotnet test`, run
  `dotnet format whitespace BlueprintAdmin.slnx` from the repository root.
- Test names are sentences with underscores. `inject()` at field level;
  standalone components with an explicit `imports` array.
- Every command the host runs is a line from `run-locally.md`: `npm ci` and
  `npm start` in `FrontendDir`, never `npm install`.
- The host listens on loopback only; no listener, CORS header or credential
  store is added (spec §8).
- FakePlatform is first-class: Install works against the fakes and the
  Playwright smoke covers it on the Stack screen.
- A port, timeout or URL is never written as a raw value in a second place:
  prose cites `Admin:ClientUrl` / `AdminOptions.ClientUrl`
  (`docs/change-locality.md` §2).
- Checks before claiming a task done: `bash .claude/scripts/host-checks.sh all`
  for host tasks, `bash .claude/scripts/npm-checks.sh all` for SPA tasks,
  and `npm run e2e` in `src/Admin.Web` (needs `npm run build` first) for
  Tasks 1 and 4.
- Commits are semantic and present-tense, the body argues the change, and it
  ends with the attribution trailer the session provides. Nothing in
  `../blueprint-backend` or `../blueprint-frontend` is edited.
- **Class `C+D`.** Touch set: `src/Admin.Host/Frontend/**`,
  `src/Admin.Host/Stack/PlatformProbe.cs`, `src/Admin.Host/Fakes/**`,
  `src/Admin.Host/Program.cs`, `tests/Admin.Host.Tests/Frontend/**`,
  `tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs`,
  `tests/Admin.Host.Tests/Fakes/**`, `src/Admin.Web/src/app/core/host/**`,
  `src/Admin.Web/src/app/features/stack/**`, `src/Admin.Web/e2e/stack.spec.ts`,
  `src/Admin.Web/e2e/frontend.spec.ts`,
  `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`, `README.md`,
  `CLAUDE.md`.
- **Neighbouring plans.** The golden-signals plan edits `stack-page.*` and
  README's "What it does today" sentence about the Stack screen; the jobs
  plan edits spec §5.10. Whichever lands second rebases those lines, not its
  code.

---

## File structure

```
src/Admin.Host/Stack/PlatformProbe.cs                         Task 1  ClientAnswersAsync
src/Admin.Host/Fakes/FakeProcessRunner.cs                     Task 1  IsRunning
src/Admin.Host/Fakes/FakePlatformHandler.cs                   Task 1  the client follows npm start
src/Admin.Host/Program.cs                                     Task 1, 2
tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs            Task 1
tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs        Task 1
tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs             Task 1
src/Admin.Web/e2e/stack.spec.ts                               Task 1
src/Admin.Host/Frontend/FrontendStatus.cs                     Task 2  install slot, outcomes
src/Admin.Host/Frontend/FrontendSupervisor.cs                 Task 2  InstallAsync, port test
src/Admin.Host/Frontend/FrontendEndpoints.cs                  Task 2  install + 409s
src/Admin.Host/Fakes/FakePlatformScripts.cs                   Task 2  npm ci recording
tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs    Task 2
tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs      Task 2
src/Admin.Web/src/app/core/host/host-types.ts                 Task 3
src/Admin.Web/src/app/core/host/host-client.ts(+spec)         Task 3
src/Admin.Web/src/app/features/stack/stack-page.{ts,html,spec.ts}  Task 3
src/Admin.Web/e2e/frontend.spec.ts                            Task 4
spec §2.1/§5.4/§5.10/§6/§10, README.md, CLAUDE.md             Task 4
```

## Task 0 (controller, not a subagent): branch

- [ ] From a clean `main`, `/branch` a `feat(frontend)/install-and-unowned-client`
  branch in a sibling worktree; `npm ci` in `src/Admin.Web`.

---

### Task 1: The client's port — a probe for it, and a fake that holds it only while `npm start` runs

**Files:**
- Modify: `src/Admin.Host/Stack/PlatformProbe.cs`
- Modify: `src/Admin.Host/Fakes/FakeProcessRunner.cs`
- Modify: `src/Admin.Host/Fakes/FakePlatformHandler.cs`
- Modify: `src/Admin.Host/Program.cs` (the two `FakePlatformHandler` constructions)
- Test: `tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs`,
  `tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs`,
  `tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs`,
  `src/Admin.Web/e2e/stack.spec.ts`

**Interfaces:**
- Produces: `Task<bool> PlatformProbe.ClientAnswersAsync(CancellationToken)`
  — true on any HTTP status from `Join(ClientUrl, "/")`, redirects included,
  false on a refused connection or the 2 s timeout.
  `static HttpMessageHandler PlatformProbe.PrimaryHandler()`, which follows
  no redirects. `bool FakeProcessRunner.IsRunning(string
  fileName, string argumentPrefix)`. `FakePlatformHandler(AdminOptions
  options, Func<bool> clientServing)`.

- [ ] **Step 1: Write the failing probe tests**

Append to `PlatformProbeTests` (it already has a private `ScriptedHandler`
and the `Probe(...)` helper):

```csharp
    [Fact]
    public async Task The_client_answers_whatever_status_its_url_returns()
    {
        // A port that answers 404 is still taken: a second ng serve would fail to bind it.
        PlatformProbe probe = Probe(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await probe.ClientAnswersAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_client_that_answers_with_a_redirect_is_answering()
    {
        // The redirect's target is not asked: the port that sent the 302 is the one taken.
        PlatformProbe probe = Probe(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://localhost:1/nowhere") },
        });

        (await probe.ClientAnswersAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public void The_real_probe_never_follows_a_redirect()
    {
        // The case above holds only if the handler hands the 302 back. A default HttpClientHandler
        // follows it, and a client answering 302 to a page that is down would then read as a free
        // port, so Start would launch a second ng serve against a taken one. The subject is the
        // handler Program.cs registers, not a scripted one.
        HttpClientHandler handler = PlatformProbe.PrimaryHandler().ShouldBeOfType<HttpClientHandler>();

        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_connection_on_the_client_url_is_not_answering_and_nothing_else_is_asked()
    {
        List<Uri> asked = [];
        PlatformProbe probe = Probe(request =>
        {
            asked.Add(request.RequestUri!);
            throw new HttpRequestException("refused");
        });

        (await probe.ClientAnswersAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        asked.ShouldBe([new Uri("http://localhost:5173/")]);
    }
```

- [ ] **Step 2: Write the failing fake-runner test**

Append to `FakeProcessRunnerTests`:

```csharp
    [Fact]
    public async Task IsRunning_follows_a_long_running_script_until_it_is_stopped()
    {
        FakeProcessRunner runner = new FakeProcessRunner(registry)
            .OnLongRunning("npm", "start", "> ng serve");
        runner.IsRunning("npm", "start").ShouldBeFalse();

        Job job = runner.Start(new ProcessSpec("npm", ["start"], "/f"));

        runner.IsRunning("npm", "start").ShouldBeTrue();
        runner.IsRunning("npm", "ci").ShouldBeFalse();

        await runner.StopAsync(job, TestContext.Current.CancellationToken);

        runner.IsRunning("npm", "start").ShouldBeFalse();
    }
```

- [ ] **Step 3: Write the failing fake-platform tests**

In `FakePlatformTests`, add `using System.Net;` and replace
`Every_surface_is_reachable_in_fake_mode` with the first test below; add the
second and the helper. The second makes its own host because the class
fixture's host is shared with the first.

```csharp
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_surface_but_the_client_is_reachable_in_fake_mode_before_npm_start()
    {
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);

        JsonElement[] reachability = [.. stack.GetProperty("reachability").EnumerateArray()];
        reachability.Where(r => r.GetProperty("name").GetString() != "client").ShouldAllBe(r => r.GetProperty("up").GetBoolean());
        (await ClientUp(client)).ShouldBeFalse();
        stack.GetProperty("backend").GetProperty("services").EnumerateArray().Count().ShouldBe(13);
    }

    [Fact]
    public async Task The_fake_client_answers_only_while_the_scripted_npm_start_runs()
    {
        await using AdminHostFactory factory = new();
        HttpClient own = factory.CreateClient();

        (await own.PostAsync("/api/stack/frontend/start", null, Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ClientUp(own)).ShouldBeTrue();

        (await own.PostAsync("/api/stack/frontend/stop", null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ClientUp(own)).ShouldBeFalse();
    }

    private static async Task<bool> ClientUp(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>("/api/stack", Token))
            .GetProperty("reachability").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "client")
            .GetProperty("up").GetBoolean();
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~PlatformProbeTests|FullyQualifiedName~FakeProcessRunnerTests|FullyQualifiedName~FakePlatformTests"`
Expected: build FAIL — `'PlatformProbe' does not contain a definition for
'ClientAnswersAsync'` and for `'PrimaryHandler'`, and `'FakeProcessRunner'
does not contain a definition for 'IsRunning'`.

- [ ] **Step 5: Add `ClientAnswersAsync` to `PlatformProbe`**

In `ProbeAsync`, replace `("client", Join(o.ClientUrl, "/")),` with
`("client", ClientTarget(o)),`, and add below `ProbeAsync`:

```csharp
    /// <summary>
    /// Whether anything answers on the client's URL, with any status: a port that answers 404 is
    /// still taken, and a second <c>ng serve</c> would fail to bind it. FrontendSupervisor refuses
    /// Start and Install on this (spec §5.4); the Stack screen's <c>client</c> chip is the same target.
    /// </summary>
    public async Task<bool> ClientAnswersAsync(CancellationToken cancellationToken) =>
        (await ProbeOneAsync("client", ClientTarget(options.Value), cancellationToken)).Status is not null;

    /// <summary>
    /// The real probe's handler, registered in Program.cs. It does not follow redirects: a 3xx is
    /// the target's own answer, and following it would report whatever the redirect names instead,
    /// so a client answering 302 to a page that is down would read as a free port.
    /// </summary>
    public static HttpMessageHandler PrimaryHandler() => new HttpClientHandler { AllowAutoRedirect = false };

    private static string ClientTarget(AdminOptions o) => Join(o.ClientUrl, "/");
```

Not following redirects applies to every chip, not only the client's: a
target answering 3xx now reads as down with its status, where it used to
read as whatever the redirect led to. None of the seven does today. The
readiness checks, the realm document, Grafana's health and `ng serve`'s `/`
all answer 200, and a readiness check that redirected would be one worth
seeing. The `"platform"` client already refuses redirects for the same
reason (spec §5.7).

- [ ] **Step 6: Add `IsRunning` to `FakeProcessRunner`**

Below `Started`:

```csharp
    /// <summary>
    /// Whether a scripted long-running job for this command is still running. FakePlatform's client
    /// URL answers from this, as a real <c>ng serve</c> holds its port only while it runs.
    /// </summary>
    public bool IsRunning(string fileName, string argumentPrefix)
    {
        lock (gate)
        {
            return longRunning.Any(j => j.State == JobState.Running
                && j.Spec.FileName == fileName
                && string.Join(' ', j.Spec.Arguments).StartsWith(argumentPrefix, StringComparison.Ordinal));
        }
    }
```

- [ ] **Step 7: Make the fake client follow the job**

In `FakePlatformHandler.cs`, change the primary constructor to
`FakePlatformHandler(AdminOptions options, Func<bool> clientServing)`, add a
branch immediately before `: Is(uri, options.GatewayUrl) ? FakeGateway.Send(request)`:

```csharp
            : Is(uri, options.ClientUrl) ? Client(clientServing())
```

and add to the class:

```csharp
    // The reference client answers only while the scripted npm start runs, as ng serve holds the
    // port only while it runs. FrontendSupervisor refuses a Start while something it did not start
    // answers there (spec §5.4), so a client that always answered would refuse every fake Start.
    private static HttpResponseMessage Client(bool serving) =>
        serving
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!doctype html><app-root></app-root>", Encoding.UTF8, MediaTypeNames.Text.Html) }
            : throw new HttpRequestException("fake: nothing listens on the client URL until npm start runs");
```

In the class summary, replace "Readiness and Grafana's health answer 200 on
any host;" with "Readiness and Grafana's health answer 200 on any host; the
client URL answers only while the scripted npm start runs;".

In `Program.cs`, both `new FakePlatformHandler(...)` calls pass the second
argument. The probe's registration becomes:

```csharp
builder.Services.AddHttpClient<PlatformProbe>().ConfigurePrimaryHttpMessageHandler(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value is { FakePlatform: true } fake
        ? new FakePlatformHandler(fake, () => sp.GetRequiredService<FakeProcessRunner>().IsRunning("npm", "start"))
        : PlatformProbe.PrimaryHandler());
```

and the `"platform"` client's `new FakePlatformHandler(fake)` becomes
`new FakePlatformHandler(fake, () => sp.GetRequiredService<FakeProcessRunner>().IsRunning("npm", "start"))`.

- [ ] **Step 8: Run the host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx; bash .claude/scripts/host-checks.sh all`
Expected: PASS, including the new cases and the replaced one. Checked on
2026-09-19: no other host test asserts that the fake client is up;
`StackEndpointTests` counts 7 reachability entries, which is unchanged, and
the only Playwright assertion is `stack.spec.ts`'s count, fixed in Step 9.

- [ ] **Step 9: Update the Playwright stack smoke, then run it**

In `src/Admin.Web/e2e/stack.spec.ts`, add after the import:

```ts
// The fake host lives for the whole run, and its client answers only while a
// scripted npm start runs, so every test starts from a stopped frontend.
test.beforeEach(async ({ request }) => {
  const stopped = await request.post('/api/stack/frontend/stop');
  expect([200, 409]).toContain(stopped.status());
});
```

and in the first test replace
`await expect(page.locator('.reachability span.up')).toHaveCount(7);` with:

```ts
  await expect(page.locator('.reachability span.up')).toHaveCount(6);
  await expect(page.locator('.reachability span.down')).toHaveCount(1);
  await expect(page.locator('.reachability span.down')).toContainText('client down');
```

Run (in `src/Admin.Web`): `npm run build; npm run e2e`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Admin.Host/Stack/PlatformProbe.cs src/Admin.Host/Fakes/FakeProcessRunner.cs src/Admin.Host/Fakes/FakePlatformHandler.cs src/Admin.Host/Program.cs tests/Admin.Host.Tests/Stack/PlatformProbeTests.cs tests/Admin.Host.Tests/Fakes/FakeProcessRunnerTests.cs tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs src/Admin.Web/e2e/stack.spec.ts
git commit -m "feat(stack): probe whether anything answers on the client URL, and let the fake client hold its port only while npm start runs"
```

The body argues F4 and F5: the probe reuses the chip's target and bound, and
the fake's always-200 client was a fact about no real port.

---

### Task 2: Install runs `npm ci`; Start and Install refuse an unowned client

**Files:**
- Modify: `src/Admin.Host/Frontend/FrontendStatus.cs` (whole file below)
- Modify: `src/Admin.Host/Frontend/FrontendSupervisor.cs` (whole file below)
- Modify: `src/Admin.Host/Frontend/FrontendEndpoints.cs` (whole file below)
- Modify: `src/Admin.Host/Fakes/FakePlatformScripts.cs`
- Modify: `src/Admin.Host/Program.cs` (the `FrontendSupervisor` registration)
- Test: `tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs`,
  `tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs`

**Interfaces:**
- Consumes: `PlatformProbe.ClientAnswersAsync` (Task 1).
- Produces: `FrontendSupervisor(IProcessRunner runner, RepoPaths paths,
  Func<string, bool> installed, Func<CancellationToken, Task<bool>>
  clientAnswers)`; `Task<FrontendInstallResult> InstallAsync(CancellationToken)`;
  `FrontendStatus(JobSummary? Job, bool Installed, JobSummary? Install)` —
  on the wire `{ job, installed, install }`; `FrontendStartOutcome` gains
  `Installing` and `ClientAnswering`; `FrontendInstallOutcome { Started,
  ClientRunning, AlreadyInstalling, ClientAnswering }`;
  `POST /api/stack/frontend/install` → 202 `JobSummary` or 409 problem
  details titled "Frontend running", "Install already running" or "Client
  already answering"; Start's new 409 titles are "Frontend dependencies
  installing" and "Client already answering".

- [ ] **Step 1: Write the failing supervisor tests**

In `FrontendSupervisorTests`, replace the `Supervisor` helper with a
port-test double and add a token helper:

```csharp
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private bool clientAnswering;
    private int probes;

    private FrontendSupervisor Supervisor(bool installed = true) =>
        new(runner, Paths, _ => installed, _ =>
        {
            Interlocked.Increment(ref probes);

            return Task.FromResult(clientAnswering);
        });
```

In `Status_before_any_start_has_no_job_and_reports_installation`, the two
expectations become `new FrontendStatus(null, true, null)` and
`new FrontendStatus(null, false, null)`. Then append:

```csharp
    [Fact]
    public async Task Start_refuses_while_a_client_the_console_did_not_start_answers()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        clientAnswering = true;
        using FrontendSupervisor supervisor = Supervisor();

        FrontendStartResult result = await supervisor.StartAsync(Token);

        result.Outcome.ShouldBe(FrontendStartOutcome.ClientAnswering);
        result.Job.ShouldBeNull();
        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_does_not_probe_the_port_while_its_own_job_runs()
    {
        // Its own ng serve answers there; that is AlreadyRunning, never a client started by hand.
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        await supervisor.StartAsync(Token);
        clientAnswering = true;

        FrontendStartResult second = await supervisor.StartAsync(Token);

        second.Outcome.ShouldBe(FrontendStartOutcome.AlreadyRunning);
        probes.ShouldBe(1);
    }

    [Fact]
    public async Task Install_runs_npm_ci_in_the_frontend_clone_and_status_reports_it()
    {
        runner.On("npm", "ci", 0, "found 0 vulnerabilities");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendInstallResult result = await supervisor.InstallAsync(Token);

        result.Outcome.ShouldBe(FrontendInstallOutcome.Started);
        ProcessSpec spec = runner.Started.Single();
        spec.FileName.ShouldBe("npm");
        spec.Arguments.ShouldBe(["ci"]);
        spec.WorkingDirectory.ShouldBe("/repo/frontend");
        supervisor.Status().Install!.Id.ShouldBe(result.Job!.Id);
        supervisor.Status().Install!.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Install_refuses_while_npm_start_runs()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult started = await supervisor.StartAsync(Token);

        FrontendInstallResult result = await supervisor.InstallAsync(Token);

        result.Outcome.ShouldBe(FrontendInstallOutcome.ClientRunning);
        result.Job.ShouldBeSameAs(started.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Install_refuses_while_an_install_runs()
    {
        runner.OnLongRunning("npm", "ci", "npm warn deprecated inflight@1.0.6");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendInstallResult first = await supervisor.InstallAsync(Token);

        FrontendInstallResult second = await supervisor.InstallAsync(Token);

        second.Outcome.ShouldBe(FrontendInstallOutcome.AlreadyInstalling);
        second.Job.ShouldBeSameAs(first.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Install_refuses_while_a_client_the_console_did_not_start_answers()
    {
        runner.On("npm", "ci", 0, "found 0 vulnerabilities");
        clientAnswering = true;
        using FrontendSupervisor supervisor = Supervisor();

        FrontendInstallResult result = await supervisor.InstallAsync(Token);

        result.Outcome.ShouldBe(FrontendInstallOutcome.ClientAnswering);
        result.Job.ShouldBeNull();
        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_refuses_while_npm_ci_runs()
    {
        // npm ci deletes node_modules before it rewrites it; an ng serve started now reads half a tree.
        runner.OnLongRunning("npm", "ci", "npm warn deprecated inflight@1.0.6");
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendInstallResult installing = await supervisor.InstallAsync(Token);

        FrontendStartResult result = await supervisor.StartAsync(Token);

        result.Outcome.ShouldBe(FrontendStartOutcome.Installing);
        result.Job.ShouldBeSameAs(installing.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_installs_start_one_process()
    {
        runner.OnLongRunning("npm", "ci", "npm warn deprecated inflight@1.0.6");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendInstallResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => supervisor.InstallAsync(Token))));

        runner.Started.Count.ShouldBe(1);
        results.Count(r => r.Outcome == FrontendInstallOutcome.Started).ShouldBe(1);
    }
```

- [ ] **Step 2: Write the failing endpoint tests**

In `FrontendEndpointTests`, add `using Admin.Host.Stack;`. In
`Stack_reports_an_installed_frontend_with_no_job_before_any_start` add
`frontend.GetProperty("install").ValueKind.ShouldBe(JsonValueKind.Null);`.
In `Start_without_node_modules_is_refused_and_says_to_run_npm_ci`, the
override becomes
`new FrontendSupervisor(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(), _ => false, _ => Task.FromResult(false))`
(its `ShouldContain("npm ci")` still holds for the new detail). Append:

```csharp
    [Fact]
    public async Task Install_runs_npm_ci_and_replays_its_output()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/install", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("commandLine").GetString().ShouldBe("npm ci");

        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.GetProperty("id").GetString()}", Token);
        view.GetProperty("summary").GetProperty("exitCode").GetInt32().ShouldBe(0);
        view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).ShouldContain("found 0 vulnerabilities");

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);
        stack.GetProperty("frontend").GetProperty("install").GetProperty("commandLine").GetString().ShouldBe("npm ci");
    }

    [Fact]
    public async Task Install_while_npm_start_runs_is_a_conflict_and_runs_nothing()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        await client.PostAsync("/api/stack/frontend/start", null, Token);

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/install", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Frontend running");
        factory.Services.GetRequiredService<FakeProcessRunner>().Started.ShouldNotContain(s => s.CommandLine == "npm ci");
    }

    [Fact]
    public async Task Start_and_install_are_refused_while_a_client_the_console_did_not_start_answers()
    {
        await using AdminHostFactory factory = new();
        // Only the probe's primary handler is replaced, so this runs through the host's own wiring
        // from PlatformProbe to FrontendSupervisor. Every target answers 404: any status counts.
        using WebApplicationFactory<Program> answering = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<PlatformProbe>().ConfigurePrimaryHttpMessageHandler(() => new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))));
        HttpClient client = answering.CreateClient();

        HttpResponseMessage[] responses =
        [
            await client.PostAsync("/api/stack/frontend/start", null, Token),
            await client.PostAsync("/api/stack/frontend/install", null, Token),
        ];

        foreach (HttpResponseMessage response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            problem.GetProperty("title").GetString().ShouldBe("Client already answering");
            problem.GetProperty("detail").GetString()!.ShouldStartWith("http://localhost:5173 answers");
        }

        answering.Services.GetRequiredService<FakeProcessRunner>().Started.ShouldNotContain(s => s.FileName == "npm");
    }
```

`ScriptedHandler` is `Admin.Host.Tests.TestSupport.ScriptedHandler`, already
imported. A second `ConfigurePrimaryHttpMessageHandler` on the same typed
client is applied after `Program.cs`'s, so it wins.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter "FullyQualifiedName~Frontend"`
Expected: build FAIL — no `InstallAsync`, no `FrontendInstallResult`, and
`FrontendSupervisor` has no four-argument constructor.

- [ ] **Step 4: Replace `FrontendStatus.cs`**

```csharp
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client as the Stack screen sees it: the last <c>npm start</c> job and the last
/// <c>npm ci</c> job, each running or exited, and whether the clone has <c>node_modules</c>.
/// Whether the client's port answers is the <c>client</c> entry of the stack's reachability, not
/// repeated here.
/// </summary>
public sealed record FrontendStatus(JobSummary? Job, bool Installed, JobSummary? Install);

public enum FrontendStartOutcome
{
    Started,
    AlreadyRunning,
    NotInstalled,
    Installing,
    ClientAnswering,
}

/// <summary>
/// <see cref="Job"/> is the new job, the <c>npm start</c> already running, the <c>npm ci</c> still
/// running, or null when dependencies are missing or something else answers on the client's port.
/// </summary>
public sealed record FrontendStartResult(FrontendStartOutcome Outcome, Job? Job);

public enum FrontendInstallOutcome
{
    Started,
    ClientRunning,
    AlreadyInstalling,
    ClientAnswering,
}

/// <summary><see cref="Job"/> is the new <c>npm ci</c>, the running job that blocks it, or null when something else answers on the client's port.</summary>
public sealed record FrontendInstallResult(FrontendInstallOutcome Outcome, Job? Job);
```

- [ ] **Step 5: Replace `FrontendSupervisor.cs`**

```csharp
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client's one <c>npm start</c> and one <c>npm ci</c>, in the frontend clone
/// (spec §5.4); both are lines <c>run-locally.md</c> runs there, and <c>npm ci</c> never
/// <c>npm install</c>, which may rewrite the lockfile the frontend owns. Start refuses while its
/// job runs, while an install runs, without <c>node_modules</c>, and while something the console
/// did not start answers on the client's port, because a second <c>ng serve</c> would compete for
/// it. Install refuses while either job runs and on the same port test, because <c>npm ci</c>
/// replaces the <c>node_modules</c> a running server reads. Stop kills the tree through the runner.
/// Everything is serialised under one gate, so two clicks cannot leave two processes.
/// </summary>
public sealed class FrontendSupervisor(
    IProcessRunner runner,
    RepoPaths paths,
    Func<string, bool> installed,
    Func<CancellationToken, Task<bool>> clientAnswers) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Written under the gate; Status reads them without, and a reference read is atomic.
    private volatile Job? current;
    private volatile Job? install;

    public static bool HasNodeModules(string frontendDir) => Directory.Exists(Path.Combine(frontendDir, "node_modules"));

    public FrontendStatus Status()
    {
        Job? job = current;
        Job? installJob = install;

        return new FrontendStatus(
            job is null ? null : JobSummary.Of(job),
            installed(paths.FrontendDir),
            installJob is null ? null : JobSummary.Of(installJob));
    }

    public async Task<FrontendStartResult> StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is { State: JobState.Running } running)
            {
                return new FrontendStartResult(FrontendStartOutcome.AlreadyRunning, running);
            }

            // Before the node_modules check, which flips false and back while npm ci rewrites the tree.
            if (install is { State: JobState.Running } installing)
            {
                return new FrontendStartResult(FrontendStartOutcome.Installing, installing);
            }

            if (!installed(paths.FrontendDir))
            {
                return new FrontendStartResult(FrontendStartOutcome.NotInstalled, null);
            }

            // Last, because it is the one check that costs a request. No job of ours runs, so whatever
            // answers is a client started by hand or one a Stop that gave up left alive (spec §5.2).
            if (await clientAnswers(cancellationToken))
            {
                return new FrontendStartResult(FrontendStartOutcome.ClientAnswering, null);
            }

            Job job = runner.Start(new ProcessSpec("npm", ["start"], paths.FrontendDir));
            current = job;

            return new FrontendStartResult(FrontendStartOutcome.Started, job);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FrontendInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is { State: JobState.Running } running)
            {
                return new FrontendInstallResult(FrontendInstallOutcome.ClientRunning, running);
            }

            if (install is { State: JobState.Running } installing)
            {
                return new FrontendInstallResult(FrontendInstallOutcome.AlreadyInstalling, installing);
            }

            if (await clientAnswers(cancellationToken))
            {
                return new FrontendInstallResult(FrontendInstallOutcome.ClientAnswering, null);
            }

            Job job = runner.Start(new ProcessSpec("npm", ["ci"], paths.FrontendDir));
            install = job;

            return new FrontendInstallResult(FrontendInstallOutcome.Started, job);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Job?> StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (current is not { State: JobState.Running } running)
            {
                return null;
            }

            // Not the request's token: an aborted request must not leave ng serve half-killed.
            await runner.StopAsync(running, CancellationToken.None);

            return running;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
```

- [ ] **Step 6: Replace `FrontendEndpoints.cs`**

```csharp
using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Admin.Host.Frontend;

public static class FrontendEndpoints
{
    public static IEndpointRouteBuilder MapFrontend(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/stack/frontend/install", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, IOptions<AdminOptions> options, CancellationToken cancellationToken) =>
        {
            FrontendInstallResult result = await supervisor.InstallAsync(cancellationToken);

            return result.Outcome switch
            {
                FrontendInstallOutcome.Started => TypedResults.Accepted((string?)null, JobSummary.Of(result.Job!)),
                FrontendInstallOutcome.ClientRunning => Conflict("Frontend running", $"npm start is job {result.Job!.Id}. Stop it first: npm ci replaces the node_modules it serves from."),
                FrontendInstallOutcome.AlreadyInstalling => Conflict("Install already running", $"npm ci is job {result.Job!.Id}."),
                _ => ClientAnswering(options.Value.ClientUrl),
            };
        });

        app.MapPost("/api/stack/frontend/start", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, RepoPaths paths, IOptions<AdminOptions> options, CancellationToken cancellationToken) =>
        {
            FrontendStartResult result = await supervisor.StartAsync(cancellationToken);

            return result.Outcome switch
            {
                FrontendStartOutcome.Started => TypedResults.Accepted((string?)null, JobSummary.Of(result.Job!)),
                FrontendStartOutcome.AlreadyRunning => Conflict("Frontend already running", $"npm start is job {result.Job!.Id}. Stop it first."),
                FrontendStartOutcome.Installing => Conflict("Frontend dependencies installing", $"npm ci is job {result.Job!.Id}. Start once it exits."),
                FrontendStartOutcome.ClientAnswering => ClientAnswering(options.Value.ClientUrl),
                _ => Conflict("Frontend dependencies not installed", $"{paths.FrontendDir} has no node_modules. Install runs npm ci there."),
            };
        });

        app.MapPost("/api/stack/frontend/stop", async Task<Results<Ok<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, CancellationToken cancellationToken) =>
            await supervisor.StopAsync(cancellationToken) is Job stopped
                ? TypedResults.Ok(JobSummary.Of(stopped))
                : Conflict("Frontend not running", "There is no npm start job to stop."));

        return app;
    }

    // Spec §5.4: whatever answers there is no job of this console's, so the console can neither stop
    // it nor start a second server against it, and npm ci would pull node_modules out from under it.
    private static ProblemHttpResult ClientAnswering(string clientUrl) =>
        Conflict("Client already answering", $"{clientUrl} answers, and it is not an npm start this console is running. Stop that client first; a Stop that gave up may have left one.");

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
```

- [ ] **Step 7: Record `npm ci` for FakePlatform and wire the port test**

In `FakePlatformScripts.cs`, add below `NgServeLines`:

```csharp
    // npm ci in the frontend clone as npm prints a clean install. It exits 0.
    private static readonly string[] NpmCiLines =
    [
        "added 1043 packages, and audited 1044 packages in 38s",
        "180 packages are looking for funding",
        "  run `npm fund` for details",
        "found 0 vulnerabilities",
    ];
```

and in `Script`, before `.OnLongRunning("npm", "start", NgServeLines);`, add
`.On("npm", "ci", 0, NpmCiLines)`.

In `Program.cs`, replace the `FrontendSupervisor` registration and its
comment with:

```csharp
// In FakePlatform mode the frontend clone may not exist at all, and its npm
// start is a recording, so the node_modules check would refuse for nothing.
// The port test resolves the probe per call: PlatformProbe is a typed
// HttpClient, transient by registration, and a singleton that kept one would
// pin its handler for the life of the host.
builder.Services.AddSingleton(sp =>
{
    Func<string, bool> installed = sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? _ => true
        : FrontendSupervisor.HasNodeModules;
    Func<CancellationToken, Task<bool>> clientAnswers = cancellationToken =>
        sp.GetRequiredService<PlatformProbe>().ClientAnswersAsync(cancellationToken);

    return new FrontendSupervisor(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(), installed, clientAnswers);
});
```

- [ ] **Step 8: Run the host checks**

Run: `dotnet format whitespace BlueprintAdmin.slnx; bash .claude/scripts/host-checks.sh all`
Expected: PASS. The existing `Start_runs_npm_start_and_replays_the_ng_serve_banner`
still passes because the fake client does not answer before the fake start
(Task 1).

- [ ] **Step 9: Commit**

```bash
git add src/Admin.Host/Frontend src/Admin.Host/Fakes/FakePlatformScripts.cs src/Admin.Host/Program.cs tests/Admin.Host.Tests/Frontend
git commit -m "feat(stack): install the frontend with npm ci, and refuse Start and Install while a client the console did not start answers"
```

The body argues F1–F3 and the reversal of spec §2.1's paragraph (amended in
Task 4), names the race the probe cannot close, and says an `npm ci` cannot
be stopped from the console.

---

### Task 3: The Stack screen — Install, its job, and the unowned-client warning

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`
- Modify: `src/Admin.Web/src/app/core/host/host-client.ts`, `host-client.spec.ts`
- Modify: `src/Admin.Web/src/app/features/stack/stack-page.ts`, `stack-page.html`, `stack-page.spec.ts`

**Interfaces:**
- Consumes: `POST /api/stack/frontend/install`; `FrontendStatus.install` on
  `GET /api/stack` (Task 2).
- Produces: `HostClient.frontendInstall(): Observable<JobSummary>`;
  `StackPage.installing`, `StackPage.unownedClient` (computed signals),
  `StackPage.installFrontend()`; buttons `button.frontend-install` labelled
  "Install (npm ci)" and `p.install-status`.

- [ ] **Step 1: Write the failing client test**

In `host-client.spec.ts`, after the "stops the frontend" case:

```ts
  it('installs the frontend', () => {
    client.frontendInstall().subscribe();

    const req = http.expectOne('/api/stack/frontend/install');
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'ci-1', commandLine: 'npm ci', state: 'Running', exitCode: null, startedAt: '' });
  });
```

- [ ] **Step 2: Write the failing page tests**

In `stack-page.spec.ts`:

- the top-level `stack` fixture's `frontend` becomes
  `{ job: null, installed: true, install: null }`;
- below `runningJob` add
  `const installJob = { id: 'ci-1', commandLine: 'npm ci', state: 'Running', exitCode: null, startedAt: '' };`;
- the `host` type gains `frontendInstall: ReturnType<typeof vi.fn>;` and the
  object gains `frontendInstall: vi.fn(() => of(installJob)),`;
- in "shows a frontend that was never started…", replace
  `expect(fixture.nativeElement.textContent).not.toContain('npm ci');` with
  `expect(fixture.nativeElement.textContent).not.toContain('has no node_modules');`
  and add `expect(q('button.frontend-install')!.disabled).toBe(false);`;
- replace the test "says npm ci is needed and disables Start when
  node_modules is absent" with:

```ts
    it('says Install runs npm ci, offers it, and disables Start when node_modules is absent', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false, install: null } }));
      const { fixture, q } = await render();

      expect(fixture.nativeElement.textContent).toContain('/work/blueprint-frontend has no node_modules. Install runs npm ci there.');
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.frontend-install')!.disabled).toBe(false);
    });
```

and append inside `describe('reference client', …)`:

```ts
    it('installs with npm ci and hands its job to the output pane', async () => {
      const { fixture, q } = await render();

      q('button.frontend-install')!.click();
      fixture.detectChanges();

      expect(host.frontendInstall).toHaveBeenCalled();
      expect(fixture.componentInstance.jobId()).toBe('ci-1');
    });

    it('shows npm ci running and disables Start and Install while it runs', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: true, install: installJob } }));
      const { q } = await render();

      expect(text(q('.install-status'))).toBe('npm ci running');
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.frontend-install')!.disabled).toBe(true);
    });

    it('says a client the console did not start answers, and disables Start and Install', async () => {
      host.stack.mockReturnValue(
        of({ ...stack, reachability: [{ name: 'client', url: 'http://localhost:5173/', up: true, status: 200 }] }),
      );
      const { fixture, q } = await render();

      expect(fixture.nativeElement.textContent).toContain(
        'Something already answers on http://localhost:5173 that this console did not start. Start and Install are refused until it stops.',
      );
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.frontend-install')!.disabled).toBe(true);
    });

    it('never calls its own running client unowned', async () => {
      host.stack.mockReturnValue(
        of({
          ...stack,
          frontend: { job: runningJob, installed: true, install: null },
          reachability: [{ name: 'client', url: 'http://localhost:5173/', up: true, status: 200 }],
        }),
      );
      const { fixture } = await render();

      expect(fixture.nativeElement.textContent).not.toContain('Something already answers');
    });
```

- [ ] **Step 3: Run them to see them fail**

Run (in `src/Admin.Web`): `npm test`
Expected: FAIL — `client.frontendInstall is not a function`, and the page
tests find no `button.frontend-install`.

- [ ] **Step 4: Implement the types, the client call and the page**

`host-types.ts` — replace `FrontendStatus`:

```ts
/** The reference client's last `npm start`, its last `npm ci`, and whether its clone has node_modules (spec §5.4). */
export interface FrontendStatus {
  job: JobSummary | null;
  installed: boolean;
  install: JobSummary | null;
}
```

`host-client.ts` — after `frontendStop()`:

```ts
  frontendInstall(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/install', null);
  }
```

`stack-page.ts` — after `frontendRunning`:

```ts
  readonly installing = computed(() => this.stack()?.frontend.install?.state === 'Running');

  /**
   * Something answers on the client's URL, with any status, and it is not this console's running
   * npm start: a client started by hand, or one a Stop that gave up left alive. The host refuses
   * Start and Install on the same test (spec §5.4); the screen says so before the click.
   */
  readonly unownedClient = computed(() => {
    const client = this.stack()?.reachability.find((r) => r.name === 'client');
    return (client?.status ?? null) !== null && !this.frontendRunning();
  });
```

and after `stopFrontend()`:

```ts
  installFrontend(): void {
    this.host.frontendInstall().subscribe(this.started);
  }
```

`stack-page.html` — in `<section class="frontend">`, replace the
`@if (!s.frontend.installed) { … }` block and the Start button with:

```html
    @if (s.frontend.install; as install) {
      <p class="install-status">npm ci @if (install.state === 'Running') { running } @else { exited {{ install.exitCode }} }</p>
    }
    @if (!s.frontend.installed) {
      <p class="error">{{ config()?.frontendDir ?? 'The frontend clone' }} has no node_modules. Install runs npm ci there.</p>
    }
    @if (unownedClient()) {
      <p class="error">Something already answers on {{ config()?.urls.client ?? 'the client URL' }} that this console did not start. Start and Install are refused until it stops.</p>
    }
    <button class="frontend-install" [disabled]="frontendRunning() || installing() || unownedClient()" (click)="installFrontend()">Install (npm ci)</button>
    <button class="frontend-start" [disabled]="frontendRunning() || installing() || unownedClient() || !s.frontend.installed" (click)="startFrontend()">Start frontend</button>
```

- [ ] **Step 5: Run the SPA checks**

Run: `bash .claude/scripts/npm-checks.sh all`
Expected: lint, tests and build PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Web/src/app/core/host src/Admin.Web/src/app/features/stack
git commit -m "feat(stack): an Install button for npm ci, and a warning when a client the console did not start answers"
```

---

### Task 4: Playwright covers Install; the spec, README and CLAUDE.md say what the console now does

**Files:**
- Modify: `src/Admin.Web/e2e/frontend.spec.ts`
- Modify: `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` §2.1, §5.4, §5.10, §6, §10
- Modify: `README.md`, `CLAUDE.md`

- [ ] **Step 1: Extend the frontend smoke**

In `e2e/frontend.spec.ts`, inside the start-and-stop test, add
`await expect(status).toContainText('client does not answer');` before
`await start.click();`, add
`await expect(status).toContainText('client answers', { timeout: 10_000 });`
after `await expect(stop).toBeEnabled();`, and add
`await expect(status).toContainText('client does not answer', { timeout: 10_000 });`
after the final `await expect(start).toBeEnabled();`. Then append:

```ts
test('the stack screen installs the client dependencies with npm ci', async ({ page }) => {
  await page.goto('/stack');
  const install = page.getByRole('button', { name: 'Install (npm ci)' });
  const output = page.locator('app-output-pane pre');

  await expect(install).toBeEnabled();
  await install.click();

  await expect(output).toContainText('found 0 vulnerabilities');
  await expect(output).toContainText('exited 0');
  // The install line follows the 3-second stack poll.
  await expect(page.locator('.install-status')).toContainText('npm ci exited 0', { timeout: 10_000 });
});
```

- [ ] **Step 2: Run it**

Run (in `src/Admin.Web`): `npm run build; npm run e2e`
Expected: PASS.

- [ ] **Step 3: Amend the spec**

§2.1 — add a row after "Start frontend":
`| Install frontend | `npm ci`, frontend |`, and replace the paragraph
"`npm ci` is not a console operation. It is a one-time prerequisite and the
console reports its absence (no `node_modules`) rather than running it." with:

```markdown
`npm ci` is the console's Install: the line `run-locally.md` runs in the
frontend clone before `npm start`, and not only once, since a pull that moves
the frontend's lockfile needs it again. It is never `npm install`, which may
rewrite a lockfile the frontend owns.
```

§5.4 — replace the paragraph with:

```markdown
Holds at most one `npm start` job and one `npm ci` job in `FrontendDir`.
`Install()` runs `npm ci`; it refuses while either job runs, and while
something the console did not start answers on `ClientUrl`, because `npm ci`
replaces the `node_modules` a running server reads. `Start()` refuses while
its job runs, while an install runs, if `node_modules` is absent, and while
something the console did not start answers on `ClientUrl` — a client started
by hand, or one a Stop that gave up (§5.2) left alive — because a second
`ng serve` would compete for the port. "Answers" is any HTTP status. The port
is tested last and under the same lock as the start, so two clicks cannot
race; a client started by hand that has not finished its first build is not
listening yet and is not seen, and whichever `ng serve` loses the port exits
saying so in its output. `Stop()` kills the tree. `Status()` reports both jobs
and whether `node_modules` exists; whether the port answers is the `client`
entry of the stack's reachability.
```

§5.10 — the row
`| `POST /stack/frontend/start`, `POST /stack/frontend/stop` | supervisor |`
becomes
`| `POST /stack/frontend/install`, `POST /stack/frontend/start`, `POST /stack/frontend/stop` | supervisor: `npm ci`, `npm start`, kill the tree; a refusal is a 409 naming what blocks it |`.

§6 — the Stack row's "Shows" cell replaces "the frontend job;" with "the
frontend job and its last `npm ci`, and a warning when something the console
did not start answers on the client's port;", and its "Does" cell becomes
"Up, Down, Down and wipe (typed confirmation), Install (`npm ci`), Start and
Stop frontend; opens the job's output pane".

§10 — "and `npm start` output from an in-code recording" becomes "and
`npm start` and `npm ci` output from in-code recordings", and "`node_modules`
absent)." becomes "`node_modules` absent, a client the console did not
start)."

- [ ] **Step 4: Amend README and CLAUDE.md**

README "What it does today": "the reference client's `npm start` with start,
stop and its output;" becomes "the reference client's `npm ci` and `npm start`
with install, start, stop and their output, refused while a client the
console did not start answers;".

README "Known limits": delete the two items beginning "The console runs
`npm start` but never `npm ci`" and "The console does not track a client
started by hand", and add in their place:

```markdown
- A client started by hand is seen only once it answers on `Admin:ClientUrl`:
  during its first build the port is still free, so Start can race it, and
  the `ng serve` that loses exits saying the port is in use.
- An `npm ci` cannot be stopped from the console; Start and Install wait for
  it to exit.
```

CLAUDE.md — "It does **not** run `npm ci` in the frontend clone." becomes
"It runs `npm ci` in the frontend clone only when Install is pressed — never
on its own, and never `npm install`." (CLAUDE.md is not in
`.claude/settings.json`'s deny list; an `auto`-mode session has been seen
refusing edits to it anyway, and then the user applies this one line.)

- [ ] **Step 5: Run everything once more**

Run: `bash .claude/scripts/host-checks.sh all; bash .claude/scripts/npm-checks.sh all`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Admin.Web/e2e/frontend.spec.ts docs/superpowers/specs/2026-09-14-blueprint-admin-design.md README.md CLAUDE.md
git commit -m "docs: npm ci is the console's Install, and Start refuses a client it did not start"
```

The body says spec §2.1's "one-time prerequisite" was the rule that moved,
and why (F2); it is a rule change, not a restatement.

---

## Self-review

- **Limits covered.** "Never `npm ci`" — Tasks 2–4. "A client started by
  hand is not tracked" — Tasks 1–4. The kept "Stop gives up after 10 s" —
  F4 confirms the screen already shows whether the client answers; Start now
  refuses in that state, and §5.4's new text says so.
- **Names used across tasks.** `ClientAnswersAsync`, `IsRunning`,
  `FrontendInstallResult`/`FrontendInstallOutcome`, `InstallAsync`,
  `FrontendStatus.Install` ↔ `install` on the wire, `frontendInstall()`,
  `installing`, `unownedClient`, `installFrontend()`, `button.frontend-install`,
  `.install-status` — each defined once and used with the same spelling.
- **Problem titles** asserted in tests match the endpoint file exactly:
  "Frontend running", "Install already running", "Client already answering",
  "Frontend dependencies installing", "Frontend dependencies not installed".
