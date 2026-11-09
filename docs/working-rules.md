# Working rules

What only this repository adds to the rules every target on the harness shares.
Those are `.harness/agent-rules.md`, which every session loads, and
`docs/harness/working-rules.md`, read before the act each of its sections names;
neither is restated here. `CLAUDE.md` keeps this repository's hard rules as one
line each and cites the section below that argues it. Read the section for what
you are about to touch; the file is not for reading whole.

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

The host refuses to start if `Admin:BackendDir` / `Admin:FrontendDir` are
wrong, naming the key. It does **not** run `npm ci` in the frontend clone.

## Style

C# follows [`.editorconfig`](../.editorconfig), copied from the backend so
this host reads like the code it operates. The prose version of each rule is
**blueprint-backend's** `docs/style-guide.md` — it is not in this tree.
Angular follows the sibling client: standalone, `inject()` at field level,
signals for state. [`style-guide.md`](style-guide.md) points at both
dialects, and owns what a comment says.

- **Prose wraps at 80 columns** (tables, links and code may exceed it),
  **C# at 120**, **TypeScript at the SPA formatter's width** (Prettier,
  checked by `npm run lint`; see [`style-guide.md`](style-guide.md)), in
  **British spelling** with literal dashes. **Identifiers keep their real spelling**:
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
- **Test names are sentences with underscores** (CA1707 is suppressed on
  `*Tests` projects for that reason).

## Working in this repo

- **Read before you edit**: the claim you are about to change is usually
  stated more than once. `Program.cs` is the host composition root.
- **Every intermediate commit builds**, by
  `docs/harness/working-rules.md`, *Commits*. Here the `commits` job in
  `ci.yml` builds each commit but the head, which `host`, `web` and `smoke`
  cover, so `git bisect` meets only commits that compile, and
  [the commit gate](../.github/commit-gate/README.md) refuses a range without
  the trailer.
- **Do not invent platform behaviour.** 401/403/409/422 pass through as the
  gateway returned them. A fake's answer is recorded from the live service
  where one can be, and says where it is not.
- **The task list** is `.harness/agent-rules.md`, *Working in a target*, for
  where it lives and when it changes, and `docs/harness/working-rules.md`,
  *Issues and the task list*, for where a row sits.
  `docs/harness-local.md` *Residuals* holds how a session reaches it here.

## Merging beside other agents

`docs/harness/working-rules.md` owns how pull requests land side by side:
*Pull requests* when a branch is updated, *CI* what a red `main` is owed, a
check that belongs to `main` and auto-merge. Two things only this repository
has stay here.

- **Every `ci` job also runs on the push to `main`**, `commits` included,
  which builds the commits the landing replayed onto a `main` their pull
  request was never tested against. `commit-gate.yml`, `locality-gate.yml`,
  `closure-gate.yml`, `comment-gate.yml` and `harness-drift.yml` run on pull
  requests alone, and that gap is accepted: they judge a range's trailers, a
  body's touch set and closures, its added lines and the managed files, which
  landing does not change.
- **A red `main` is an issue because of
  [`main-red.yml`](../.github/workflows/main-red.yml)**, which turns a failed
  `ci` run on `main`'s tip into one, which outlives the run and the mail, and
  closes it on the next green run. A revert runs `git-revert-pr.sh` and a flake's
  rerun `gh run rerun <run> --failed`, each by hand, as no command grants them
  (`docs/harness-boundaries.md`).

## One owner per fact

The rule is `docs/harness/working-rules.md`, *Review*, and
[`change-locality.md`](change-locality.md) §2. Here a rule stated in `CLAUDE.md`
and argued in `.editorconfig` or `Directory.Build.props` moves in both.

## Precedence where two documents disagree

The code and `README.md` beat the spec on a matter of fact.
`docs/superpowers/plans/` is a **frozen delivery record** — never edited to
match the code that followed. The design spec moves when a rule does.
`.remember/` is session state; never edit it. A code change that contradicts
the spec is not done until the spec is amended in the same PR, or the code is
changed to match — pick one, in the PR. Where the spec is genuinely wrong, say
so in the commit body and follow the code; do not silently rewrite the spec,
and do not edit `plans/` to match.

## Commands and skills

Each command's frontmatter `description` says what it is for; the list is
not restated here. The ones a session meets most are `/ship`, `/branch`,
`/commit`, `/pr` and `/review-branch`; the sweeps are
`/security-sweep` and `/bug-sweep`, and `/style-pass` applies one corrected
form corpus-wide. There is no `/validate-blueprint` or `/new-chapter`: this
repository has no blueprint chapter tree. The locality **gate** is CI, unlike
the frontend clone where it was helper-only.

Skills (always on, not slash commands): **codebase-index** before a "where is
X" question; **admin-invariants** before a new endpoint, screen or host
command.

Checks are `bash .claude/scripts/host-checks.sh` and
`bash .claude/scripts/npm-checks.sh` (the latter `cd`s into `src/Admin.Web`).
