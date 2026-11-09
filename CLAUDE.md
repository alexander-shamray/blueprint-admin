# CLAUDE.md

A locator and the hard rules. Anything that moves when a PR lands — what has
been built, a count of anything, a port — is owned elsewhere and cited by
name, never restated. **Read the file that covers what you are about to
touch, before you touch it**, and read a long one by its index or a grep, not
whole.

| | |
|---|---|
| [`README.md`](README.md) | How to run it, what the screens do today, known limits |
| `.todo/blueprint-admin.md` in the main checkout's parent directory (local) | The task list, for the user; `.harness/agent-rules.md` says when it changes |
| [`docs/working-rules.md`](docs/working-rules.md) | Build policy, style, what only this repository adds to the shared rules, precedence, commands |
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: trust order, classes, touch sets; the gate reads `.github/locality-gate/classes.yml` |
| [`.harness/agent-rules.md`](.harness/agent-rules.md) | The rules every repository on the harness shares, imported below; managed |
| [`docs/harness/working-rules.md`](docs/harness/working-rules.md) | Their detail for commits, pull requests, review, CI, tests and issues, and the review bar; managed |
| [`docs/harness/token-rules.md`](docs/harness/token-rules.md) | The reasons behind the subagent, review-round and plan rules; managed |
| [`docs/harness/pitfalls.md`](docs/harness/pitfalls.md) | Shell, git, machine and auto-mode traps; managed |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses; managed; start at its index |
| [`docs/harness-local.md`](docs/harness-local.md) | What the managed commands name by role, filled in for this repository: its documents, *Ship checks*, residuals and review examples |
| [`docs/commands/`](docs/commands/) | The argument for each long command in `.claude/commands/`, whose runbook cites it by heading; managed, but for `review-branch.md`, this repository's own as its command is; read only when a command is disputed or edited |
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
src/Admin.Mcp/             the MCP route: stdio to an agent, HTTP to the host's /api
src/Admin.Web/             Angular 22 standalone SPA, built into Host/wwwroot
tests/Admin.Host.Tests/    xunit
src/Admin.Web/e2e/         Playwright (FakePlatform)
tools/token-usage/         Transcript report for the token plan
.github/workflows/         host, web, smoke
```

### Owners of facts you will be tempted to restate

| Fact | Owner |
|---|---|
| A rule every repository on the harness shares | `.harness/agent-rules.md`, and `docs/harness/` for its detail |
| Which files are managed | `.harness/manifest.json`; `.harness/config.json`'s `local` list names what stays here |
| Ports, sibling dirs, FakePlatform | `AdminOptions.cs` |
| Which services the platform has, and what hangs from each | `PlatformServices.All` in `src/Admin.Host/Config/PlatformServices.cs` |
| SPA dev server and `/api` proxy | `src/Admin.Web/angular.json`, `proxy.conf.json` |
| Compose service list | backend `deploy/compose/` |
| Fake recordings | `src/Admin.Host/Fakes/` |
| Gateway routes (copied, cited) | `src/Admin.Host/Api/GatewayRoutes.cs` |
| Example request bodies | `src/Admin.Host/Api/RunLocallyExamples.cs` — change when `run-locally.md` does |
| Broker service | `src/Admin.Host/Broker/BrokerService.cs` |
| Platform queues, projection queue (copied, cited) | `PlatformQueues.Table` in `src/Admin.Host/Broker/PlatformQueues.cs` |
| Drain watch after publish | `PUBLISH_OPERATION` in `src/Admin.Web/src/app/features/api/api-client.ts` |
| Grafana / Loki / Explore | `GrafanaClient.cs`, `ExploreLink.cs` |
| Golden-signal queries (copied, cited) | `GoldenSignals.cs` |
| What a Tempo span is | `SpanRecogniser.Table` in `SpanRecogniser.cs` |
| OS (tree kill, job object, `npm.cmd`) | `ProcessRunner.cs`, `WindowsJobObject.cs`, `ExecutableResolver.cs` |
| The assistant's core, its protocol and its Anthropic adapter | blueprint-backend's `Common.Assistant` and `Common.Assistant.Anthropic` packages (`src/BuildingBlocks/` there), at the versions `Directory.Packages.props` pins; restored through `nuget.config` |
| The assistant's protocol, mirrored (copied, cited) | `src/Admin.Web/src/app/assistant/protocol.ts`, with the owner's golden files copied to `golden/` beside it |
| Assistant on or off, and the `Admin:Assistant` keys | `AssistantOptions` in `src/Admin.Host/Assistant/AssistantOptions.cs` |
| The assistant's limits and their defaults | `AssistantLimits` in the `Common.Assistant` package |
| What the Anthropic provider sends, the default model and effort, and how its stream is read | `RequestBuilder`, `AnthropicSettings` and `StreamMapper` in the `Common.Assistant.Anthropic` package |
| The MCP route's server-tool calls outside a conversation | `ServerToolCalls` in `src/Admin.Host/Assistant/ServerToolCalls.cs` |
| Where the provider's credential comes from | the SDK's own resolution, built in `AnthropicProviderFactory` (`src/Admin.Host/Assistant/`); no key holds one |
| What each screen is for, as the assistant explains it | `help` in `BUILT_IN_PAGES` (`src/Admin.Web/src/app/workspace/built-in-pages.ts`) |
| Which console reads the assistant may call, and the endpoint each mirrors | `AdminReadTools` in `src/Admin.Host/Assistant/AdminReadTools.cs` |
| The MCP route's tool endpoints, and what it speaks to an agent | `AssistantEndpoints` (`/api/assistant/tools`) and `McpServer` in `src/Admin.Mcp/`; the catalogue it serves is `src/Admin.Web/public/assistant-catalogue.json`, held to `catalogue()` by its spec |
| The fake assistant's answers | `FakeAssistantScripts` in `src/Admin.Host/Fakes/` |
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

## Hard rules

Each carries its reason, or is argued in
[`docs/working-rules.md`](docs/working-rules.md) or
[`docs/change-locality.md`](docs/change-locality.md) §6. The harness half
(uncommitted work, branch updates, a red `main`, commit messages, review
threads, the task list, `.claude/`) is `.harness/agent-rules.md`'s, imported
below, and `docs/harness/working-rules.md`'s.

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

## The harness

Every path `.harness/manifest.json` lists is managed: `blueprint-harness` owns
it, synced at the commit the manifest names, and a sync pull request brings it
here. A residual only this repository has goes in `docs/harness-local.md`
*Residuals*, which also says what a bare `#NN` in a managed file names, and a
working rule only this repository has in `docs/working-rules.md` or
`docs/change-locality.md` §6.

@.harness/agent-rules.md
