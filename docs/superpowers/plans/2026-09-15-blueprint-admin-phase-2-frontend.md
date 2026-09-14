# Blueprint admin, phase 2: Frontend — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Stack screen starts and stops the reference client's `npm start` in the frontend clone, shows its state and output, and refuses to start when `node_modules` is absent — in the real mode and in FakePlatform mode.

**Architecture:** A `FrontendSupervisor` singleton (host, namespace `Admin.Host.Frontend`) holds at most one `npm start` job, started through the existing `IProcessRunner`, so output, SSE streaming, tree-kill and host-shutdown cleanup come from phase 1 unchanged. `GET /api/stack` gains a `frontend` object; `POST /api/stack/frontend/start` and `/stop` drive the supervisor. Because Windows' `CreateProcess` only appends `.exe`, `npm` (which is `npm.cmd` there) cannot start today; an `ExecutableResolver` used by `ProcessRunner` searches `PATH` with `PATHEXT` on Windows, keeping OS knowledge in the Jobs module (spec §5.2). The SPA's Stack page gets a "Reference client" section.

**Tech Stack:** as phase 1 — .NET SDK 10.0.302, C# 14, minimal APIs, xunit.v3, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`; Angular 22.1.x, Vitest via `ng test`, Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` — §2.1 (Start/Stop frontend rows; `npm ci` is not a console operation), §5.2 (tree kill is the one OS-specific place), §5.4 FrontendSupervisor, §5.10 (`GET /stack` carries the frontend job summary; `POST /stack/frontend/start|stop`), §6 Stack screen, §9, §10, §12 phase 2.

**Decisions this plan makes where the spec is silent:**

- §5.4 says `Status()` reports "whether port 5173 answers". That probe already exists: `PlatformProbe` reports `client` at `Admin:ClientUrl` in `GET /api/stack`'s `reachability`. The supervisor's status is `{ job, installed }` and the SPA joins it with the `client` reachability entry rather than probing the port twice.
- Refusals are RFC 9457 problems with status 409: `Frontend already running`, `Frontend dependencies not installed`, `Frontend not running`. Start answers 202 with the job summary; Stop answers 200 with the stopped (exited) job's summary.
- A `npm start` the developer launched by hand is not tracked: Start runs a second one, `ng serve` finds 5173 taken and exits non-zero, and the job's output says so. No extra detection.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`. `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or editing `.cs` files and before every `dotnet build`/`dotnet test`, run `dotnet format whitespace BlueprintAdmin.slnx` from the repo root.
- Test names are sentences with underscores.
- The host listens on `127.0.0.1:5300` only; never add a listener, CORS header or credential store (spec §8).
- Every command the host runs is a line from `run-locally.md`: the frontend command is exactly `npm start` in `Admin:FrontendDir`. `npm ci` is never run by the console.
- FakePlatform is first-class: the new endpoints work against the fakes and the Playwright smoke covers the new controls.
- All `dotnet` commands run from the repository root; all `npm` commands from `src/Admin.Web`.
- Commit messages use `feat:`/`fix:`/`test:`/`docs:` prefixes and end with the attribution trailer the session provides. Before every commit, `git branch --show-current` must print `feat/phase-2-frontend` (the checkout is shared with other agents).
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Jobs/ExecutableResolver.cs              Task 1  bare name → what CreateProcess can start
src/Admin.Host/Jobs/ProcessRunner.cs                   Task 1  uses the resolver (one line)
tests/Admin.Host.Tests/Jobs/ExecutableResolverTests.cs Task 1
tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs      Task 1  + npm starts through the real runner
src/Admin.Host/Frontend/FrontendSupervisor.cs          Task 2  the one npm start job
src/Admin.Host/Frontend/FrontendStatus.cs              Task 2  wire shape + start result
tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs Task 2
src/Admin.Host/Frontend/FrontendEndpoints.cs           Task 3  POST start/stop
src/Admin.Host/Stack/StackEndpoints.cs                 Task 3  StackView gains Frontend
src/Admin.Host/Fakes/FakePlatformScripts.cs            Task 3  recorded ng serve output
src/Admin.Host/Program.cs                              Task 3  registration + MapFrontend
tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs Task 3
src/Admin.Web/src/app/core/host/host-types.ts          Task 4  FrontendStatus
src/Admin.Web/src/app/core/host/host-client.ts         Task 4  frontendStart/frontendStop
src/Admin.Web/src/app/core/host/host-client.spec.ts    Task 4
src/Admin.Web/src/app/features/stack/stack-page.{ts,html,css,spec.ts} Task 4
src/Admin.Web/e2e/frontend.spec.ts                     Task 5  Playwright smoke
README.md, CLAUDE.md                                   Task 5
```

## Task 0 (controller, not a subagent): branch

- [ ] From the repo root on a clean `main` at or after `891dbb3`: `git switch -c feat/phase-2-frontend`, then commit this plan: `git add docs/superpowers/plans/2026-09-15-blueprint-admin-phase-2-frontend.md && git commit -m "docs: phase 2 frontend implementation plan"`.

---

### Task 1: Resolve bare command names on Windows so `npm` can start

**Files:**
- Create: `src/Admin.Host/Jobs/ExecutableResolver.cs`
- Modify: `src/Admin.Host/Jobs/ProcessRunner.cs` (the `new ProcessStartInfo(spec.FileName)` line in `Start`)
- Create: `tests/Admin.Host.Tests/Jobs/ExecutableResolverTests.cs`
- Modify: `tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs`

**Interfaces:**
- Consumes: `ProcessRunner`, `ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)`, `JobRegistry` (phase 1).
- Produces: `public static class ExecutableResolver` with `string Resolve(string fileName)` and `string Resolve(string fileName, bool isWindows, string? path, string? pathExt, Func<string, bool> fileExists)`. `ProcessSpec.FileName` stays the bare name (`"npm"`), so `CommandLine` and `FakeProcessRunner` matching are unchanged.

- [ ] **Step 1: Write the failing real-runner test**

Append to `ProcessRunnerTests` (the class already has `registry` and `Runner`):

```csharp
    [Fact]
    public async Task Npm_starts_by_its_bare_name_on_every_platform()
    {
        // On Windows npm is npm.cmd, and CreateProcess only appends .exe; the
        // frontend supervisor starts "npm start" by name (spec §2.1).
        ProcessSpec spec = new("npm", ["--version"], Environment.CurrentDirectory);

        Job job = Runner.Start(spec);
        int exit = await job.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, string.Join(Environment.NewLine, job.Since(-1).Select(l => l.Text)));
        job.Since(-1).ShouldContain(l => l.Stream == OutputStream.Stdout && l.Text.Length > 0);
    }
