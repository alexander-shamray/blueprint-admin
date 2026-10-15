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
`/commit`, `/pr`, `/review-copilot` and `/review-branch`; the sweeps are
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
