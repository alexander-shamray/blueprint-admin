# Working rules

The rules `CLAUDE.md` used to carry in full, moved here so a session loads
them when it needs one rather than on every turn. `CLAUDE.md` keeps the
hard rules as one line each and cites the section below that argues it.
Read the section for what you are about to touch; the file is not for
reading whole.

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

One lesson stays here, because its subject is every other rule: **a gate that
silently stops covering the newest surface is the most-repeated failure in
these repositories**, and the only defence is a test whose subject is *what
the gate is looking at*, not what it found.

## Style

C# follows [`.editorconfig`](../.editorconfig), copied from the backend so
this host reads like the code it operates. The prose version of each rule is
**blueprint-backend's** `docs/style-guide.md` — it is not in this tree.
Angular follows the sibling client: standalone, `inject()` at field level,
signals for state. [`style-guide.md`](style-guide.md) points at both
dialects.

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
- **Every commit carries `PR: #<n>`, and every intermediate commit
  builds.** A pull request lands by rebase, so the trailer is the only
  record of which pull request a commit on `main` came from; `/pr` adds it
  once the number exists, `/commit` writes it after, and
  [the commit gate](../.github/commit-gate/README.md) refuses a range
  without it. The `commits` job in `ci.yml` builds each commit but the head,
  which `host`, `web` and `smoke` cover, so `git bisect` meets only commits
  that compile. Reverting a pull request is
  `bash .claude/scripts/git-revert-pr.sh <n>`.
- **A reply is not a resolution.** Resolve a review thread in the same act as
  the reply, and leave an `Ask` open on purpose.
- **Uncommitted work in the tree belongs in the PR being worked on**, in its
  own commit with a body that argues it. **Never revert it to clean the tree**;
  if it does not belong here, say so and ask rather than decide by deleting.
- **Do not invent platform behaviour.** 401/403/409/422 pass through as the
  gateway returned them. A fake's answer is recorded from the live service
  where one can be, and says where it is not.
- **The task list is `TODO.md` at the main checkout's root: gitignored,
  local, never committed.** It is for the user, and it lists open PRs
  (with the unpaused issues each closes) and every other open issue.
  **Where a row sits is a function of state, so every agent that works here
  re-derives it at each event** (work starts on an issue, a PR opens, merges
  or closes, an issue is filed, closed, reopened, paused or unpaused, work
  is abandoned) **and writes the result in the same act**:

  | State | Where |
  |---|---|
  | Open issue, nothing built for it | **Remaining**, in its wave |
  | Open issue labelled `paused` | **Paused**, whatever else is true of it |
  | Open issue a session has taken up (about to be branched, or with a branch or a worktree) and no open PR | **In progress**, with the branch once it is named |
  | Open PR | **In progress**, one row for the PR naming the open, unpaused issues it closes, or `—` when there are none; an issue so named has no row of its own |
  | Closed issue; merged or closed PR | no row, and no note of what landed, which git and the closed issues already keep |

  Three things follow. Work that stops without landing (a claim abandoned,
  a PR closed unmerged, or one merged without closing its issue) leaves the
  issue open and ends its claim, so its row is re-derived by the table:
  **In progress** with the branch if one remains, otherwise back to its wave.
  Whatever the work found and filed has its own row by the same table, made
  in the main checkout or owed as the paragraph below says. And the move from
  **Remaining** to **In progress** is made before the session enters the
  worktree.

  Only the main checkout's copy counts, never one in a worktree — and a
  session inside `.claude/worktrees/` is refused that copy by Claude Code's
  own isolation. So the move in is made in the main checkout before
  `EnterWorktree` and the re-derivation after `ExitWorktree` (which removes
  the row when the issue has closed and otherwise moves it as the paragraph
  above says); every event that happens inside the worktree (a PR opens, a
  filing, work stops) is carried in the report as owed and made once the
  session is back. The file's own note, topmost, holds the section and
  column rules. When in doubt, rebuild it from `gh pr list --limit 1000` and
  `gh issue list --limit 1000`, because the default of 30 truncates
  silently.