```

- [ ] **Step 2: Run it and confirm it fails on Windows**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Npm_starts_by_its_bare_name"`
Expected on Windows: FAIL, exit `-1`, output line `Could not start 'npm': ...`. (On Linux it already passes; the resolver tests below are the cross-platform red.)

- [ ] **Step 3: Write the resolver's unit tests**

`tests/Admin.Host.Tests/Jobs/ExecutableResolverTests.cs`:

```csharp
using Admin.Host.Jobs;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class ExecutableResolverTests
{
    private const string Tools = @"C:\tools";
    private const string Node = @"C:\node";
    private const string PathExt = ".COM;.EXE;.BAT;.CMD";

    [Fact]
    public void Npm_resolves_to_its_cmd_shim_on_windows()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd")];

        ExecutableResolver.Resolve("npm", true, $"{Tools};{Node}", PathExt, files.Contains).ShouldBe(Path.Combine(Node, "npm.cmd"));
    }

    [Fact]
    public void An_earlier_path_directory_wins_over_a_better_extension_later()
    {
        HashSet<string> files = [Path.Combine(Tools, "npm.cmd"), Path.Combine(Node, "npm.exe")];

        ExecutableResolver.Resolve("npm", true, $"{Tools};{Node}", PathExt, files.Contains).ShouldBe(Path.Combine(Tools, "npm.cmd"));
    }

    [Fact]
    public void Within_a_directory_pathext_order_decides()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd"), Path.Combine(Node, "npm.exe")];

        ExecutableResolver.Resolve("npm", true, Node, PathExt, files.Contains).ShouldBe(Path.Combine(Node, "npm.exe"));
    }

    [Fact]
    public void An_empty_pathext_falls_back_to_the_windows_default()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd")];

        ExecutableResolver.Resolve("npm", true, Node, null, files.Contains).ShouldBe(Path.Combine(Node, "npm.cmd"));
    }

    [Fact]
    public void Names_are_unchanged_off_windows()
    {
        ExecutableResolver.Resolve("npm", false, Node, PathExt, _ => true).ShouldBe("npm");
    }

    [Theory]
    [InlineData("npm.cmd")]
    [InlineData(@"C:\node\npm")]
    [InlineData("./npm")]
    public void A_name_with_an_extension_or_a_directory_is_unchanged(string fileName)
    {
        ExecutableResolver.Resolve(fileName, true, Node, PathExt, _ => true).ShouldBe(fileName);
    }

    [Fact]
    public void An_unresolvable_name_is_returned_as_is_so_start_reports_it()
    {
        ExecutableResolver.Resolve("nope", true, $"{Tools};{Node}", PathExt, _ => false).ShouldBe("nope");
    }
}
```

- [ ] **Step 4: Run them to verify they fail to compile**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet build`
Expected: FAIL, `CS0103: The name 'ExecutableResolver' does not exist`.

- [ ] **Step 5: Implement the resolver**

`src/Admin.Host/Jobs/ExecutableResolver.cs`:

```csharp
namespace Admin.Host.Jobs;

/// <summary>
/// Turns a bare command name into something the operating system can start.
/// Windows' CreateProcess appends only <c>.exe</c>, so <c>npm</c>, which is
/// <c>npm.cmd</c> there, is "file not found" unless PATH is searched with
/// PATHEXT the way a shell does. Off Windows, and for a name that already
/// carries an extension or a directory, the name is returned unchanged; a name
/// that resolves nowhere is returned unchanged too, so the start fails with the
/// name the user recognises.
/// </summary>
public static class ExecutableResolver
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    public static string Resolve(string fileName) => Resolve(
        fileName,
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        File.Exists);

    public static string Resolve(string fileName, bool isWindows, string? path, string? pathExt, Func<string, bool> fileExists)
    {
        if (!isWindows || string.IsNullOrEmpty(path) || Path.HasExtension(fileName) || fileName.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return fileName;
        }

        string[] extensions = (string.IsNullOrEmpty(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, fileName + extension);

                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }
}
```

In `ProcessRunner.Start`, change

```csharp
        ProcessStartInfo info = new(spec.FileName)
```

to

```csharp
        ProcessStartInfo info = new(ExecutableResolver.Resolve(spec.FileName))
```

and extend the class summary's last sentence: after "which is what makes <c>ng serve</c> stoppable on Windows" add "; with <see cref=\"ExecutableResolver\"/> it is also what makes <c>npm</c> startable there".

- [ ] **Step 6: Run all host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: PASS, every test including `Npm_starts_by_its_bare_name_on_every_platform` and the seven `ExecutableResolverTests` cases (nine with theory rows).

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Host/Jobs/ExecutableResolver.cs src/Admin.Host/Jobs/ProcessRunner.cs tests/Admin.Host.Tests/Jobs/ExecutableResolverTests.cs tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs
git commit -m "fix(host): resolve bare command names through PATHEXT on Windows so npm starts"
```

---

### Task 2: FrontendSupervisor

