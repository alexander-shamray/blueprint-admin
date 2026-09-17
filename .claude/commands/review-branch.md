---
description: Review branch vs main for contradictions; recheck suggestions.md when it already exists
argument-hint: "[recheck | full | --local]"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(git diff:*), Bash(git log:*), Bash(git status:*), Bash(git merge-base:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(bash .claude/scripts/npm-checks.sh:*), Bash(bash .claude/scripts/host-checks.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(bash .claude/scripts/pr-locality.sh:*), Bash(rm suggestions.md)
disallowed-tools: Edit(.git/**), Edit(./.git/**), Edit(.git), Edit(./.git), Edit(.claude/**), Edit(./.claude/**), Edit(.remember/**), Edit(./.remember/**), Edit(.github/**), Edit(./.github/**), Edit(.vscode/**), Edit(./.vscode/**), Edit(android/**), Edit(./android/**), Edit(docs/**), Edit(./docs/**), Edit(e2e/**), Edit(./e2e/**), Edit(ios/**), Edit(./ios/**), Edit(public/**), Edit(./public/**), Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(.nvmrc), Edit(./.nvmrc), Edit(.prettierrc), Edit(./.prettierrc), Edit(AGENTS.md), Edit(./AGENTS.md), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(README.md), Edit(./README.md), Edit(angular.json), Edit(./angular.json), Edit(capacitor.config.ts), Edit(./capacitor.config.ts), Edit(eslint.config.js), Edit(./eslint.config.js), Edit(ionic.config.json), Edit(./ionic.config.json), Edit(package-lock.json), Edit(./package-lock.json), Edit(package.json), Edit(./package.json), Edit(playwright.config.ts), Edit(./playwright.config.ts), Edit(tsconfig.app.json), Edit(./tsconfig.app.json), Edit(tsconfig.json), Edit(./tsconfig.json), Edit(tsconfig.spec.json), Edit(./tsconfig.spec.json), Edit(.npmrc), Edit(./.npmrc), Edit(npm-shrinkwrap.json), Edit(./npm-shrinkwrap.json), Edit(vitest.config.ts), Edit(./vitest.config.ts), Edit(vitest.config.js), Edit(./vitest.config.js), Edit(vitest.config.mts), Edit(./vitest.config.mts), Edit(vite.config.ts), Edit(./vite.config.ts), Edit(vite.config.js), Edit(./vite.config.js), Edit(vite.config.mts), Edit(./vite.config.mts), Edit(eslint.config.mjs), Edit(./eslint.config.mjs), Edit(eslint.config.cjs), Edit(./eslint.config.cjs), Edit(prettier.config.js), Edit(./prettier.config.js), Edit(prettier.config.cjs), Edit(./prettier.config.cjs), Edit(prettier.config.mjs), Edit(./prettier.config.mjs), Edit(.prettierrc.js), Edit(./.prettierrc.js), Edit(.prettierrc.cjs), Edit(./.prettierrc.cjs), Edit(.prettierrc.json), Edit(./.prettierrc.json), Edit(karma.conf.js), Edit(./karma.conf.js), Edit(jest.config.js), Edit(./jest.config.js), Edit(**/*.config.js), Edit(**/*.config.cjs), Edit(**/*.config.mjs), Edit(**/*.config.ts), Edit(**/*.config.mts), Edit(**/package.json), Edit(**/.npmrc), Edit(**/tsconfig*.json), Edit(**/.prettierrc*), Bash(git push:*), Edit(node_modules/**), Edit(./node_modules/**), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(global.json), Edit(./global.json), Edit(BlueprintAdmin.slnx), Edit(./BlueprintAdmin.slnx), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore)
---

Review uncommitted or branch work for **contradictions and self-consistency
issues**, and manage `suggestions.md` at the repository root.

## Mode selection (do this first)

**Before choosing a mode, check whether `suggestions.md` exists at the repo
root.** That file is the running record of open issues; when it is present,
the default job is to re-verify it, not to ignore it and start a fresh review.

| Condition | Mode |
|---|---|
| `suggestions.md` **exists** and `$ARGUMENTS` is empty or `recheck` | **Recheck** (below) |
| `suggestions.md` **missing** and `$ARGUMENTS` is empty | **Full review** — branch vs `main` |
| `$ARGUMENTS` is `full` or `full --local` | **Full review**, even if `suggestions.md` exists (replace the file from scratch after the new pass) |
| `$ARGUMENTS` is `--local` only, no `suggestions.md` | **Full review** of the working tree |
| `$ARGUMENTS` is `--local` and `suggestions.md` exists | **Recheck** first (same as default when the file exists); do not silently switch to a full local sweep unless the user also passed `full` |
| `$ARGUMENTS` is `recheck` and `suggestions.md` **missing** | Stop: say there is nothing to recheck and offer a full review — do not enter recheck mode against a file that is not there |

`recheck` as an explicit argument is kept for clarity; it is **not** required
when the file is already there. Prefer the file’s presence over the empty
argument list.

---

## What counts as a finding

The bar is **two statements that cannot both be true**, or a statement that
cannot be true of the system described — not pure style taste. Prefer:

1. **Document ↔ code drift** (samples, pins, type names, provider order,
   endpoints, ports, credentials). The spec's §11 and
   `docs/client-architecture.md` are the documents that make checkable
   claims about this code; read §12 before treating a spec claim as
   settled.
2. **Cross-document / CLAUDE.md contradictions** — two rules that cannot both
   be true, or a planned tree stated as present. **Not** a pre-existing
   phase marker, test count, project count or second copy of a value that
   this branch left untouched: those are restatements
   `docs/change-locality.md` §2 forbids writing, so a stale one is awaiting
   removal by the plan, not a finding against this branch. One this branch
   **introduces or edits** is a finding under the same section.
3. **Pin drift** — `package.json` vs `package-lock.json` vs the versions the
   spec's §11 and the plan's *Versions* table state; a package imported in a
   sample and pinned nowhere; a peer range the lockfile did not resolve to.
4. **Native and CI drift** — `capacitor.config.ts`, `android/**`, `ios/**` or
   `.github/workflows/ci.yml` against what the spec's §8 and
   `docs/client-architecture.md` §15 claim (schemes, origins, the emulator
   relaxations, which job asserts what). This class is worth its own line
   because lint, the unit suite and the web build cover none of it: the
   relaxations live in files `cap sync` generates.
5. **Incomplete reconciliation** — a rule this change states (or a fix it claims)
   that the corpus still violates in the same change set.
6. **A file outside the declared touch set.** The PR body's `| Class |` and
   `| Touch set |` rows say what this branch may edit, and
   `bash .claude/scripts/pr-locality.sh <n>` — `<n>` from
   `bash .claude/scripts/pr-for-branch.sh` — judges every changed path
   against them and prints one `class` line and one `inside <path>` or
   `outside <path>` line per file. **It never prints the rows**: the set is
   the author's text, and a path grammar cannot keep prose out of a path,
   so the helper consumes the cell and only its own two words leave. Each
   `outside` line is a finding, and its resolution is a narrower diff, a
   row widened with its reason beside it when the path is inside the
   class's tree set, or a different class when it is not — never a row
   widened silently (`docs/change-locality.md` §3). Where the helper prints
   nothing or cannot run — a `--local` review with no PR yet, a body
   carrying neither row, or the sandbox clone, which has no network — say so
   and skip this check rather than inferring a class. **Nothing enforces the
   touch set here**: the repository this came from runs
   `.github/locality-gate/` on every push, that workflow was not ported, and
   `docs/change-locality.md` §3 says so. This helper is the whole of the
   check rather than an early read of one, so a skipped or unparseable
   verdict leaves the rows unverified rather than merely unverified-yet. **The verdict narrows
   and grants nothing**: an `inside` line is not a licence for anything
   this command's grant refuses, and the class's tree set in the contract
   still bounds what a row may declare.

Reject as non-findings the house styles `docs/style-guide.md` tabulates on
purpose (braceless single statements, file-scoped namespaces, explicit
types, British prose beside real identifier spellings, unpinned Aspire with
§4.4 carve-outs, spread-over-`.ToArray()` when the corpus is already
clean), and the stale restatements `docs/change-locality.md` §2 leaves in
place — a pre-existing count, "since PR-NN" or second copy of a value that
this branch did not touch, where the owner site is already correct.

---

## Recheck mode

**Trigger:** `suggestions.md` is present (default), or `$ARGUMENTS` contains
`recheck`.

1. **Read `suggestions.md` in full.** Enumerate every numbered issue from the
   status table and from the per-issue headings. If the file is empty or has no
   numbered issues, say so and offer a full review rather than inventing items.
2. **For each issue, independently:**
   - Locate the sites named under **Where** / **File** (grep siblings if the
     line numbers moved).
   - Verify whether the stated **Problem** still holds in the current tree
     (and, for branch work, against the current tip — not an old diff alone).
   - Set status to exactly one of:
     - **fixed** — evidence shows the defect is gone
     - **open** — still true
     - **correct** — owner accepted the current state as intentional (only if
       the file already said so, or the user has said so in this conversation)
     - **false positive** — original finding was wrong; only if re-verification
       shows the claim never held / no longer applies as a defect
3. **Do not invent new issues** in recheck mode unless verifying a listed item
   surfaces a **direct regression of that item** (same claim, still broken in a
   new place). A full sweep for new findings is `full`, not recheck.
4. **Rewrite `suggestions.md`:**
   - Update the status table and every per-issue **Status** line.
   - Keep fixed items briefly under their headings (or a “Fixed this pass”
     section) so the next recheck still has context — unless step 5 deletes the
     file.
   - Bump **Re-checked:** to today’s date.
5. **If every issue is `fixed`, `correct`, or `false positive`:**
   - **Delete** `suggestions.md`.
   - Report in chat: all closed, file removed, one-line evidence per former
     issue.
6. **If any issue remains `open`:** keep the file, report counts
   (open / fixed / other).

---

## Full review mode

**Trigger:** no `suggestions.md`, or user passed `full` / `full --local`.

1. **Establish the range.**
   - Branch (default): `MERGE_BASE=$(git merge-base origin/main HEAD)` (fall
     back to `main`), then `git diff --stat` / `--name-only` / full diff
     `"$MERGE_BASE..HEAD"`, plus `git log --oneline "$MERGE_BASE..HEAD"`.
   - `--local`: `git status --short` and `git diff HEAD` (include untracked
     that matter; skip bulk tooling noise).
2. **Read the change.** Prefer full source of load-bearing files over the
   diff alone. Grep `src/`, `e2e/` and `docs/` for every **symbol** the
   change touches, and the one spec section or `docs/client-architecture.md`
   argument that owns a rule the change moved. Do not tour the corpus for the value: a restatement outside the
   touch set that the change left stale is not this branch's to fix
   (`docs/change-locality.md` §2).
3. **Run cheap gates when the range touches them.**
   - Lint and the unit suite, when the range touches `src/` or any file the
     toolchain reads:
     `bash .claude/scripts/host-checks.sh [all|fast]` and
     `bash .claude/scripts/npm-checks.sh [all|fast|lint|test|build]`

   **In the sandbox none of them is available**, and that is deliberate rather
   than an oversight: every one starts with `npm ci`, which needs
   `registry.npmjs.org`, and the reviewer's egress allow-list admits
   `api.x.ai` and `auth.x.ai` and nothing else. An image that could install its
   own toolchain could install anything, which is the capability the sandbox
   exists to remove. So whether lint, the tests and the build are green is the
   **host's** to verify — report it as unverified rather than asserting it, and
   never report `command not found` as a finding about the branch.

   **The Playwright suite is nobody's gate here.** It needs the Host under
   FakePlatform — `docs/testing.md` owns what — and `npm-checks.sh` has no
   `e2e` mode for exactly that reason. CI's `smoke` job is where it runs.
   Say it was not run; do not imply it was.
4. **Author findings only when verified.** Quote the conflicting sites.
   Severity: **bug** | **suggestion** | **nit**.
5. **Write `suggestions.md` at the repo root** when any issue is open. Shape:

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

6. **If the review finds nothing open**, do **not** create `suggestions.md`.
   If `full` was requested and an old file existed, **delete** it when the new
   pass is clean; **replace** it when the new pass has findings.
7. **`suggestions.md` is working state.** Never commit it. Do not add it to
   the index. Say so if the user is about to ship.
8. **Write `.grok-review-ran` at the repo root, as the last thing you do** —
   one line, any content. It is gitignored, so it dirties nothing.

   **This is the evidence that the review happened, and it exists because a
   finished turn is not that evidence (#18).** `grok-review.sh` reads an
   absent `suggestions.md` as "nothing to report", and it could only tell that
   apart from "the reviewer never looked" by the model's root `stopReason` —
   which proves a completed response and not a single tool call. A model that
   ended its turn without opening the clone left no `suggestions.md` and was
   counted as a clean round. So the file's absence is now a refusal rather
   than a pass, and writing it is the one step that cannot be skipped by a
   review that decided there was nothing to say.

   Its **content is never read** — only that it exists and is a regular file —
   so nothing written here steers anything on the host.

---

## Report (chat)

Always end with:

- Mode actually run: **recheck** or **full** (and branch/base or local)
- Whether `suggestions.md` was present at start
- Issue counts by status (open / fixed / correct / false positive)
- Path to `suggestions.md` if kept, or that it was deleted / not created
- One-line top remaining findings (if any)

Do not fix the findings in this command unless the user explicitly asks to
apply them after the review.

**That is enforced now, and used to be prose alone (#60).** This command
declared "do not fix" while holding `Write` and `Edit` over every path
`.claude/settings.json` did not deny — a read-only claim resting on prose while
the grant permits writing everywhere, which for a review command is the worse
failure. The frontmatter's `disallowed-tools` path-scopes `Edit` away from
every tracked tree, `docs/` included, **and from every tracked file at the
repository root**.

**The root files were the hole in the first version of this**, raised in review
and worth stating rather than quietly patching: denying directories alone left
`CLAUDE.md`, `package.json`, `angular.json` and `tsconfig.json` writable,
which is a boundary with a gap exactly where this repository keeps its build
inputs. A command promising not to fix findings could still apply one
to root configuration.

They are **enumerated** rather than denied wholesale, and `suggestions.md` is
why: it lives at the root, it is this command's one legitimate output, and it
is untracked — so denying every *tracked* root file leaves it alone, where a
blanket `Edit(**)` or a `/*` root pattern would take the deliverable with it.
`test_grok_helpers.py` reads the tracked set from `git ls-files` and asserts
each is denied, so a new root file is a red build rather than a silent gap.

**Two limits, both stated rather than glossed.**

`disallowed-tools` binds the Claude Code host path — `/review-branch` run here,
including `--local`. It says nothing about the containerised run: inside
`grok-review.sh` this file is read by **grok**, a different CLI, under
`--permission-mode bypassPermissions`, and nothing has established that grok
honours a `disallowed-tools` key at all. There the only thing keeping the
reviewer from rewriting the branch it is reviewing is the container's
disposability — a property of the sandbox, not of this grant. Do not read the
frontmatter as reaching that run.

And the list is a deny-list, so a tree added later is editable until someone
adds it. `test_grok_helpers.py` asserts the list covers every tracked
top-level tree, which is what makes that a red build instead of a quiet
widening.

**That test can never see the case that mattered, and the reason is
structural.** It reads `git ls-files`, so it enumerates what EXISTS; the
dangerous file is one that does not. The Node toolchain reads a set of names
without being asked, and several of them **are JavaScript that is executed in
order to be loaded**: `eslint.config.js` runs when ESLint starts, a
`vitest.config.ts` runs when the tests do, `.npmrc` can set `script-shell`,
and a `pre`/`post` lifecycle entry in `package.json` runs on every `npm`
invocation. A command granted `Write` and a build at once could create a root
file the enumeration could not contain and then run the build it already had —
host code execution, the same shape an auto-imported MSBuild targets file had
in the repository these commands came from, where it was measured and raised
in review.

Two changes close it, and they close different halves. The executor is
`npm-checks.sh`, which fixes the script names and the flags — `npm run:*`
would admit any script the manifest names, including one this command had just
written, and `npm test -- --config <path>` admits a file that is executed in
order to be read, `suggestions.md` included. And the auto-load surface itself
is denied: every name the toolchain reads without being asked, in the exact
spelling this file already uses, plus `**/*.config.js`, `**/*.config.cjs`,
`**/*.config.mjs`, `**/*.config.ts`, `**/*.config.mts`, `**/package.json`,
`**/.npmrc`, `**/tsconfig*.json` and `**/.prettierrc*` for the class.

**Which half is measured is worth saying.** The exact-filename form is the one
this file has always used and the one the suite reads. The `**/` globs are the
documented gitignore-style syntax and are **not** measured here — they are
belt to the exact names' braces, so if that syntax turned out inert in a
`disallowed-tools` value the demonstrated vector would still be closed. Do not
read them as the control; read the names as the control.

**A redirection on any granted `Bash` command still writes what these refuse,
and that is a residual rather than a closed boundary.** Measured against this
repository's own hook, not reasoned about: `ls > .claude/settings.json` and
`wc -l README.md > package.json` are both admitted, and so is
`git log --oneline > package.json` — `guard-git-argv.py` strips redirections in
order to judge the argv and never refuses the write one performs, and
`guard-edit-target.py` is registered on `Edit|Write|NotebookEdit|MultiEdit`, so
it never sees a `Bash` call at all. Every `Edit(...)` rule in this file is
therefore defence in depth against a model that reaches for the editing tools,
not a bound on what this command can write.

`/review-grok` was the one command that closed it, by denying `Bash` whole —
its body argues exactly this case, from the round that measured it. The
commands that cannot follow it are the ones whose helpers *are* their API. What
stood for them was that every granted `Bash` entry names a fixed helper or a
read-only git verb, so a redirect had to be appended by the model itself rather
than supplied by anything it read: a weaker property, and the true one.

**Closed now, in the place that sentence named (#20).** `guard-git-argv.py`
already parsed redirections in order to strip them, so the targets were in hand
and what was missing was a rule about them. A redirection that OPENS its target
is refused when the path names the machinery trees or a toolchain root file;
reading through `<` is untouched, since reading the machinery is what half this
command does. The three measured above are refused, and the write no longer
lands. So the `Edit(...)` rules in this file and the hook now bound the same
surface from two sides, which is what the paragraph above said they did not.

The residual that replaces it, because closing one is not closing the class: a
redirection is not the only way a command writes — `tee`, `cp`, `sed -i` and an
interpreter all do, and none is judged. What made the redirection worth closing
first is that it rides on a command that is already approved.

**The verbs are judged too since #26**, because under a user-level `auto` mode
an un-granted `cp` is approved as well; the interpreters and the writers not on
the hook's list are what stands, and `docs/harness-boundaries.md` owns that
residual.

**`node_modules/` is denied too, and the sentence that used to stand here was
wrong.** It is gitignored, so no enumeration built on `git ls-files` names it,
and `node_modules/.bin` holds every executable `npm-checks.sh` invokes. This
paragraph used to say that what stood instead was "no grant that writes
outside the enumerated trees" — which is false, and Copilot said so. `Edit`
and `Write` are granted here without a path scope; the enumerated trees are a
DENY list, and a path no rule names is permitted. So an injected finding could
have written `node_modules/.bin/ng` or `node_modules/@angular/cli/bin/ng.js`
and the `npm run lint` that follows would have executed it. Both spellings are
denied now, in every command that holds an editing tool, and
`test_every_editing_command_denies_the_machinery` covers it.

What is still true, and is a mitigation rather than the control: `npm ci`
removes and reinstalls the tree from the lockfile, so a write into it does not
survive the next install.
