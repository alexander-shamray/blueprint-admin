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

`npm-checks.sh` has no `e2e` mode. Playwright talks to the Host under
`--Admin:FakePlatform=true`. Locally: build the SPA, run the Host with that
flag, then `npm run e2e` in `src/Admin.Web`. CI's `smoke` job is the same
path. A green unit suite is not a green smoke.

FakePlatform's fixtures are recordings. To re-record them, bring the stack up,
run `dotnet run --project src/Admin.Host -- --Admin:Record=true`, and open
Stack, Broker and API. Then send some traffic and open the Stack strip and a
trace, so that the golden signals and Grafana's datasources are read. Review
the diff under `src/Admin.Host/Fakes/fixtures`, since a test or a spec that
pinned the old answer moves with it. Spec §10 says what is recorded and what
is not.

## Harness

`python .claude/scripts/shard-harness-suite.py` — Python 3.12, plus Bash,
`grep`, Git and `jq` on PATH. Python plus `jq` alone fails before a case
runs. It discovers both `.claude/scripts` and `.github/locality-gate` and
runs their classes as parallel workers; `docs/harness-boundaries.md` owns what
that may not do. The `harness` job in `ci.yml` is the matrix.

## Locality

`python -m unittest` in `.github/locality-gate/` is the gate's own suite.
CI runs it on every pull request, then enforces the **base** copy of the
gate against the PR body. Bootstrap: the first PR that lands the directory
is judged by HEAD, with a warning.