**Files:**
- Create: `src/Admin.Host/Frontend/FrontendStatus.cs`
- Create: `src/Admin.Host/Frontend/FrontendSupervisor.cs`
- Create: `tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner.Start(ProcessSpec)`, `IProcessRunner.StopAsync(Job, CancellationToken)`, `Job.State`, `Job.ExitCode`, `JobState.Running`, `JobSummary.Of(Job)`, `RepoPaths(string BackendDir, string FrontendDir, string ComposeFile)`, `FakeProcessRunner.On/OnLongRunning/Started`.
- Produces (namespace `Admin.Host.Frontend`):
  - `public sealed record FrontendStatus(JobSummary? Job, bool Installed)`
  - `public enum FrontendStartOutcome { Started, AlreadyRunning, NotInstalled }`
  - `public sealed record FrontendStartResult(FrontendStartOutcome Outcome, Job? Job)` — `Job` is the new job for `Started`, the running one for `AlreadyRunning`, `null` for `NotInstalled`.
  - `public sealed class FrontendSupervisor(IProcessRunner runner, RepoPaths paths, Func<string, bool> installed) : IDisposable` with `static bool HasNodeModules(string frontendDir)`, `FrontendStatus Status()`, `Task<FrontendStartResult> StartAsync(CancellationToken)`, `Task<Job?> StopAsync(CancellationToken)` (returns the stopped job, or `null` when none was running).

- [ ] **Step 1: Write the failing tests**

`tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs`:

```csharp
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class FrontendSupervisorTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/compose.yml");

    private readonly JobRegistry registry = new(new FakeTimeProvider());
    private readonly FakeProcessRunner runner;

    public FrontendSupervisorTests()
    {
        runner = new FakeProcessRunner(registry);
    }

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    private FrontendSupervisor Supervisor(bool installed = true) => new(runner, Paths, _ => installed);

    [Fact]
    public async Task Start_runs_npm_start_in_the_frontend_clone()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendStartResult result = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FrontendStartOutcome.Started);
        result.Job!.State.ShouldBe(JobState.Running);
        ProcessSpec spec = runner.Started.Single();
        spec.FileName.ShouldBe("npm");
        spec.Arguments.ShouldBe(["start"]);
        spec.WorkingDirectory.ShouldBe("/repo/frontend");
    }

    [Fact]
    public async Task Start_refuses_while_a_job_is_running_and_starts_nothing()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult first = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        FrontendStartResult second = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        second.Outcome.ShouldBe(FrontendStartOutcome.AlreadyRunning);
        second.Job.ShouldBeSameAs(first.Job);
        runner.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_refuses_when_node_modules_is_absent()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor(installed: false);

        FrontendStartResult result = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FrontendStartOutcome.NotInstalled);
        result.Job.ShouldBeNull();
        runner.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_that_exited_on_its_own_can_be_started_again()
    {
        // ng serve exits at once when 5173 is taken; that job is over, not running.
        runner.On("npm", "start", 1, "Port 5173 is already in use.");
        using FrontendSupervisor supervisor = Supervisor();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        FrontendStartResult again = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        again.Outcome.ShouldBe(FrontendStartOutcome.Started);
        runner.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_starts_start_one_process()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();

        FrontendStartResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => supervisor.StartAsync(TestContext.Current.CancellationToken))));

        runner.Started.Count.ShouldBe(1);
        results.Count(r => r.Outcome == FrontendStartOutcome.Started).ShouldBe(1);
    }

    [Fact]
    public async Task Stop_kills_the_running_job_and_status_keeps_its_exit_code()
    {
        runner.OnLongRunning("npm", "start", "> ng serve");
        using FrontendSupervisor supervisor = Supervisor();
        FrontendStartResult started = await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Job? stopped = await supervisor.StopAsync(TestContext.Current.CancellationToken);

        stopped.ShouldBeSameAs(started.Job);
        stopped!.State.ShouldBe(JobState.Exited);
        FrontendStatus status = supervisor.Status();
        status.Job!.Id.ShouldBe(started.Job!.Id);
        status.Job.State.ShouldBe(JobState.Exited);
        status.Job.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Stop_with_nothing_running_returns_null()
    {
        using FrontendSupervisor supervisor = Supervisor();

        (await supervisor.StopAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void Status_before_any_start_has_no_job_and_reports_installation()
    {
        using FrontendSupervisor installed = Supervisor(installed: true);
        using FrontendSupervisor missing = Supervisor(installed: false);

        installed.Status().ShouldBe(new FrontendStatus(null, true));
        missing.Status().ShouldBe(new FrontendStatus(null, false));
    }

    [Fact]
    public void HasNodeModules_is_true_only_when_the_directory_exists()
    {
        DirectoryInfo clone = Directory.CreateTempSubdirectory("admin-frontend-");

        try
        {
            FrontendSupervisor.HasNodeModules(clone.FullName).ShouldBeFalse();

            clone.CreateSubdirectory("node_modules");

            FrontendSupervisor.HasNodeModules(clone.FullName).ShouldBeTrue();
        }
        finally
        {
            clone.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet build`
Expected: FAIL, `CS0246: The type or namespace name 'FrontendSupervisor' could not be found` (and the `Admin.Host.Frontend` namespace).

- [ ] **Step 3: Implement**

`src/Admin.Host/Frontend/FrontendStatus.cs`:

```csharp
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client as the Stack screen sees it: the last <c>npm start</c>
/// job, running or exited, and whether the clone has <c>node_modules</c>.
/// Whether port 5173 answers is the <c>client</c> entry of the stack's
/// reachability, not repeated here.
/// </summary>
public sealed record FrontendStatus(JobSummary? Job, bool Installed);

public enum FrontendStartOutcome
{
    Started,
    AlreadyRunning,
    NotInstalled,
}

/// <summary><see cref="Job"/> is the new job, the one already running, or null when dependencies are missing.</summary>
public sealed record FrontendStartResult(FrontendStartOutcome Outcome, Job? Job);
```

`src/Admin.Host/Frontend/FrontendSupervisor.cs`:

```csharp
using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Frontend;

/// <summary>
/// The reference client's one <c>npm start</c>, in the frontend clone (spec §5.4).
/// Start refuses while a job is running, and when <c>node_modules</c> is absent,
/// because <c>npm ci</c> is a prerequisite the console reports rather than runs
/// (spec §2.1). Stop kills the tree through the runner. Start and Stop are
/// serialised so two clicks cannot leave two <c>ng serve</c> processes.
/// </summary>
public sealed class FrontendSupervisor(IProcessRunner runner, RepoPaths paths, Func<string, bool> installed) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Written under the gate; Status reads it without, and a reference read is atomic.
    private volatile Job? current;

    public static bool HasNodeModules(string frontendDir) => Directory.Exists(Path.Combine(frontendDir, "node_modules"));

    public FrontendStatus Status()
    {
        Job? job = current;

        return new FrontendStatus(job is null ? null : JobSummary.Of(job), installed(paths.FrontendDir));
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

            if (!installed(paths.FrontendDir))
            {
                return new FrontendStartResult(FrontendStartOutcome.NotInstalled, null);
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

- [ ] **Step 4: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~FrontendSupervisorTests"`
Expected: PASS, 9 tests. Then `dotnet test` — all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Admin.Host/Frontend tests/Admin.Host.Tests/Frontend/FrontendSupervisorTests.cs
git commit -m "feat(host): FrontendSupervisor holds the one npm start job"
```

---

### Task 3: Frontend endpoints, the stack view and FakePlatform's `ng serve`

**Files:**
- Create: `src/Admin.Host/Frontend/FrontendEndpoints.cs`
- Modify: `src/Admin.Host/Stack/StackEndpoints.cs` (`GET /api/stack` handler and `StackView`)
- Modify: `src/Admin.Host/Fakes/FakePlatformScripts.cs` (`Script` gains an `npm start` script)
- Modify: `src/Admin.Host/Program.cs` (register the supervisor; `app.MapFrontend()` after `app.MapStack()`)
- Create: `tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs`

**Interfaces:**
- Consumes: everything Task 2 produces; `AdminHostFactory` (tests); `FakeProcessRunner.OnLongRunning(string, string, params string[])`.
- Produces (wire, camelCase JSON, enums as strings):
  - `GET /api/stack` → `{ backend, frontend: { job: JobSummary | null, installed: bool }, reachability }`. C#: `public sealed record StackView(ComposeStatus Backend, FrontendStatus Frontend, IReadOnlyList<Reachability> Reachability)`.
  - `POST /api/stack/frontend/start` → 202 `JobSummary`; 409 problem titled `Frontend already running` or `Frontend dependencies not installed`.
  - `POST /api/stack/frontend/stop` → 200 `JobSummary` (state `Exited`); 409 problem titled `Frontend not running`.
  - FakePlatform `npm start` output includes the line `  Local:   http://localhost:5173/`.
  - `public static IEndpointRouteBuilder MapFrontend(this IEndpointRouteBuilder app)` in `Admin.Host.Frontend.FrontendEndpoints`.

- [ ] **Step 1: Write the failing endpoint tests**

Each test owns its factory, because the supervisor is a singleton whose state would otherwise leak between tests in any order.

`tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class FrontendEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_reports_an_installed_frontend_with_no_job_before_any_start()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);

        JsonElement frontend = stack.GetProperty("frontend");
        frontend.GetProperty("installed").GetBoolean().ShouldBeTrue();
        frontend.GetProperty("job").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Start_runs_npm_start_and_replays_the_ng_serve_banner()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/start", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("commandLine").GetString().ShouldBe("npm start");
        job.GetProperty("state").GetString().ShouldBe("Running");

        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.GetProperty("id").GetString()}", Token);
        view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).ShouldContain("  Local:   http://localhost:5173/");

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);
        stack.GetProperty("frontend").GetProperty("job").GetProperty("state").GetString().ShouldBe("Running");
    }

    [Fact]
    public async Task A_second_start_while_running_is_a_conflict()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        (await client.PostAsync("/api/stack/frontend/start", null, Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage second = await client.PostAsync("/api/stack/frontend/start", null, Token);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Frontend already running");
        factory.Services.GetRequiredService<FakeProcessRunner>().Started.Count(s => s.FileName == "npm").ShouldBe(1);
    }

    [Fact]
    public async Task Stop_kills_the_job_and_a_second_stop_is_a_conflict()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        await client.PostAsync("/api/stack/frontend/start", null, Token);

        HttpResponseMessage stop = await client.PostAsync("/api/stack/frontend/stop", null, Token);

        stop.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement job = await stop.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("state").GetString().ShouldBe("Exited");
        job.GetProperty("exitCode").GetInt32().ShouldBe(-1);

        HttpResponseMessage again = await client.PostAsync("/api/stack/frontend/stop", null, Token);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Frontend not running");
    }

    [Fact]
    public async Task Start_without_node_modules_is_refused_and_says_to_run_npm_ci()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> uninstalled = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => new FrontendSupervisor(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(), _ => false))));
        HttpClient client = uninstalled.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/start", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Frontend dependencies not installed");
        problem.GetProperty("detail").GetString()!.ShouldContain("npm ci");
        uninstalled.Services.GetRequiredService<FakeProcessRunner>().Started.ShouldNotContain(s => s.FileName == "npm");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("frontend").GetProperty("installed").GetBoolean().ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~FrontendEndpointTests"`
Expected: FAIL — `frontend` property missing from `/api/stack` (`KeyNotFoundException`) and the start/stop routes answer 404.

- [ ] **Step 3: Implement the endpoints**

`src/Admin.Host/Frontend/FrontendEndpoints.cs`:

```csharp
using Admin.Host.Config;
using Admin.Host.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Frontend;

public static class FrontendEndpoints
{
    public static IEndpointRouteBuilder MapFrontend(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/stack/frontend/start", async Task<Results<Accepted<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, RepoPaths paths, CancellationToken cancellationToken) =>
        {
            FrontendStartResult result = await supervisor.StartAsync(cancellationToken);

            return result.Outcome switch
            {
                FrontendStartOutcome.Started => TypedResults.Accepted((string?)null, JobSummary.Of(result.Job!)),
                FrontendStartOutcome.AlreadyRunning => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Frontend already running",
                    detail: $"npm start is job {result.Job!.Id}. Stop it first."),
                _ => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Frontend dependencies not installed",
                    detail: $"{paths.FrontendDir} has no node_modules. Run npm ci there; the console does not."),
            };
        });

        app.MapPost("/api/stack/frontend/stop", async Task<Results<Ok<JobSummary>, ProblemHttpResult>> (FrontendSupervisor supervisor, CancellationToken cancellationToken) =>
            await supervisor.StopAsync(cancellationToken) is Job stopped
                ? TypedResults.Ok(JobSummary.Of(stopped))
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Frontend not running", detail: "There is no npm start job to stop."));

        return app;
    }
}
```

In `StackEndpoints.MapStack`, the `GET /api/stack` handler becomes:

```csharp
        app.MapGet("/api/stack", async (ComposeService compose, FrontendSupervisor frontend, PlatformProbe probe, CancellationToken cancellationToken) =>
        {
            Task<ComposeStatus> backend = compose.PsAsync(cancellationToken);
            Task<IReadOnlyList<Reachability>> reachability = probe.ProbeAsync(cancellationToken);

            return TypedResults.Ok(new StackView(await backend, frontend.Status(), await reachability));
        });
```

and the record becomes (add `using Admin.Host.Frontend;`):

```csharp
public sealed record StackView(ComposeStatus Backend, FrontendStatus Frontend, IReadOnlyList<Reachability> Reachability);
```

- [ ] **Step 4: Register the supervisor and record `ng serve`**

`Program.cs`: add `using Admin.Host.Frontend;`; after `builder.Services.AddSingleton<LogFollower>();` add

```csharp
// In FakePlatform mode the frontend clone may not exist at all, and its npm
// start is a recording, so the node_modules check would refuse for nothing.
builder.Services.AddSingleton(sp =>
{
    Func<string, bool> installed = sp.GetRequiredService<IOptions<AdminOptions>>().Value.FakePlatform
        ? _ => true
        : FrontendSupervisor.HasNodeModules;

    return new FrontendSupervisor(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(), installed);
});
```

and after `app.MapStack();` add `app.MapFrontend();`.

`FakePlatformScripts`: add the field below `FollowLogLines`

```csharp
    // npm start in the frontend clone as the Angular dev server prints it. It
    // keeps running until stopped, as ng serve does.
    private static readonly string[] NgServeLines =
    [
        "> blueprint-frontend@0.0.0 start",
        "> ng serve",
        "Initial chunk files | Names  | Raw size",
        "main.js             | main   | 212.40 kB",
        "styles.css          | styles |  95.12 kB",
        "Application bundle generation complete. [2.315 seconds]",
        "Watch mode enabled. Watching for file changes...",
        "  Local:   http://localhost:5173/",
    ];
```

and chain onto the end of `Script`'s `return runner ...` expression, after the `logs -f` line:

```csharp
            .OnLongRunning("npm", "start", NgServeLines);
```

(move the terminating `;` from the `logs -f` line to this one).

- [ ] **Step 5: Run all host tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: PASS, including the 5 `FrontendEndpointTests` and the unchanged `StackEndpointTests`/`FakePlatformTests`.

- [ ] **Step 6: Smoke the real host by hand**

Run: `dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true` in the background, then
`curl -s -X POST http://127.0.0.1:5300/api/stack/frontend/start` → a `Running` summary with `"commandLine":"npm start"`;
`curl -s http://127.0.0.1:5300/api/stack` → `"frontend":{"job":{...,"state":"Running"...},"installed":true}`;
`curl -s -X POST http://127.0.0.1:5300/api/stack/frontend/stop` → `"state":"Exited","exitCode":-1`. Stop the host.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Host/Frontend/FrontendEndpoints.cs src/Admin.Host/Stack/StackEndpoints.cs src/Admin.Host/Fakes/FakePlatformScripts.cs src/Admin.Host/Program.cs tests/Admin.Host.Tests/Frontend/FrontendEndpointTests.cs
git commit -m "feat(host): frontend start/stop endpoints and the frontend on GET /api/stack"
```

---

### Task 4: The Stack screen's reference-client controls

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`
- Modify: `src/Admin.Web/src/app/core/host/host-client.ts`
- Modify: `src/Admin.Web/src/app/core/host/host-client.spec.ts`
- Modify: `src/Admin.Web/src/app/features/stack/stack-page.ts`, `stack-page.html`, `stack-page.css`, `stack-page.spec.ts`

**Interfaces:**
- Consumes: the Task 3 wire shapes.
- Produces: `interface FrontendStatus { job: JobSummary | null; installed: boolean }`; `StackView.frontend: FrontendStatus`; `HostClient.frontendStart(): Observable<JobSummary>`, `HostClient.frontendStop(): Observable<JobSummary>`. DOM contract the Playwright smoke (Task 5) uses: section `.frontend`, status line `.frontend-status` whose text contains `npm start` and one of `not started` / `running` / `exited <code>`; buttons with accessible names `Start frontend`, `Stop frontend`, `Show output`.

- [ ] **Step 1: Write the failing HostClient tests**

Append inside the `describe` in `host-client.spec.ts`:

```ts
  it('starts the frontend', () => {
    client.frontendStart().subscribe();

    const req = http.expectOne('/api/stack/frontend/start');
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' });
  });

  it('stops the frontend', () => {
    client.frontendStop().subscribe();

    const req = http.expectOne('/api/stack/frontend/stop');
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'fe-1', commandLine: 'npm start', state: 'Exited', exitCode: -1, startedAt: '' });
  });
```

- [ ] **Step 2: Write the failing StackPage tests**

In `stack-page.spec.ts`:

1. Give the shared `stack` fixture a frontend and a client reachability entry:

```ts
const stack = {
  backend: { /* unchanged */ },
  frontend: { job: null, installed: true },
  reachability: [
    { name: 'gateway', url: 'http://localhost:5000/health/ready', up: true, status: 200 },
    { name: 'client', url: 'http://localhost:5173/', up: false, status: null },
  ],
};
```

(keep `backend` exactly as it is), and set `config.frontendDir` to `'/work/blueprint-frontend'`.

2. Extend the `host` type and mock in `beforeEach`:

```ts
  let host: {
    stack: ReturnType<typeof vi.fn>;
    config: ReturnType<typeof vi.fn>;
    backendUp: ReturnType<typeof vi.fn>;
    backendDown: ReturnType<typeof vi.fn>;
    frontendStart: ReturnType<typeof vi.fn>;
    frontendStop: ReturnType<typeof vi.fn>;
  };
```

```ts
      frontendStart: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' })),
      frontendStop: vi.fn(() => of({ id: 'fe-1', commandLine: 'npm start', state: 'Exited', exitCode: -1, startedAt: '' })),
```

3. Add a helper under the `const config` declaration and the tests at the end of the `describe`:

```ts
const runningJob = { id: 'fe-1', commandLine: 'npm start', state: 'Running', exitCode: null, startedAt: '' };

function text(el: Element | null): string {
  return el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}
```

```ts
  describe('reference client', () => {
    async function render() {
      const fixture = TestBed.createComponent(StackPage);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      const q = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLButtonElement | null;
      return { fixture, q };
    }

    it('shows a frontend that was never started, with Start enabled and Stop disabled', async () => {
      const { fixture, q } = await render();

      expect(text(q('.frontend-status'))).toBe('npm start not started client does not answer');
      expect(q('button.frontend-start')!.disabled).toBe(false);
      expect(q('button.frontend-stop')!.disabled).toBe(true);
      expect(q('button.frontend-output')).toBeNull();
      expect(fixture.nativeElement.textContent).not.toContain('npm ci');
    });

    it('starts the frontend and hands its job to the output pane', async () => {
      const { fixture, q } = await render();

      q('button.frontend-start')!.click();
      fixture.detectChanges();

      expect(host.frontendStart).toHaveBeenCalled();
      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('disables Start and enables Stop while npm start runs, and Stop calls the host', async () => {
      host.stack.mockReturnValue(
        of({
          ...stack,
          frontend: { job: runningJob, installed: true },
          reachability: [{ name: 'client', url: 'http://localhost:5173/', up: true, status: 200 }],
        }),
      );
      const { fixture, q } = await render();

      expect(text(q('.frontend-status'))).toBe('npm start running client answers');
      expect(q('button.frontend-start')!.disabled).toBe(true);
      expect(q('button.frontend-stop')!.disabled).toBe(false);

      q('button.frontend-stop')!.click();
      fixture.detectChanges();

      expect(host.frontendStop).toHaveBeenCalled();
      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('shows the exit code of a job that ended', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: { ...runningJob, state: 'Exited', exitCode: 1 }, installed: true } }));
      const { q } = await render();

      expect(text(q('.frontend-status'))).toContain('npm start exited 1');
      expect(q('button.frontend-start')!.disabled).toBe(false);
    });

    it('opens the output of the last job on Show output', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: runningJob, installed: true } }));
      const { fixture, q } = await render();

      q('button.frontend-output')!.click();

      expect(fixture.componentInstance.jobId()).toBe('fe-1');
    });

    it('says npm ci is needed and disables Start when node_modules is absent', async () => {
      host.stack.mockReturnValue(of({ ...stack, frontend: { job: null, installed: false } }));
      const { fixture, q } = await render();

      expect(fixture.nativeElement.textContent).toContain('/work/blueprint-frontend has no node_modules. Run npm ci there; the console does not.');
      expect(q('button.frontend-start')!.disabled).toBe(true);
    });

    it("shows the host's refusal when a start conflicts", async () => {
      host.frontendStart.mockReturnValueOnce(
        throwError(() => ({ error: { title: 'Frontend already running', detail: 'npm start is job fe-0. Stop it first.' } })),
      );
      const { fixture, q } = await render();

      q('button.frontend-start')!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('npm start is job fe-0. Stop it first.');
    });
  });
```

- [ ] **Step 3: Run them to verify they fail**

Run: `npm test`
Expected: FAIL — TypeScript errors for `frontendStart`/`frontendStop` on `HostClient`, and `.frontend-status` not found.

- [ ] **Step 4: Implement types and client**

`host-types.ts`: add above `StackView`

```ts
/** The reference client's last `npm start` and whether its clone has node_modules (spec §5.4). */
export interface FrontendStatus {
  job: JobSummary | null;
  installed: boolean;
}
```

and add `frontend: FrontendStatus;` to `StackView` between `backend` and `reachability`.

`host-client.ts`: add after `backendDown`

```ts
  frontendStart(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/start', null);
  }

  frontendStop(): Observable<JobSummary> {
    return this.http.post<JobSummary>('/api/stack/frontend/stop', null);
  }
```

- [ ] **Step 5: Implement the page**

`stack-page.ts`: add after `readonly error = signal<string | null>(null);`

```ts
  /** Whether the client's port answers: the `client` reachability entry, which is how spec §5.4's port check reaches this screen. */
  readonly clientUp = computed(() => this.stack()?.reachability.find((r) => r.name === 'client')?.up ?? false);
  readonly frontendRunning = computed(() => this.stack()?.frontend.job?.state === 'Running');
```

and after `wipe()`:

```ts
  startFrontend(): void {
    this.host.frontendStart().subscribe(this.started);
  }

  stopFrontend(): void {
    this.host.frontendStop().subscribe(this.started);
  }

  showFrontendOutput(): void {
    const id = this.stack()?.frontend.job?.id;
    if (id) {
      this.jobId.set(id);
    }
  }
```

`stack-page.html`: inside the `@if (stack(); as s) { ... }` block, directly after `</table>`, add