## Merging beside other agents

Several sessions keep pull requests open at once, and `protect-main`, the
repository's ruleset, owns how they land: rebase only, with no rule that a
branch be up to date first. So no pull request is tested against the `main`
it lands on, and the run of `ci` on the push to `main` is the first to see
two pull requests together.

- **Do not update an open pull request because another one merged.** No
  rebase, and no *Update branch* button. A branch is updated only when
  GitHub reports a conflict, or when its checks went red with a red `main`
  that has since turned green (the last bullet), and the update is
  `bash .claude/scripts/git-rebase-onto-main.sh <branch> start`, which
  fetches, replays onto `origin/main` and publishes under a lease. A rebase
  landing refused is `/ship` step 6's own update, with its exit codes;
  `mergeable` reading `CONFLICTING` stops `/ship`, and the update is the
  caller's, run by hand before `/ship` resumes. The raw spelling's push,
  `git push --force-with-lease`, is denied in `.claude/settings.json`,
  because a lease cannot check what the helper's guards do.
- **Never force-push a branch another session owns**, even through the
  helper. Its pull request is that session's, and so is the conflict.
- **Each clone sets `git config rerere.enabled true` once.** Worktrees share
  the clone's config, so a resolution recorded in one is replayed in the
  next. It does not stage what it replays, so the helper's `continue` still
  follows a `git add`.
- **No pull request is set to auto-merge** while `protect-main` requires no
  status check: with nothing to wait for, auto-merge lands at once, before
  `ci` has run. `/ship` waits on the checks and lands with
  `gh-pr-merge.sh` against the head it read, and a merge by hand does the
  same.
- **Every `ci` job also runs on the push to `main`**, `commits` included,
  which builds the commits the landing replayed onto a `main` their pull
  request was never tested against. Only `commit-gate.yml` and
  `locality-gate.yml` run on pull requests alone, and that gap is accepted:
  they judge a range's trailers and a body's touch set, which landing does
  not change.
- **A red `main` is reverted first and fixed forward after.**
  [`main-red.yml`](../.github/workflows/main-red.yml) turns a failed `ci`
  run on `main`'s tip into an issue, which outlives the run and the mail,
  and closes it on the next green run. **That open issue is the signal, not
  a pull request's own checks**: a `pull_request` run is pinned to the
  merge it started on and never reruns when `main` moves, so a pull request
  last pushed before `main` went red still reads green. So an open
  `main is red` issue stops a landing as a red check would; `/ship` step 6
  does not read it yet (#531). The pull request that turned it red is
  reverted with `bash .claude/scripts/git-revert-pr.sh <n>`, run by hand,
  since no command grants it (`docs/harness-boundaries.md` says so), and
  the revert lands as its own pull request; the fix lands after it. A flake
  is the exception, a comment on the flaky tracker by the user-level rule.
  A check that is red on a pull request and on `main` belongs to `main`, so
  a pull request that touched none of it waits for that issue to close and
  is not changed to make it pass. Once the issue closes, a rerun would still
  read the old merge with the red `main`, so that pull request is updated
  through `git-rebase-onto-main.sh`, the one update no conflict asked for.

## One owner per fact

Every fact has exactly one owner, and every other mention cites the owner by
symbol or file, never by value. `CLAUDE.md` is inside the rule too, and **no
gate reads it**: a rule stated there and argued in `.editorconfig` or
`Directory.Build.props` moves in both.

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

[`harness-boundaries.md`](harness-boundaries.md) is the inventory of what the
harness grants. **Read its index first, then only the section you need,
before touching anything under `.claude/`.** Checks are
`bash .claude/scripts/host-checks.sh` and
`bash .claude/scripts/npm-checks.sh` (the latter `cd`s into `src/Admin.Web`).
