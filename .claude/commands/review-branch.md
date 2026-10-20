---
description: Review branch vs main for contradictions; recheck suggestions.md when it already exists
argument-hint: "[recheck | full | --local]"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(git diff:*), Bash(git log:*), Bash(git status:*), Bash(git merge-base:*), Bash(codebase-index diff-impact:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(bash .claude/scripts/npm-checks.sh:*), Bash(bash .claude/scripts/host-checks.sh:*), Bash(bash .claude/scripts/harness-checks.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(bash .claude/scripts/pr-locality.sh:*), Bash(rm suggestions.md)
disallowed-tools: Edit(.git/**), Edit(./.git/**), Edit(.git), Edit(./.git), Edit(.claude/**), Edit(./.claude/**), Edit(.remember/**), Edit(./.remember/**), Edit(.github/**), Edit(./.github/**), Edit(.vscode/**), Edit(./.vscode/**), Edit(android/**), Edit(./android/**), Edit(docs/**), Edit(./docs/**), Edit(e2e/**), Edit(./e2e/**), Edit(ios/**), Edit(./ios/**), Edit(public/**), Edit(./public/**), Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(tools/**), Edit(./tools/**), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(.nvmrc), Edit(./.nvmrc), Edit(.prettierrc), Edit(./.prettierrc), Edit(AGENTS.md), Edit(./AGENTS.md), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(README.md), Edit(./README.md), Edit(angular.json), Edit(./angular.json), Edit(capacitor.config.ts), Edit(./capacitor.config.ts), Edit(eslint.config.js), Edit(./eslint.config.js), Edit(ionic.config.json), Edit(./ionic.config.json), Edit(package-lock.json), Edit(./package-lock.json), Edit(package.json), Edit(./package.json), Edit(playwright.config.ts), Edit(./playwright.config.ts), Edit(tsconfig.app.json), Edit(./tsconfig.app.json), Edit(tsconfig.json), Edit(./tsconfig.json), Edit(tsconfig.spec.json), Edit(./tsconfig.spec.json), Edit(.npmrc), Edit(./.npmrc), Edit(npm-shrinkwrap.json), Edit(./npm-shrinkwrap.json), Edit(vitest.config.ts), Edit(./vitest.config.ts), Edit(vitest.config.js), Edit(./vitest.config.js), Edit(vitest.config.mts), Edit(./vitest.config.mts), Edit(vite.config.ts), Edit(./vite.config.ts), Edit(vite.config.js), Edit(./vite.config.js), Edit(vite.config.mts), Edit(./vite.config.mts), Edit(eslint.config.mjs), Edit(./eslint.config.mjs), Edit(eslint.config.cjs), Edit(./eslint.config.cjs), Edit(prettier.config.js), Edit(./prettier.config.js), Edit(prettier.config.cjs), Edit(./prettier.config.cjs), Edit(prettier.config.mjs), Edit(./prettier.config.mjs), Edit(.prettierrc.js), Edit(./.prettierrc.js), Edit(.prettierrc.cjs), Edit(./.prettierrc.cjs), Edit(.prettierrc.json), Edit(./.prettierrc.json), Edit(karma.conf.js), Edit(./karma.conf.js), Edit(jest.config.js), Edit(./jest.config.js), Edit(**/*.config.js), Edit(**/*.config.cjs), Edit(**/*.config.mjs), Edit(**/*.config.ts), Edit(**/*.config.mts), Edit(**/package.json), Edit(**/.npmrc), Edit(**/tsconfig*.json), Edit(**/.prettierrc*), Bash(git push:*), Edit(node_modules/**), Edit(./node_modules/**), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(global.json), Edit(./global.json), Edit(BlueprintAdmin.slnx), Edit(./BlueprintAdmin.slnx), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore), Edit(~/.claude/projects/**), Edit(~/.claude/output-styles/**), Edit(Directory.Build.targets), Edit(./Directory.Build.targets), Edit(Directory.Build.rsp), Edit(./Directory.Build.rsp), Edit(Directory.Solution.props), Edit(./Directory.Solution.props), Edit(Directory.Solution.targets), Edit(./Directory.Solution.targets), Edit(NuGet.Config), Edit(./NuGet.Config), Edit(nuget.config), Edit(./nuget.config), Edit(NuGet.config), Edit(./NuGet.config), Edit(BlueprintAdmin.slnx.targets), Edit(./BlueprintAdmin.slnx.targets), Edit(before.BlueprintAdmin.slnx.targets), Edit(./before.BlueprintAdmin.slnx.targets), Edit(after.BlueprintAdmin.slnx.targets), Edit(./after.BlueprintAdmin.slnx.targets), Edit(**/Directory.Build.*), Edit(**/Directory.Solution.*), Edit(**/NuGet.Config), Edit(**/nuget.config), Edit(**/NuGet.config), Edit(**/*.slnx.targets), Edit(**/Directory.Packages.*), Edit(**/*.csproj.user), Edit(**/obj/**)
---

Review uncommitted or branch work for **contradictions and self-consistency
issues**, and manage `suggestions.md` at the repository root. Every rule's
argument is in `docs/commands/review-branch.md`. The same bar is the one the
`branch-reviewer` agent reads in `/ship` step 5.

## Mode selection (do this first)

**Before choosing a mode, check whether `suggestions.md` exists at the repo
root.** That file is the running record of open issues; when it is present, the
default job is to re-verify it, not to start a fresh review.

| Condition | Mode |
|---|---|
| `suggestions.md` **exists** and `$ARGUMENTS` is empty or `recheck` | **Recheck** (below) |
| `suggestions.md` **missing** and `$ARGUMENTS` is empty | **Full review** — branch vs `main` |
| `$ARGUMENTS` is `full` or `full --local` | **Full review**, even if `suggestions.md` exists (replace the file from scratch after the new pass) |
| `$ARGUMENTS` is `--local` only, no `suggestions.md` | **Full review** of the working tree |
| `$ARGUMENTS` is `--local` and `suggestions.md` exists | **Recheck** first (same as default when the file exists); do not silently switch to a full local sweep unless the user also passed `full` |
| `$ARGUMENTS` is `recheck` and `suggestions.md` **missing** | Stop: say there is nothing to recheck and offer a full review — do not enter recheck mode against a file that is not there |

Prefer the file's presence over the empty argument list.

## What counts as a finding

The bar is **two statements that cannot both be true**, or a statement that
cannot be true of the system described — not pure style taste. Prefer:

1. **Document ↔ code drift** (samples, pins, type names, provider order,
   endpoints, ports, credentials). The design spec
   (`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`) and
   `README.md` make checkable claims; `AdminOptions.cs` owns ports and sibling
   paths.
2. **Cross-document / CLAUDE.md contradictions** — two rules that cannot both
   be true, or a planned tree stated as present. A stale restatement is a
   finding only if this branch **introduces or edits** it
   (`docs/change-locality.md` §2).
3. **Pin drift** — `Directory.Packages.props` vs a `PackageReference`;
   `src/Admin.Web/package.json` vs its lockfile; `global.json` vs the SDK the
   Host builds with.
4. **CI drift** — `.github/workflows/ci.yml` against `docs/testing.md`.
5. **Incomplete reconciliation** — a rule or fix this change states that the
   corpus still violates in the same change set.
6. **A file outside the declared touch set**: each `outside <path>` line of
   `bash .claude/scripts/pr-locality.sh <n>` (`<n>` from
   `bash .claude/scripts/pr-for-branch.sh`), resolved per
   `docs/change-locality.md` §3 — a narrower diff, a row widened with its
   reason, or a different class — never a row widened silently. Where the
   helper prints nothing or cannot run, say so and skip; never infer a class.
   An `inside` line grants nothing. (why: docs/commands/review-branch.md,
   *What counts as a finding*)

Reject as non-findings only the house styles this repository settled — British
prose beside real identifier spellings, and the C# dialect `.editorconfig`
enforces — and stale restatements `docs/change-locality.md` §2 leaves in place
where the owner site is already correct. Do **not** reject frontend-only
forms.

## Recheck mode

**Trigger:** `suggestions.md` is present (default), or `$ARGUMENTS` contains
`recheck`.

1. **Read `suggestions.md` in full.** Enumerate every numbered issue. If none,
   say so and offer a full review rather than inventing items.
2. **For each issue, independently:** locate the sites named under **Where** /
   **File** (grep siblings if the lines moved), verify the **Problem** against
   the current tree and tip, and set exactly one status: **fixed**, **open**,
   **correct** (only if the file or the user already said so) or **false
   positive** (never held, or no longer a defect).
3. **Do not invent new issues**, unless verifying a listed item surfaces a
   direct regression of that item. A full sweep is `full`, not recheck.
4. **Rewrite `suggestions.md`:** the status table and every **Status** line,
   fixed items kept briefly, **Re-checked:** bumped to today.
5. **If every issue is `fixed`, `correct`, or `false positive`:** delete
   `suggestions.md` and report one line of evidence per former issue.
6. **If any issue remains `open`:** keep the file, report counts.

## Full review mode

**Trigger:** no `suggestions.md`, or `full` / `full --local`.

1. **Establish the range.** Branch: run `git merge-base origin/main HEAD` on
   its own (fall back to `main`) and carry the sha it prints into every later
   command — a shell variable does not outlive the call, and an empty one turns
   `<merge-base>..HEAD` into an empty range that reads as a clean review. Then
   `git diff --stat` / `--name-only` / full, and `git log --oneline`, over
   `<merge-base>..HEAD`. `--local`: `git status --short` and `git diff HEAD`.
2. **Read the change, starting from its blast radius.** Run
   `codebase-index diff-impact --base <merge-base> --json` first, or
   `--base HEAD` under `--local`; its list is where the search starts, not
   where it stops, since it names no absences. Prefer full source of
   load-bearing files over the diff; Grep `src/`, `e2e/` and `docs/` for every
   touched **symbol** and the one spec section that owns a rule the change
   moved. Never tour the corpus for a value.
3. **Run cheap gates when the range touches them:**
   `bash .claude/scripts/host-checks.sh [all|fast]` and
   `bash .claude/scripts/npm-checks.sh [all|fast|lint|test|build]` for `src/`
   or toolchain files; `bash .claude/scripts/harness-checks.sh` for
   `.claude/**`, `.github/locality-gate/**` or `tools/**`. The Playwright suite is CI's
   `smoke` job: say it was not run, never imply it was.
4. **Author findings only when verified.** Quote the conflicting sites.
   Severity: **bug** | **suggestion** | **nit**.
5. **Write `suggestions.md` at the repo root** when any issue is open:

   ```markdown
   # Suggestions — branch vs `main`   # or "local uncommitted"

   **Reviewed:** <ISO date>
   **Branch:** <name>
   **Base:** origin/main @ <short sha>
   **Scope:** <one line>
   **Diff:** <N files, +/- lines>

   ## Overall
   <2–4 sentences>

   | # | Severity | Item | Status |
   |---|---|---|---|
   | **1** | bug | … | open |

   ## Bugs / Suggestions / Nits
   ### 1. …

   | | |
   |---|---|
   | **Where** | … |
   | **Status** | open |

   **Problem.** …
   **Suggestion.** …

   ## What looks good (no action)
   …

   ## Recommended fix order
   …
   ```

6. **If the review finds nothing open**, create no file; under `full`, delete
   an old one when clean, replace it when not.
7. **`suggestions.md` is working state.** Never commit it or add it to the
   index; say so if the user is about to ship.

## Report (chat)

Always end with: the mode actually run (and branch/base or local); whether
`suggestions.md` was present at start; issue counts by status; its path if
kept, or that it was deleted or not created; one line per top remaining
finding.

Never fix findings in this command unless the user asks to apply them after
the review. That is enforced by the frontmatter's `disallowed-tools`, which
path-scopes `Edit` away from every tracked tree and root file and denies the
toolchain's auto-loaded names; a redirection on a granted `Bash` command is a
residual the hook bounds from the other side. (why:
docs/commands/review-branch.md, *The frontmatter denies, and what stays open*)