```html
  <section class="frontend">
    <h2>Reference client</h2>
    <p class="frontend-status">
      npm start
      @if (s.frontend.job; as job) {
        @if (job.state === 'Running') { running } @else { exited {{ job.exitCode }} }
      } @else {
        not started
      }
      <span [class.up]="clientUp()" [class.down]="!clientUp()">client {{ clientUp() ? 'answers' : 'does not answer' }}</span>
    </p>
    @if (!s.frontend.installed) {
      <p class="error">{{ config()?.frontendDir ?? 'The frontend clone' }} has no node_modules. Run npm ci there; the console does not.</p>
    }
    <button class="frontend-start" [disabled]="frontendRunning() || !s.frontend.installed" (click)="startFrontend()">Start frontend</button>
    <button class="frontend-stop" [disabled]="!frontendRunning()" (click)="stopFrontend()">Stop frontend</button>
    @if (s.frontend.job) {
      <button class="frontend-output" (click)="showFrontendOutput()">Show output</button>
    }
  </section>
```

`stack-page.css`: append

```css
.frontend { margin-bottom: 1rem; }
.frontend h2 { font-size: 1rem; margin: 0 0 0.5rem; }
.frontend-status span { margin-left: 0.75rem; padding: 0.1rem 0.4rem; border-radius: 3px; }
.frontend-status .up { background: #dfd; }
.frontend-status .down { background: #fdd; }
.frontend button { margin-right: 0.5rem; }
```

- [ ] **Step 6: Run lint, tests and build**

Run: `npm run lint && npm test && npm run build`
Expected: lint clean; all Vitest specs pass (the 8 existing StackPage tests plus 7 new, 2 new HostClient tests); build succeeds. If the exact `.frontend-status` strings differ only by whitespace inside Angular's `@if` blocks, fix the template, not the assertion.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Web/src/app/core/host src/Admin.Web/src/app/features/stack
git commit -m "feat(web): start, stop and follow the reference client from the Stack screen"
```

---

### Task 5: Playwright smoke and docs

**Files:**
- Create: `src/Admin.Web/e2e/frontend.spec.ts`
- Modify: `README.md` ("What it does today", "Known limits in phase 1")
- Modify: `CLAUDE.md` ("Owners of facts" paragraph)

**Interfaces:**
- Consumes: the Task 4 DOM contract; FakePlatform `ng serve` output from Task 3; `POST /api/stack/frontend/stop` 200/409.
- Produces: nothing later tasks use.

- [ ] **Step 1: Write the smoke**

`src/Admin.Web/e2e/frontend.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

// The fake host lives for the whole run and a CI retry re-enters with whatever
// the failed attempt left running, so every test starts from a stopped frontend.
test.beforeEach(async ({ request }) => {
  const stopped = await request.post('/api/stack/frontend/stop');
  expect([200, 409]).toContain(stopped.status());
});

