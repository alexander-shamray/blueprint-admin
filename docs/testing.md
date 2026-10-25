# Testing

What a checkout needs that `BlueprintAdmin.slnx` and `ci.yml` cannot say.

## Host

`dotnet test` (via `bash .claude/scripts/host-checks.sh`) needs the SDK in
`global.json`. It does **not** need Docker: FakePlatform is in-process.
Container-spawning tests in the Host use `node` for long-running children
(`ProcessRunnerTests`); CI installs Node for that reason.

## Drift

`tests/Admin.Host.Tests/Drift/` reads every fact this console copies from
`blueprint-backend` against the clone beside the main checkout, resolved
the way the host resolves `Admin:BackendDir` and overridden by the same
`BLUEPRINT_Admin__BackendDir`. With no clone those tests skip; with
`ADMIN_DRIFT_REQUIRED=true` they fail instead, which is how CI's `drift`
job runs them against the backend's `main`. The `run-locally.md` checks
skip in CI regardless: that file is at the workspace root, in no
repository. `DriftCoverageTests` needs no clone and always runs. A source
file that names `blueprint-backend` without a drift check or a listed
reason fails it. A service unit or a grant the console does not have yet is
a named exception in its test, citing the issue that removes it.

## SPA

Commands run in `src/Admin.Web`. `bash .claude/scripts/npm-checks.sh` `cd`s
there. `npm ci` from the lockfile, never `npm install`. A fresh worktree has
no `node_modules`; the first check fails on a missing `ng`.

`npm run lint` also runs `npm run i18n:check`: `ng extract-i18n` into
`.i18n-check/`, then `scripts/check-translations.mjs`, which fails when
`src/locale/messages.en-US.json` is not what the templates say or a shipped
translation lacks a message. After changing a string, `npm run i18n:extract`
and translate what it adds. The `@angular-eslint/template/i18n` rule runs on
the templates `eslint.config.js` lists as translated.

CI's `web` job also runs `npm audit --audit-level=high` after `npm ci`, dev
dependencies included, so a new high or critical advisory turns the build red.
`npm-checks.sh` leaves it out because it asks the registry; run it by hand in
`src/Admin.Web` to see what CI will say. A red build here is meant to block:
Dependabot's security update is the usual way out, and for an advisory with no
patch yet the way out is an issue naming it and a change that raises the level
for as long as the issue is open, not a silent skip.

`npm-checks.sh` has no `e2e` mode. Playwright talks to the Host under
`--Admin:FakePlatform=true`. Locally: build the SPA, run the Host with that
flag, then `npm run e2e` in `src/Admin.Web`. CI's `smoke` job is the same
path. A green unit suite is not a green smoke. `playwright.config.ts` starts
a second fake host, with the assistant off, on the port `e2e/hosts.ts` names;
only `assistant-off.spec.ts` reads it.

The assistant's protocol is pinned by golden JSON files under
`src/Admin.Assistant/Protocol/golden/`: `ProtocolGoldenTests` holds the C#
records to them and `protocol.spec.ts` the TypeScript mirror, so a field
changed on one side alone fails. A new body or event is a new golden file:
the C# test fails one no C# test pins, but `protocol.spec.ts` imports each
file by name, so the new one is added there by hand.

Most of FakePlatform's fixtures are recordings; `FixtureRecordings` names
which, and why each of the rest is written by hand. To re-record, bring the stack up,
run `dotnet run --project src/Admin.Host -- --Admin:Record=true`, and open
Stack, Broker and API. Then send some traffic and open the Stack strip and a
trace, so that the golden signals and Grafana's datasources are read. Open
Mail and each message on it once an order has run, so the sink's list, every
message and its headers are read; the Mail e2e spec counts that order's
messages, so it moves with the recording. Review
the diff under `src/Admin.Host/Fakes/fixtures`, since a test or a spec that
pinned the old answer moves with it.

## Harness

`python .claude/scripts/shard-harness-suite.py` — Python 3.12, plus Bash,
`grep`, Git and `jq` on PATH. Python plus `jq` alone fails before a case
runs. It discovers `.claude/scripts`, `.github/locality-gate`,
`.github/commit-gate` and `tools/token-usage`, and runs their classes as parallel workers; `docs/harness-boundaries.md` owns what
that may not do. The `harness` job in `ci.yml` is the matrix.

## Commit gate

`python -m unittest` in `.github/commit-gate/` is the gate's own suite, and
the `harness` job runs it too. The `commit-gate` workflow runs it on every
pull request, then enforces the **base** copy of the gate. It reads git
rather than `gh`, so a branch can run it once its pull request exists:

```bash
git fetch origin main
py -3.12 .github/commit-gate/commit_gate.py --pr <n> origin/main HEAD
```

The `commits` job in `ci.yml` is its other half: it builds every commit
before the head and runs no tests.

## Locality

`python -m unittest` in `.github/locality-gate/` is the gate's own suite.
CI runs it on every pull request, then enforces the **base** copy of the
gate against the PR body. Bootstrap: the first PR that lands the directory
is judged by HEAD, with a warning.
