# Harness-local: blueprint-admin

What the payload's commands name by role, filled in for this repository.
The four headings are fixed by the payload's `docs/harness-boundaries.md`,
*Managed files*; this file is the repository's own and no sync writes it.

## Documents

| Role | Path |
|---|---|
| Change contract | `docs/change-locality.md` |
| Style guide | `docs/style-guide.md` |
| Testing document | `docs/testing.md` |
| Product specification | `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` |
| Roadmap | none |
| Closed records | `docs/superpowers/plans/` |
| Measurements | `docs/token-usage.md`, `docs/token-quality-sets.md` |

The change contract's class map is `.github/locality-gate/classes.yml`, which
its §3 cites. The plans are the frozen delivery record of phases 0–5; a branch,
commit or pull request for a task from one takes its `## Task n:` heading.

A branch's type falls out of what it touched: `docs/**` alone is `docs/`;
`.editorconfig`, `CLAUDE.md`, `AGENTS.md`, `.claude/**`, `.harness/**`,
`tools/**`, CI, `.mcp.json`, `.codeindexignore`, `src/Admin.Web/package.json`,
`src/Admin.Web/eslint.config.js` and `src/Admin.Web/.prettierrc` are `chore/`;
`src/Admin.Host/**`, `src/Admin.Mcp/**`, `src/Admin.Web/**` and `tests/**` are
`feat(<scope>)/`, `fix/` or `refactor/`, as the diff decides.

## Ship checks

Selected by what the diff touched:

- **`bash .claude/scripts/host-checks.sh all`** whenever the change reaches
  `src/Admin.Host/**`, `src/Admin.Mcp/**`, `tests/**`, `*.csproj`,
  `Directory.*.props` or `BlueprintAdmin.slnx`.
- **`bash .claude/scripts/npm-checks.sh all`** whenever it reaches
  `src/Admin.Web/**` or that project's lockfile, eslint or prettier config.
  **A new worktree has no `node_modules`**: run
  `bash .claude/scripts/spa-ci.sh` once first, which is `npm ci`, and again when
  `package.json` or the lockfile is dirty.
- **`bash .claude/scripts/harness-checks.sh`** whenever it reaches `.claude/**`,
  `.harness/**`, `.github/locality-gate/**`, `.github/commit-gate/**`,
  `.github/pin-gate/**`, `.github/issue-form-gate/**` or `tools/**`. It runs
  what the change reaches of the payload's suite, through
  `shard-harness-suite.py --selective`, then those roots; CI runs it whole.
- A docs-only change runs `fast` or nothing and says which.
- **The Playwright suite is CI's `smoke` job and is not run here**: it needs the
  host under FakePlatform and a browser, and a suite that passed because nothing
  was listening would be worse than none. The PR body says plainly it was not
  run.

A gate that reports a pass over nothing is ranked critical by `/bug-sweep`. Here
that means the locality gate, the pin gate, the `harness` and `gate-suites`
jobs, `dotnet test`, `npm test` and `smoke`.

The checks `/ship` step 6's wait requires: `closure-gate`, `commit-gate`,
`commits`, `drift`, `harness (macos-latest)`, `harness (ubuntu-latest)`,
`harness (windows-latest)`, `harness-drift`, `host (ubuntu-latest)`,
`host (windows-latest)`, `locality-gate`, `smoke` and `web`.
`rulesets/main.json` in `blueprint-harness` owns what the `main` ruleset
requires. A check is added here first, so the wait is not short of the ruleset;
no gate compares the two.

## Residuals

What the managed files do not state because only this repository's tree or
toolchain makes it true. The harness's `IMPORT.md`, *Admin audit, pinned at
c5ec185c*, names each one's source line in this repository's copy before the
first sync.

**A bare `#NN` under `.claude/`, `docs/harness/` or `docs/commands/`, or in
`docs/harness-boundaries.md`, is not an issue here.** The machinery was built
in `alexander-shamray/blueprint-backend` and the harness, and their arguments
are kept verbatim, so chase such a number there. A citation in a file this
repository owns means this repository, `docs/commands/review-branch.md`
among them.

