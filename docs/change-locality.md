# Change locality — the operating contract

How an agent works this repository so that a fix touches its own slice and
nothing shared, so that code is the source of trust, and so that several
agents can run at once without meeting in the same file.

This file does not replace the design spec. Where the spec and compiled code
with green tests disagree about a **fact**, the code is right. A document is
amended when a **rule** moved, never to restate a fact the code already owns.

The class → tree-set map the locality gate reads is
[`.github/locality-gate/classes.yml`](../.github/locality-gate/classes.yml).
That file is the owner of the paths; this table states the rule in words.

## 1. Source of trust, in order

A lower layer cites a higher one by name. It never copies the value.

1. **Gates and tests.** Green means the claim they cover is true: `dotnet test`
   on `BlueprintAdmin.slnx`, `npm run lint` / `npm test` / `npm run build` in
   `src/Admin.Web`, Playwright against FakePlatform, the `harness` job, the
   locality gate.
2. **Code.** `AdminOptions`, `Program.cs`, the SPA composition roots, OpenAPI
   as the platform returned it.
3. **The design spec**,
   [`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`](superpowers/specs/2026-09-14-blueprint-admin-design.md)
   — §1 before behaviour, §13 before adding any. Outranked by the code on a
   matter of fact.
4. **Git.** Commit body argues the change; the PR body is the house form.
5. **`CLAUDE.md`** and the files it points at. How to act here.

**`docs/superpowers/plans/` is a delivery record, not a specification.** Do
not edit it to match what the code became.

## 2. The rule

**Every fact has exactly one owner, and every other mention cites the owner.**
If you meet a stale restatement while doing something else, leave it.

Never write, in any document:

- a number of tests, screens or lines;
- a package version outside `Directory.Packages.props` or
  `src/Admin.Web/package.json`;
- a port, timeout or URL as a raw integer in a second place —
  `AdminOptions.cs` owns those;
- "since PR-NN" or the history of how a rule was corrected.

A measurement in a commit or PR body, as of a named date or commit, is not a
restatement.

## 3. Change classes

Name the class and the touch set before editing. A change that needs two
classes names both (`C+E`, `A+D`) and its touch set is the union.

**CI reads both rows.** `.github/workflows/locality-gate.yml` fails a PR on a
path outside the class map or the declared touch set. `/review-branch`,
`/review-copilot` and `/ship` also run `bash .claude/scripts/pr-locality.sh`.

| Class | Examples | May touch | Never touches |
|---|---|---|---|
| **A — local** | one Host area (Jobs, Broker, Trace), one SPA screen | one tree under `src/Admin.Host/**` or `src/Admin.Web/src/app/features/<name>/**`, and its tests | the other product (Host vs Web) in the same PR unless the touch set names both and the class is not A; `CLAUDE.md`; the harness |
| **B — shared mechanism** | `AdminOptions`, SSE client, identity state, `LoopbackOriginGuard` | one shared Host or `src/Admin.Web/src/app/core/**` / `shared/**` area, and the one screen spec that proves the wiring | two core areas in one PR; the frozen plan |
| **C — a rule moved** | FakePlatform becomes required for a new surface; loopback policy tightens | code and tests; the single spec section the change amends | every historical paragraph; the plan; `CLAUDE.md` |
| **D — docs or harness** | a new `/command`, this contract, a CI job | only the documents, `.claude/**`, `.github/**`, `CLAUDE.md`, and root config the touch-set row names | `src/**` — that absence is load-bearing |
| **E — dependency graph** | add a package, raise a pin | `Directory.Packages.props` with the `.csproj`; or `src/Admin.Web/package.json` with the lockfile; `global.json` / `BlueprintAdmin.slnx` when those pins move | the sections that use the package |

Notes:

- **`Program.cs` is the host composition root.** A Class A change that has to
  edit it has outgrown its class.
- **A spec beside the file it covers moves with it.**
- **`package.json` and `package-lock.json` move together.** Same for a
  `PackageReference` and `Directory.Packages.props`.
- **Nothing in `../blueprint-backend` or `../blueprint-frontend` is this
  repository's class.** Raise the fact there.

## 4. Parallel work

One worktree and one branch per agent, via `/branch`.

| Agent owns | Notes |
|---|---|
| one Host area under `src/Admin.Host/<area>/**` | never two agents on `Jobs/` or `Config/` at once |
| one SPA feature under `src/Admin.Web/src/app/features/<name>/**` | |
| `src/Admin.Web/src/app/core/**` or `shared/**` | one agent only |
| `.claude/**` | one agent only: `settings.json` self-locks |

Repo-wide mutexes (one agent at a time):

```
Program.cs                      AdminOptions.cs
src/Admin.Web/package.json      src/Admin.Web/package-lock.json
Directory.Packages.props        BlueprintAdmin.slnx
.github/workflows/ci.yml        .claude/settings.json
```

## 5. The procedure

1. Name the class and the touch set before the first edit; copy them into the
   PR body's `| Class |` and `| Touch set |` rows when `/pr` opens it.
2. Search **code** for the symbol, not the value in docs.
3. Write the test first, then the change.
4. Run `bash .claude/scripts/host-checks.sh fast` and
   `bash .claude/scripts/npm-checks.sh fast` for anything under `src/`;
   `all` on both before the PR opens. [`testing.md`](testing.md) has the rest.
5. Commit with `/commit`. Open the PR with `/pr`.
6. Stop. Do not invent platform behaviour the gateway already owns.

## 6. What still applies

- FakePlatform is first-class: a new endpoint works against the fakes and the
  Playwright smoke covers its screen.
- Exact pins, both toolchains.
- The dialect in [`style-guide.md`](style-guide.md).
- `main` stays green.
- `Closes #n` as a bare line when a change closes an issue.
- Loopback only; no credential store (spec §8).
- [`harness-boundaries.md`](harness-boundaries.md) before touching `.claude/`.

## 7. Checklist

```
[ ] Class named (A/B/C/D/E) in the PR body
[ ] Touch set listed there; nothing outside it is edited
[ ] Symbol searched in src/ and tests/, not the value in docs/
[ ] Test written first; host-checks.sh and npm-checks.sh fast green, all before the PR
[ ] Spec amended in the same PR only if a rule moved; plans/ untouched
[ ] No present-tense count, version or raw port written outside its owner
[ ] Mutex surfaces this PR needs are named
```
