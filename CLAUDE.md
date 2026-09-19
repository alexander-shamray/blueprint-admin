# CLAUDE.md

Guidance for Claude Code when working in this repository. This file is a
primer: what the repo is, where things are, and how to act here. Anything that
moves when a PR lands — what has been built, a count of anything, a port — is
owned elsewhere and cited from here by name, never restated.
**Read the file that covers what you are about to touch, before you touch it.**

| | |
|---|---|
| [`README.md`](README.md) | How to run it, what the screens do today, known limits |
| [`docs/todo.md`](docs/todo.md) | Tasks in progress and remaining; every task change edits it |
| [`docs/change-locality.md`](docs/change-locality.md) | The operating contract: trust order, classes, touch sets; the gate reads `.github/locality-gate/classes.yml` |
| [`docs/harness-boundaries.md`](docs/harness-boundaries.md) | What the harness grants these commands, and refuses |
| [`docs/style-guide.md`](docs/style-guide.md) | Pointers to the two dialects; `/style-pass` records a newly settled form here |
| [`docs/testing.md`](docs/testing.md) | Host vs SPA vs smoke vs harness; FakePlatform for e2e |
| [`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`](docs/superpowers/specs/2026-09-14-blueprint-admin-design.md) | The design. §1 before changing behaviour; §13 before adding any |
| [`docs/superpowers/plans/`](docs/superpowers/plans/) | Delivery record, phases 0–5 — complete; cited for a phase's title, never as current truth |
| [`Directory.Build.props`](Directory.Build.props) | Target, artefacts path, ADR-019 analysis (the policy lives in blueprint-backend) |
| [`.editorconfig`](.editorconfig) | C# dialect this host shares with the backend |
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

Two projects, mirroring the siblings:

```
src/Admin.Host/            .NET 10 minimal API: Compose, npm as children, HTTP
                           proxy, SSE. Composition root: Program.cs
src/Admin.Web/             Angular 22 standalone SPA, built into Host/wwwroot
tests/Admin.Host.Tests/    xunit
src/Admin.Web/e2e/         Playwright (FakePlatform)
.github/workflows/         host, web, smoke
```

**Precedence where two documents disagree**: the code and `README.md` beat the
spec on a matter of fact. `docs/superpowers/plans/` is a **frozen delivery
record** — never edited to match the code that followed. The design spec
moves when a rule does. `.remember/` is session state; never edit it.

### Owners of facts you will be tempted to restate

| Fact | Owner |
|---|---|
| Ports, sibling dirs, FakePlatform | `AdminOptions.cs` |
| SPA dev server and `/api` proxy | `src/Admin.Web/angular.json`, `proxy.conf.json` |
| Compose service list | backend `deploy/compose/` |
| Fake recordings | `src/Admin.Host/Fakes/` |
| Gateway routes (copied, cited) | `src/Admin.Host/Api/GatewayRoutes.cs` |
| Example request bodies | `src/Admin.Host/Api/RunLocallyExamples.cs` — change when `run-locally.md` does |
| Broker service / projection queue | `src/Admin.Host/Broker/BrokerService.cs` |
| Drain watch after publish | `PUBLISH_OPERATION` in `src/Admin.Web/src/app/features/api/api-page.ts` |
| Grafana / Loki / Explore | `GrafanaClient.cs`, `ExploreLink.cs` |
| Golden-signal queries (copied, cited) | `GoldenSignals.cs` |
| What a Tempo span is | `SpanRecogniser.Table` in `SpanRecogniser.cs` |
| OS (tree kill, job object, `npm.cmd`) | `ProcessRunner.cs`, `WindowsJobObject.cs`, `ExecutableResolver.cs` |

## The one rule that matters

**Every fact has exactly one owner, and every other mention cites the owner**
— by symbol or file, never by value. A stale restatement met in passing is
left; it is not this change. A code change that contradicts the spec is not
done until the spec is amended in the same PR, or the code is changed to
match — pick one, in the PR. Where the spec is genuinely wrong, say so in
the commit body and follow the code; do not silently rewrite the spec, and
do not edit `plans/` to match.

This file is inside the rule too, and **no gate reads it**: a rule stated
here and argued in `.editorconfig` or `Directory.Build.props` moves in both.

## The commands

From the repository root unless noted. `README.md` owns the long form.

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

Three things hold first: **`npm ci`, never `npm install`**, because `install`
may rewrite the lockfile; **a `.cs` written with LF fails IDE0055 on every
line** — `.gitattributes` checks C# out CRLF; and **FakePlatform is
first-class**: a new endpoint is not done until it works against the fakes
and the Playwright smoke covers its screen.

The host refuses to start if `Admin:BackendDir` / `Admin:FrontendDir` are
wrong, naming the key. It does **not** run `npm ci` in the frontend clone.

## Build policy

.NET SDK is `global.json` with `rollForward: disable`. Package versions live
in `Directory.Packages.props` with **exact** pins — never add `Version=` on a
`PackageReference`. `Directory.Build.props` carries the backend's **ADR-019**
policy: `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`. Three
`.editorconfig` rules are at `warning` — **IDE0055**, **IDE0065**,
**IDE0161** — and a warning stops the build. `#pragma` is not the way out.