**The code index's search limit is 5 here.** The managed skill and hint say
`--limit 3`, backend's measurement; on this corpus 3 loses 2 of 8, measured at
5, and a managed file has no seam for it. A search here carries `--limit 5`,
which the grant admits.

**Code a session writes runs unjudged through a granted check helper, and only
the trust boundary stands** (#310). `/ship` can write `src/**`, `tests/**` and
`tools/**` beside `host-checks.sh`, `npm-checks.sh` and `harness-checks.sh`,
which run what the tree holds, so a written test, spec or
`tools/token-usage/test_*.py` module executes as host code no hook judges. That
is the job, so the residual is stated and not closed: the unattended chain's
only input is the branch it works on, the review reads the diff before the
merge, and `/ship` step 6 merges only once every check on the pushed head has
passed. `main`'s ruleset (id 23805274) carries the required checks, so a merge
outside `/ship` waits on them too (#530). A required check is matched by its
job's name, so it holds a red or missing job and not one a pull request rewrote
under the same name: that is the review's to read.

**A project file stays editable, and the pin gate is what holds it** (#542,
#547). `*.csproj` is MSBuild that `host-checks.sh` runs, the same class of input
as `src/**`, and a supply-chain input the denied `Directory.Packages.props` does
not close: it can name its own version with `VersionOverride`, opt out of
central management or add a feed. `.github/pin-gate/` refuses each in CI. A
session can edit that gate too, since `.github/**` is not denied here, a deny on
it being a new stop on ordinary CI work: CI's read of the head and the review
hold it. `/style-pass` used to deny `*.csproj` in its frontmatter; a managed
command has no per-target line and a settings deny would also stop `/ship`, so
that is lost.

**The files MSBuild and NuGet load without being asked are denied by name**
(#311). `Directory.Build.targets` and `.rsp`, `Directory.Solution.props` and
`.targets`, `NuGet.Config` in each casing and the solution's
`before.`/`after.`/plain `.slnx.targets` are untracked, so no listing of tracked
files reaches them, yet an `Exec` in one runs under the granted `host-checks.sh`
and the build reports success. The root `nuget.config`, which names the GitHub
Packages feed the assistant's core restores from (#435), may be tracked and
stays denied like the rest, so a change to it is the owner's.
`.harness/config.json` denies them at the root in both spellings and by family
at any depth, with `MSBuild.rsp`, any `global.json`, `Directory.Packages.*`,
`*.csproj.user` and any `obj` tree, where a restore that finds nothing changed
does not rewrite a planted `*.nuget.g.targets`. Not covered: a
`Directory.Build.props` above the checkout, which the edit guard's
outside-the-checkout rule judges.

**The SPA toolchain is editable, and the granted runners execute it.** This
repository's `/ship` and `/pr` denied `package.json`, `angular.json`, `tsconfig`,
the eslint and prettier configs and `**/*.config.*` in their frontmatter, and
`/style-pass` a narrower set, without the eslint and prettier files it edits. The
payload's commands carry none of it, and `npm-checks.sh` and `spa-ci.sh` are
settings allows that hold every turn. A session can therefore edit a script or a config and run it with no
prompt, as it already can with a spec through `npm test`. No settings deny
replaces them, since 25 edits in three weeks were ordinary Class E and tooling
work; review and CI stand. Only `.npmrc`, which no session edited and which
moves the registry `spa-ci.sh` installs from, is denied. `/style-pass` edits
`eslint.config.js` and runs the linter it configures, which is the pass's job.
Only the local `/review-branch` still denies the executed configs, and
`test_the_auto_load_surface_is_denied` holds that list.

**The auto-mode classifier has refused `/ship`'s merge, and no grant in this
tree answers it** (#613). Under a user-level `"defaultMode": "auto"`,
`gh-pr-merge.sh` was refused as "Merge Without Review" with every step 6 gate
read in the same turn: it refused #611, #612 and #614 and let #609 through. The
owner's `autoMode.environment` already trusted this repository's `origin`
throughout, so trust was not the remedy; the owner added an `autoMode.allow`
entry on 2026-11-01, #615 then landed unrefused and #613 closed. One landing is
not a proof, so a new refusal stays `/ship`'s stop with the owner's line, and
reopens #613.

**The managed `pr-locality.sh` reads class `A+D+E`, and the gate here refuses
it.** `/pr` admits the one three-member class backend's contract argues; this
repository's `docs/change-locality.md` section 3 admits none, so
`.github/locality-gate/locality_gate.py` refuses the cell. A body naming
`A+D+E` gets a verdict from the helper and goes red in CI; the class is the
fault, and two classes, or two pull requests, are the answer.

**The gate skips rows inside a fenced code block, and the managed
`pr-locality.sh` does not.** `_unfenced` in `.github/locality-gate/locality_gate.py`
reads a fence as CommonMark does; the helper counts every line. A body with a
fenced example row beside the table is refused by the helper and judged by the
gate, and one whose only rows are fenced gets a verdict from the helper and a
refusal in CI. Until blueprint-harness#498 brings the helper level, a fenced
example writes its rows with a leading space, which neither reader takes.

**A session writes the task list as `.harness/agent-rules.md` says, by no route
of this repository's own.** The file sits outside every checkout;
`.harness/config.json` `task_list` names it, and a `dispatch_board.py tasks`
call with no run finds it from the main checkout's copy, from a worktree as
well (`task_list()`); the forms that take a run read the path passed in
`--tasks`. `docs/harness/task-list.md`, *The events and their calls*, says which
forms are which. *Working in a target* in
`.harness/agent-rules.md` says which writes go through those calls. The main
checkout's root may hold a gitignored `TODO.md` link for the user's editor; the
edit guard refuses a write through it.

**A cheaper reviewer or model answers to this repository's recall.** The managed
`docs/harness/token-rules.md` waits on a target's recall sets; here they are
`docs/token-quality-sets.md`. To claim a reviewer or a prompt change, check out
a row's merge base so the defect is present, run the candidate, count a row
found when a finding names its file and the same fault, and hold the count at no
fewer than the baseline that file records. A cut that loses a finding is not a
saving (`docs/token-plan.md`, §3).

**The lockfile leaves the sweep's helper.** This repository's own
`sweep-slices.sh` counted `src/Admin.Web/package-lock.json` as generated and
read it mechanically. The payload's helper reads no lockfile, so
`.harness/sweep-rows.tsv` gives it a `samples` row, which counts a file with no
fence unread, and `TheLockfile` in `.claude/scripts/test_sweep_slices_admin.py`
holds the two promises instead: every `resolved` on the npm registry over https,
and a sha512 on every entry that is not a link, bundled or the root. A sweep run
no longer refuses on a fault; the `harness` job does.

## Review examples

The kinds of finding this repository's surface produces, which
`docs/harness/working-rules.md`, *What counts as a finding*, and the
`branch-reviewer` profile read here. They are the first four of the list the
local `.claude/commands/review-branch.md` keeps in its own *What counts as a
finding*, whose fifth and sixth the working rules already state:

1. **Document ↔ code drift**: samples, pins, type names, provider order,
   endpoints, ports, credentials. The design spec and `README.md` make checkable
   claims; `AdminOptions.cs` owns ports and sibling paths.
2. **Cross-document / `CLAUDE.md` contradictions**: two rules that cannot both
   be true, or a planned tree stated as present. A stale restatement is a finding
   only if this branch **introduces or edits** it (`docs/change-locality.md` §2).
3. **Pin drift**: `Directory.Packages.props` vs a `PackageReference`;
   `src/Admin.Web/package.json` vs its lockfile; `global.json` vs the SDK the
   host builds with.
4. **CI drift**: `.github/workflows/ci.yml` against `docs/testing.md`.