test('the stack screen starts and stops the reference client', async ({ page }) => {
  await page.goto('/stack');
  const status = page.locator('.frontend-status');
  const start = page.getByRole('button', { name: 'Start frontend' });
  const stop = page.getByRole('button', { name: 'Stop frontend' });
  const output = page.locator('app-output-pane pre');

  await expect(status).toContainText('npm start');
  await expect(start).toBeEnabled();
  await expect(stop).toBeDisabled();

  await start.click();
  await expect(output).toContainText('http://localhost:5173/');
  // The buttons follow the 3-second stack poll.
  await expect(status).toContainText('running', { timeout: 10_000 });
  await expect(stop).toBeEnabled();

  await stop.click();
  await expect(output).toContainText('exited -1');
  await expect(status).toContainText('exited -1', { timeout: 10_000 });
  await expect(start).toBeEnabled();
});
```

- [ ] **Step 2: Run the smoke**

Run from the repo root: `dotnet build`, then in `src/Admin.Web`: `npm run build && npm run e2e`
Expected: PASS, every existing `stack.spec.ts`/`logs.spec.ts` test plus this one. If a host is already listening on 5300 that is not in FakePlatform mode, `global-setup.ts` refuses; stop it first.

- [ ] **Step 3: Update README.md**

Replace the "What it does today" paragraph with:

```markdown
Phases 0 to 2 of the spec: the Stack screen (Compose services, reachability,
up, down, down with a typed `down -v` confirmation, live output; the reference
client's `npm start` with start, stop and its output) and the Logs screen
(follow, service filter, text filter, correlation-id highlight). The API
console, broker inspection and the event trace are the spec's later phases.
```

Rename the heading `## Known limits in phase 1` to `## Known limits` and replace its bullets with:

```markdown
- The host keeps at most one `logs -f` job: Follow stops the previous one
  before starting the next. Stop on the Logs screen only closes the browser's
  stream; the job keeps running until the next Follow or until the host exits.
- The console runs `npm start` but never `npm ci`: with no `node_modules` in
  the frontend clone, Start is refused and the screen says so.
- A client started by hand is not the console's: Start runs a second
  `npm start`, which exits because port 5173 is taken, and its output says so.
- No API console, broker inspection or event trace yet.
```

- [ ] **Step 4: Update CLAUDE.md**

In the "Owners of facts you will be tempted to restate" paragraph, append this sentence at the end: "The operating system is known in two files only, `src/Admin.Host/Jobs/ProcessRunner.cs` (tree kill) and `src/Admin.Host/Jobs/ExecutableResolver.cs` (`npm` is `npm.cmd` on Windows); start commands by bare name."

- [ ] **Step 5: Full verification**

From the repo root: `dotnet format whitespace BlueprintAdmin.slnx --verify-no-changes && dotnet build && dotnet test`; in `src/Admin.Web`: `npm run lint && npm test && npm run build && npm run e2e`.
Expected: all green.

- [ ] **Step 6: Real-mode check (Windows workstation, only if `../blueprint-frontend/node_modules` exists)**

Run `dotnet run --project src/Admin.Host`, open http://127.0.0.1:5300/stack, click **Start frontend**: the output pane shows `ng serve`'s build and `Local: http://localhost:5173/`, the status line turns to `running` and `client answers`. Click **Stop frontend**: `exited -1`, and within one poll `client does not answer`; `netstat -ano | findstr :5173` shows no listener (the whole tree died). Record the outcome in the task report; skip with a note if the clone has no `node_modules`.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Web/e2e/frontend.spec.ts README.md CLAUDE.md
git commit -m "test(web): Playwright smoke for frontend start and stop; docs for phase 2"
```

---

### Task 6: Stop reaches every descendant on Windows, and never hangs

*Added during execution (ledger ruling after Task 5's real-mode check).* Against a real host, `POST /api/stack/frontend/stop` hung for minutes: the tracked `npm.cmd` root and an intermediate `cmd.exe` had already exited, the `ng serve` `node.exe` survived as an orphan holding 5173 and the inherited stdout/stderr pipes, `Process.Kill(entireProcessTree: true)` (which walks live parent links) could not find it, and `ProcessRunner.StopAsync` awaits `job.Completion`, which `CompleteWhenExitedAsync` only completes once the pipes reach EOF. Spec §5.2 makes killing the tree "the reason `ng serve` can be stopped at all on Windows"; phase 2 is not done without it.

**Files:**
- Modify: `src/Admin.Host/Jobs/ProcessRunner.cs`
- Create (if the design below needs it): `src/Admin.Host/Jobs/WindowsJobObject.cs` (P/Invoke wrapper, `[SupportedOSPlatform("windows")]`)
- Modify: `tests/Admin.Host.Tests/Jobs/ProcessRunnerTests.cs`
- Modify: `CLAUDE.md` only if the OS-boundary sentence (Owners paragraph) no longer names the right files
- Modify: `docs/superpowers/plans/2026-09-15-blueprint-admin-phase-2-frontend.md` is already amended by the controller; commit it with this task

**Interfaces:**
- Consumes: `IProcessRunner.Start(ProcessSpec)` / `StopAsync(Job, CancellationToken)`, `Job.MarkExited(int)` (idempotent: a second call is a no-op), `Job.Completion`, `ExecutableResolver.Resolve`.
- Produces: no signature changes. Behavioural contract of `ProcessRunner`:
  1. `StopAsync` kills the started process **and every process it started, directly or through intermediates that have since exited**, on Windows.
  2. `StopAsync` returns within a bounded time (the kill, then at most 10 seconds' wait) even if some handle keeps the output pipes open; in that case the job is marked exited with `-1` and a warning is logged, so a Stop request never hangs.
  3. A root that exits on its own while descendants still write output keeps the job `Running` (the output is still live) and `StopAsync` still kills those descendants.
  4. Host disposal (`DisposeAsync`) has the same reach.

**Required approach (Windows):** assign each started process to its own Win32 Job Object immediately after `Process.Start()` (`CreateJobObject`, `SetInformationJobObject` with `JOBOBJECT_EXTENDED_LIMIT_INFORMATION.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `AssignProcessToJobObject`); stop with `TerminateJobObject`; close the job handle when the job completes. Descendants inherit job membership, so orphans are reached, and kill-on-close also means a crashed host takes its children with it. Keep `Kill(entireProcessTree: true)` as the non-Windows path and as the Windows fallback when assignment fails (log it). Document the unavoidable window: a child spawned between `Process.Start()` and assignment escapes the job; for `npm`, `docker` and `node` the first spawn comes after runtime start-up. Use `SafeHandle` for the job handle and `[LibraryImport]`/`[DllImport]` per the analyser policy.

- [ ] **Step 1: Reproduce with a failing test (Windows)** — in `ProcessRunnerTests`, a test that starts `node -e` with a script that spawns an intermediate `node` which spawns a long-lived grandchild (`stdio: 'inherit'`, the grandchild prints `pid=<process.pid>` then ticks every 50 ms) and then **exits**; the root may also exit. Wait until the `pid=` line appears, call `StopAsync` wrapped in `WaitAsync(TimeSpan.FromSeconds(20))`, then assert the job is `Exited` and the grandchild PID is no longer a running process. Mark it Windows-only (xunit v3 `Assert.SkipUnless(OperatingSystem.IsWindows(), ...)` or the project's existing skip idiom). Run it: it must FAIL today (timeout or surviving PID). Record the RED output.
- [ ] **Step 2: A bounded-stop test (all platforms)** — a runner-level test showing `StopAsync` returns within ~15 s and the job is `Exited` even when a descendant that escaped the kill still holds the pipes. If no portable way exists to make a descendant escape, cover contract 2 on Windows by spawning the grandchild detached from the job via `cmd /c start /b` is NOT acceptable (it would be inside the job); instead unit-test the timeout path by extracting the wait into a small internal seam, or state in the report why contract 2 is covered by Step 1 alone.
- [ ] **Step 3: Implement** the Job Object path and the bounded wait.
- [ ] **Step 4: Run** `dotnet format whitespace BlueprintAdmin.slnx && dotnet build && dotnet test` (all pass, output pristine).
- [ ] **Step 5: Real-mode verification (Windows workstation):** with `../blueprint-frontend/node_modules` present and ports 5300/5173 free, run the real host (`dotnet run --project src/Admin.Host`), and three times: `POST /api/stack/frontend/start` → poll `GET /api/stack` until the `client` reachability entry is up → `POST /api/stack/frontend/stop` returns 200 within 15 s with `exitCode` present → `netstat -ano | findstr :5173` shows no LISTENING socket and no `node.exe` with `ng.js serve` in its command line remains. Then stop the host (Ctrl+C-equivalent kill of the process you started) and confirm 5300 and 5173 are free. Paste the outputs in the report.
- [ ] **Step 6: Commit** — `fix(host): stop kills orphaned descendants through a Windows job object and never hangs`, including the amended plan file.
