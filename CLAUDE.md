# CLAUDE.md

A locator and the hard rules. Anything that moves when a PR lands — what has
been built, a count of anything, a port — is owned elsewhere and cited by
name, never restated. **Read the file that covers what you are about to
touch, before you touch it**, and read a long one by its index or a grep, not
whole.

| | |
|---|---|
| [`README.md`](README.md) | How to run it, what the screens do today, known limits |
| `TODO.md` (gitignored, local) | Open PRs and issues, for the user; every task change edits it |
| [`docs/working-rules.md`](docs/working-rules.md) | Build policy, style, working rules, precedence, commands |
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: trust order, classes, touch sets; the gate reads `.github/locality-gate/classes.yml` |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses; start at its index |
| [`docs/commands/`](docs/commands/) | The argument for each long command in `.claude/commands/`, whose runbook cites it by heading; read only when a command is disputed or edited |
| [`docs/style-guide.md`](docs/style-guide.md) | Pointers to the two dialects; `/style-pass` records a newly settled form here |
| [`docs/testing.md`](docs/testing.md) | Host vs SPA vs smoke vs harness; FakePlatform for e2e |
| [`docs/token-plan.md`](docs/token-plan.md), [`docs/token-usage.md`](docs/token-usage.md) | Where a session's tokens go, and the plan to cut them |
| [`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`](docs/superpowers/specs/2026-09-14-blueprint-admin-design.md) | The design. §1 before changing behaviour; §13 before adding any |
| [`docs/superpowers/plans/`](docs/superpowers/plans/) | Frozen delivery record, phases 0–5; grep it, never read it whole |
| [`Directory.Build.props`](Directory.Build.props), [`.editorconfig`](.editorconfig) | ADR-019 analysis and the C# dialect shared with the backend |
| [`src/Admin.Host/Config/AdminOptions.cs`](src/Admin.Host/Config/AdminOptions.cs) | Ports, sibling paths, Grafana/Keycloak URLs, FakePlatform |
| [`.claude/skills/`](.claude/skills/) | `codebase-index` (read ranges, not whole files); `admin-invariants` (run-locally, FakePlatform, siblings) |
| [`.mcp.json`](.mcp.json) | Local index as an MCP server (`--root .`) |

## What this repo is

`blueprint-admin` is the **local console** for `alexander-shamray/blueprint-backend`:
one page that runs, stops, observes and exercises the platform, doing verbatim
what the workspace's `run-locally.md` says a developer does by hand. It binds
**loopback only**, holds the realm passwords, is **never deployed** and has
**no login**. Spec §8 is why; never add a listener, a CORS header or a
credential store.

It is a **client of two siblings it does not own**. Nothing in
`../blueprint-backend` or `../blueprint-frontend` is edited from here. A
platform fact that is not observable is raised there. Every command the host
runs is a line from `run-locally.md`; if the console needs something that
document does not do, change that document's owner first.

```
src/Admin.Host/            .NET 10 minimal API: Compose, npm as children, HTTP
                           proxy, SSE. Composition root: Program.cs
src/Admin.Web/             Angular 22 standalone SPA, built into Host/wwwroot
tests/Admin.Host.Tests/    xunit
src/Admin.Web/e2e/         Playwright (FakePlatform)
tools/token-usage/         Transcript report for the token plan
.github/workflows/         host, web, smoke
```

### Owners of facts you will be tempted to restate

| Fact | Owner |
|---|---|
| Ports, sibling dirs, FakePlatform | `AdminOptions.cs` |
| SPA dev server and `/api` proxy | `src/Admin.Web/angular.json`, `proxy.conf.json` |
| Compose service list | backend `deploy/compose/` |
| Fake recordings | `src/Admin.Host/Fakes/` |
| Gateway routes (copied, cited) | `src/Admin.Host/Api/GatewayRoutes.cs` |
| Example request bodies | `src/Admin.Host/Api/RunLocallyExamples.cs` — change when `run-locally.md` does |
| Broker service | `src/Admin.Host/Broker/BrokerService.cs` |
| Platform queues, projection queue (copied, cited) | `PlatformQueues.Table` in `src/Admin.Host/Broker/PlatformQueues.cs` |
| Drain watch after publish | `PUBLISH_OPERATION` in `src/Admin.Web/src/app/features/api/api-page.ts` |
| Grafana / Loki / Explore | `GrafanaClient.cs`, `ExploreLink.cs` |
| Golden-signal queries (copied, cited) | `GoldenSignals.cs` |
| What a Tempo span is | `SpanRecogniser.Table` in `SpanRecogniser.cs` |
| OS (tree kill, job object, `npm.cmd`) | `ProcessRunner.cs`, `WindowsJobObject.cs`, `ExecutableResolver.cs` |
| Token figures | `docs/token-usage.md` |

