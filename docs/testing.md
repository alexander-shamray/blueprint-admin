# Testing

What a checkout needs that `BlueprintAdmin.slnx` and `ci.yml` cannot say.

## Host

`dotnet test` (via `bash .claude/scripts/host-checks.sh`) needs the SDK in
`global.json`. It does **not** need Docker: FakePlatform is in-process.
Container-spawning tests in the Host use `node` for long-running children
(`ProcessRunnerTests`); CI installs Node for that reason.

## SPA

Commands run in `src/Admin.Web`. `bash .claude/scripts/npm-checks.sh` `cd`s
there. `npm ci` from the lockfile, never `npm install`. A fresh worktree has
no `node_modules`; the first check fails on a missing `ng`.

`npm-checks.sh` has no `e2e` mode. Playwright talks to the Host under
`--Admin:FakePlatform=true`. Locally: build the SPA, run the Host with that
flag, then `npm run e2e` in `src/Admin.Web`. CI's `smoke` job is the same
path. A green unit suite is not a green smoke.

## Harness

`python -m unittest discover -s .claude/scripts -p 'test_*.py'` — Python 3.12,
plus `jq` on the runner. The `harness` job in `ci.yml` is the matrix.

## Locality

`python -m unittest` in `.github/locality-gate/` is the gate's own suite.
CI runs it on every pull request, then enforces the **base** copy of the
gate against the PR body. Bootstrap: the first PR that lands the directory
is judged by HEAD, with a warning.