SPA versions live in `src/Admin.Web/package.json` with **exact** pins — no
`^`, no `~` — and `package-lock.json` moves in the same commit. `.nvmrc`
pins Node.

One lesson stays here, because its subject is every other rule: **a gate that
silently stops covering the newest surface is the most-repeated failure in
these repositories**, and the only defence is a test whose subject is *what
the gate is looking at*, not what it found.

## Style

C# follows [`.editorconfig`](.editorconfig), copied from the backend so this
host reads like the code it operates. The prose version of each rule is
**blueprint-backend's** `docs/style-guide.md` — it is not in this tree.
Angular follows the sibling client: standalone, `inject()` at field level,
signals for state. These stay here because they have to be true before anyone
opens those files:

- **Prose wraps at 80 columns** (tables, links and code may exceed it),
  **C# at 120**, **TypeScript at the SPA formatter's width**, in **British
  spelling** with literal dashes. **Identifiers keep their real spelling**:
  `IHttpClientFactory`, never "corrected" or Americanised.
- **File-scoped namespaces**, blank line after. **Explicit types for locals**,
  with the backend's `var` carve-outs only.
- **A single statement may omit braces; two or more always take them, and so
  does one that wraps.** **One space before `=`, `=>` and `{`, never a column
  of them** — IDE0055 makes a padded column a failed build.
- **`inject()` at field level**, never a constructor parameter. **Standalone
  components** with an explicit `imports` array.
- **No `#pragma` suppressions and no real credentials.** Spec §8's loopback
  and the realm passwords in `AdminOptions` are the stated local exception.
- **A comment says why and cites the owner** — no history, no inventory, no
  PR named. A finding against a comment is closed by cutting it.
- **Test names are sentences with underscores** (CA1707 is suppressed on
  `*Tests` projects for that reason).

## Working in this repo

- **Read before you edit**: the claim you are about to change is usually
  stated more than once. `Program.cs` is the host composition root.
- **Commit messages** are semantic and present-tense — `docs:`,
  `feat(<scope>):`, `fix:`, `chore:`, `refactor:`, `test:` — and the body
  argues the change. A `Closes #n` in a commit body fires on merge whatever
  the description says, so the description is reconciled to the commits and
  never the reverse.
- **A reply is not a resolution.** Resolve a review thread in the same act as
  the reply, and leave an `Ask` open on purpose.
- **Uncommitted work in the tree belongs in the PR being worked on**, in its
  own commit with a body that argues it. **Never revert it to clean the tree**;
  if it does not belong here, say so and ask rather than decide by deleting.
- **Do not invent platform behaviour.** 401/403/409/422 pass through as the
  gateway returned them. Inventory 502 is expected until that service exists.
- **[`docs/todo.md`](docs/todo.md) moves with every task change**, in the
  same PR. A change that starts, finishes or discovers a task, or opens or
  closes a PR or issue, edits its row there. Before a PR merges, re-read
  `gh pr list` and `gh issue list` and reconcile both sections, removing
  the PR's own row and the rows of the issues it closes — those issues are
  still open until the merge closes them, so `gh issue list` will not drop
  them for you. The file is `main`'s view, so each merge is the moment it is
  brought up to date.

## Available commands

| | |
|---|---|
| `/ship` | Clean `main` → `/branch` → checks → `/commit` → `/pr` → the Copilot review loop (Grok is disabled) → merge → teardown. **It stops for nothing that is a judgement** |
| `/branch` | A correctly named branch **in a sibling worktree** the session moves into; in place when the tree is dirty or the parent is not writable |
| `/commit` | Split the working tree into semantic commits with arguing bodies |
| `/pr` | Open a PR in the house body form, with `| Class |` and `| Touch set |` |
| `/review-grok` | Triage an external review into a resolution record |
| `/review-copilot` | Triage Copilot's PR comments — verify each before acting |
| `/review-branch` | Review the branch against `main` for contradictions; writes `suggestions.md` |
| `/style-pass` | Apply one corrected form corpus-wide, then record it in the guide and the linters |
| `/security-sweep` | Loop a defensive security audit in a throwaway worktree, filing an issue per confirmed medium-or-above finding |
| `/bug-sweep` | The same loop aimed at defects, filed at **critical or high** |

There is no `/validate-blueprint` or `/new-chapter`: this repository has no
blueprint chapter tree. The locality **gate** is CI, unlike the frontend
clone where it was helper-only.

Skills (always on, not slash commands): **codebase-index** before a "where is
X" question; **admin-invariants** before a new endpoint, screen or host
command.

### What cuts across them

[`docs/harness-boundaries.md`](docs/harness-boundaries.md) is the inventory.
**Read it before touching anything under `.claude/`**. Checks are
`bash .claude/scripts/host-checks.sh` and
`bash .claude/scripts/npm-checks.sh` (the latter `cd`s into `src/Admin.Web`).