## The one rule that matters

**Every fact has exactly one owner, and every other mention cites the owner**
— by symbol or file, never by value. A stale restatement met in passing is
left; it is not this change. A code change that contradicts the spec is not
done until the spec is amended in the same PR, or the code is changed to
match. This file is inside the rule too, and **no gate reads it**.

## The commands

From the repository root unless noted.

```bash
dotnet run --project src/Admin.Host
dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true
dotnet test
dotnet format whitespace BlueprintAdmin.slnx   # after creating .cs files

# in src/Admin.Web
npm ci                 # from the lockfile — never `npm install`
npm start              # SPA; port in angular.json, not AdminOptions
npm run build          # lands in Admin.Host/wwwroot
npm test
npm run lint
npm run e2e            # Playwright against FakePlatform; needs the host
```

Checks are `bash .claude/scripts/host-checks.sh` and
`bash .claude/scripts/npm-checks.sh`. The host refuses to start if
`Admin:BackendDir` / `Admin:FrontendDir` are wrong, naming the key.

## Subagents, review rounds and plans

Every turn re-sends the whole context, so an agent costs what it reads times
the turns it takes; `docs/token-plan.md` is where the spend was measured.

- **Delegate with a brief.** A subagent is handed the files, diff or question
  it needs and returns a short structured answer, findings as `file:line`.
  `Explore` before `general-purpose` for a search; nothing is delegated that
  one `Grep` answers.
- **The review loop** is `/ship` step 5's: rounds of one read-only
  `branch-reviewer`, handed the diff under `artifacts/review/` and not the
  author's conclusions, to a seven-round ceiling. Round 2 onwards reads the
  diff since the last round and the open findings, not the tree. Never
  `general-purpose`, never the session reviewing its own diff.
- **Plans.** A change that fits one PR takes plan mode, not a plan file; a
  plan cites code by path and symbol, and stays under 50 KB.
- **Before a wait, shed the context.** A session about to wait on CI or a
  background agent compacts first: a wake re-sends all of it.
- **One task per session**: `/clear` between tasks.

## Hard rules

Each is argued in [`docs/working-rules.md`](docs/working-rules.md).

- **`npm ci`, never `npm install`**: `install` may rewrite the lockfile.
- **A `.cs` written with LF fails IDE0055 on every line**; `.gitattributes`
  checks C# out CRLF. Run `dotnet format whitespace` after creating one.
- **FakePlatform is first-class**: a new endpoint is not done until it works
  against the fakes and the Playwright smoke covers its screen.
- **Exact pins only**: `Directory.Packages.props` and `package.json`; never
  `Version=` on a `PackageReference`, never `^` or `~`. The lockfile moves in
  the same commit.
- **A warning stops the build** (`TreatWarningsAsErrors`; IDE0055, IDE0065,
  IDE0161). No `#pragma`, and no real credentials.
- **Do not invent platform behaviour.** 401/403/409/422 pass through as the
  gateway returned them.
- **Never revert uncommitted work to clean the tree**; it belongs in the PR
  being worked on, or ask.
- **Commit messages** are semantic and present-tense, and the body argues.
  Resolve a review thread in the same act as the reply.
- **`TODO.md`** is gitignored and local, and every agent keeps it true: at
  each event re-derive a row's section from the state table in
  `docs/working-rules.md`, and move an issue from Remaining to In progress
  *before* entering its worktree.
- **Read `docs/harness-boundaries.md` by its index** before touching
  anything under `.claude/`.
