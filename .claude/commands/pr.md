---
description: Open a pull request with a body in the house form
argument-hint: "[title — omit to derive it from the commits]"
allowed-tools: Read, Grep, Glob, Write, Bash(git status:*), Bash(git log:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git push -u origin:*), Bash(git push origin:*), Bash(bash .claude/scripts/gh-pr-create.sh), Bash(bash .claude/scripts/pr-closure-input.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(bash .claude/scripts/npm-checks.sh:*), Bash(bash .claude/scripts/host-checks.sh:*), Bash(bash .claude/scripts/harness-checks.sh:*)
disallowed-tools: Edit(.claude/**), Edit(./.claude/**), Edit(.github/**), Edit(./.github/**), Edit(.remember/**), Edit(./.remember/**), Edit(android/**), Edit(./android/**), Edit(ios/**), Edit(./ios/**), Edit(.git/**), Edit(./.git/**), Edit(package.json), Edit(./package.json), Edit(package-lock.json), Edit(./package-lock.json), Edit(npm-shrinkwrap.json), Edit(./npm-shrinkwrap.json), Edit(.npmrc), Edit(./.npmrc), Edit(angular.json), Edit(./angular.json), Edit(tsconfig.json), Edit(./tsconfig.json), Edit(tsconfig.app.json), Edit(./tsconfig.app.json), Edit(tsconfig.spec.json), Edit(./tsconfig.spec.json), Edit(eslint.config.js), Edit(./eslint.config.js), Edit(.prettierrc), Edit(./.prettierrc), Edit(capacitor.config.ts), Edit(./capacitor.config.ts), Edit(playwright.config.ts), Edit(./playwright.config.ts), Edit(ionic.config.json), Edit(./ionic.config.json), Edit(.nvmrc), Edit(./.nvmrc), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(README.md), Edit(./README.md), Edit(**/*.config.js), Edit(**/*.config.cjs), Edit(**/*.config.mjs), Edit(**/*.config.ts), Edit(**/*.config.mts), Edit(**/package.json), Edit(**/.npmrc), Edit(**/tsconfig*.json), Edit(**/.prettierrc*), Edit(node_modules/**), Edit(./node_modules/**)
---

Open a PR for the current branch. Title: $1 — if empty, derive it from the
commits.

## Push the branch first

Read `git status -sb` and do the least that is needed:

| State | Action |
|---|---|
| No upstream | `git push -u origin <branch>` |
| Tracking, ahead | `git push origin <branch>` |
| In sync | Nothing |

Name the remote and the branch in both cases. A bare `git push` relies on the
branch's tracking config to say where it goes, and it matches neither allow
rule in `.claude/settings.json` — so it prompts, which is the one thing an
unattended chain must not do.

Say which of the three it was. A push is the first thing in this command that
another person can see, so it is worth one line of report rather than
happening silently.

**Two kinds of push are denied in `.claude/settings.json`, and neither is a
step to work around:** rewriting history (`--force`, `-f`, `--force-with-lease`,
`--delete`) and anything landing on `main`. A branch wanting either is raising
a decision — stop and say so. Do not reach for `gh pr create`'s own offer to
push either: it is the same action by a route that skips the upstream check
above, so it pushes without ever reporting that it did.

**The deny list is defence in depth, not the guard.** It matches on command
prefixes, and a refspec has more spellings than a prefix list can hold —
`origin main`, `origin HEAD:main` and `origin <branch>:main` are three ways to
say one thing, and only the first two are enumerated. What actually keeps this
safe is the rule above: push the **current branch, by name**, and nothing else.
A push whose destination is not the branch you are on is not this step.

**The allow rules do not guarantee the push runs.** Under a user-level
`"defaultMode": "auto"` the push has been refused with the harness's "has been
denied" text and no hook reason, on three branches in a row (#33);
`docs/harness-boundaries.md` owns what is and is not known about why. A
refused push is a **stop**, not a retry: report that the push did not happen
and no PR was opened, give the line for the caller to run —
`! git push -u origin <branch>`, or the tracking form — and name the remedy
that removes the need for it, which is user-level: this repository and its
`origin` named as trusted source control in `autoMode.environment`. Never
reach the remote by another spelling or a wrapper whose text hides it; that
is routing around a refusal, not pushing.

## Title

One line, semantic prefix, the same form as the commits. It names the change,
not the file count. Where the branch implements a task from the
implementation plan, use its `## Task n:` heading verbatim.

A branch carrying several related commits takes a title that covers them
honestly — `chore: Claude Code guidance, return types in the samples, and the
stale pins it surfaced` names the third thing rather than hiding it under the
first two.

## Body

Wrapped at 80 columns, British spelling, no emoji. Structure:

1. **An opening sentence or two** saying what the branch is and how it is
   organised — `Five commits, each self-contained.`
2. **`## What changed`**, then one block per commit, each led by a bold line
   naming that commit:

   ```markdown
   **`docs:` return types in the service samples**

   211 inferred returns become 27 annotated ones. A reader of a fenced code
   block has no hover and no go-to-definition…
   ```

   The block argues the change the way the commit body does; it does not
   summarise the diff. Reuse the commit bodies where they already say it well.
3. **The honest cost**, wherever there is one. A PR that says what it
   deliberately did *not* do — and why — is the one a reviewer can trust.
   Name anything left undecided, and anything a later change will have to
   revisit.
4. **Tables** for summary data. The two-column borderless form (`| | |`) is the
   established shape for metadata.
5. The `🤖 Generated with [Claude Code]` footer and session link.

### The class and the touch set

The metadata table opens with two rows. The class and the touch set are
decided before the first edit — in the issue, or in the first commit's body
when there is none, per `docs/change-locality.md` §5 — and copied here when
the PR opens, with any file added mid-work and the reason it was:

```markdown
| | |
|---|---|
| Class | A |
| Touch set | `src/Admin.Host/Jobs/**`, `tests/Admin.Host.Tests/Jobs/**` |
```

`Class` is one letter from that file's table — A local, B shared mechanism,
C a rule moved, D docs or harness, E the dependency graph — or two joined
with `+`, as `C+E`, for a change the contract's §3 says needs both, with the
touch set as the union. `Touch set` is the
paths that class allows and this branch actually edited, as globs or files on
one line. A file in the diff outside the set is a finding for
`/review-branch`, and the answer is a narrower diff, a row widened with its
reason beside it, or a different class — never a row widened silently.

### What the branch closes

**A closing keyword inside a table cell links nothing.** The metadata row —
`| Closes | #88 (high), #81 (high) |` — is a summary for a reader and that is
all it is: a cell boundary sits between `Closes` and `#88`, so GitHub is never
handed a keyword-reference pair. This is not GitHub declining to read a table.
PR #112's row was the only place its keywords appeared,
`closingIssuesReferences` reads `[]` to this day, and #84, #70 and #40 were
closed by hand once somebody noticed.

So the body carries **both**: the row as the human-readable summary, and a
bare `Closes #n` line for each issue below it.

**A `Closes #n` in a commit body fires on merge whatever the description
says.** That is the opposite failure and it has fired too. PR #116's review
loop narrowed two of its claims and the body was rewritten to say *"#56 stays
open"*; the merge closed #30 and #56 anyway, out of commits written before the
loop ran. Both were reopened by hand with the reason recorded.

**The commits are the half that cannot be taken back, so the description is
reconciled to them** — never the other way round. A description is editable
and a commit message is not, which is why withdrawing a closure from the body
reads as sufficient and is not. If a commit already closes an issue the branch
has since decided to leave open, name it in the description, let it close, and
reopen it with the reason on the issue itself.

**A keyword the body only *discusses* still links.** GitHub's linker does not
read markdown, so a `` `Closes #30` `` quoted inside an argument about closures
closes #30 — which makes this section's own examples a hazard for any PR that
edits it. Write the number away from the keyword when the point is the keyword.

**This repository has no closure gate, and that is a gap rather than a
decision.** The backend these commands came from carries
`.github/closure-gate/`, a workflow that compares what the body *says* against
what the merge *will do*, on every push and on every description edit. Nothing
here does, so the reconciliation above is **checked by reading** and not by a
gate — which is exactly the arrangement that let PR #112 and PR #116 go wrong
over there. Porting the gate is a follow-up worth having.

Until then, ask GitHub directly once the pull request exists — **not before
opening**: `pr-closure-input.sh` needs a pull request to read, and
`closingIssuesReferences` is GitHub's parse of a body it has not been given
yet, so there is nothing to ask about until the PR is open:

```bash
bash .claude/scripts/pr-closure-input.sh <n>
```

Read `closingIssuesReferences` out of that JSON and check it against the
`Closes` lines you wrote. A number in one and not the other is the finding.

State the check result in the body — `dotnet test` via
`bash .claude/scripts/host-checks.sh all`, and `npm run lint` / `npm test` /
`npm run build` via `bash .claude/scripts/npm-checks.sh all` — and report it
as it actually came out. Say plainly when the Playwright suite was not run;
it needs the Host under FakePlatform, and CI's `smoke` job is the place it
runs. `docs/testing.md` owns the trigger contract.

## Before opening

- `bash .claude/scripts/pr-for-branch.sh` — an OPEN row means you are
  updating, not creating. Say so and stop. It returns the newest row for this
  repository and no other, so a branch name reused after a merge cannot show
  the old `MERGED` row here and have this command open a duplicate (#24).
- `bash .claude/scripts/host-checks.sh all` and
  `bash .claude/scripts/npm-checks.sh all` — Host tests, SPA lint, tests and
  build, green before the PR opens, or said plainly in the body if not.
  `bash .claude/scripts/harness-checks.sh` when the diff reaches `.claude/**`
  or `.github/locality-gate/**`.

## Steps

Write the title with `Write` to `pr-title.txt` and the body to `pr-body.md`,
both at the checkout root — the only two paths the helper reads — rather than
through a heredoc, which mangles the wrapping. Then:

```bash
bash .claude/scripts/gh-pr-create.sh
```

**The title never appears on the command line.** It comes from `$1` or from
commit text, and inside double quotes a title such as `fix: $(…)` runs the
substitution in the calling shell before the helper can refuse anything. A
file is the channel `gh-issue-filing.sh` already uses for untrusted titles,
and it is why the helper takes no argument.

**The helper rather than `gh pr create` since #16.** `Bash(gh pr create:*)` was
a prefix grant, so a trailing `--repo`, `--head`, `--base` or `--body-file`
chose a repository, a branch, a base and a body that were not the ones this
command derived. All four are fixed in the helper now: the repository and the
branch come from the checkout, the base is the literal `main`, and the body
file is the caller-created `pr-body.md` at the checkout root — `--body-file`
publishes whatever it reads, including a file the session's own `Read` is
bounded away from. Both files are gitignored, a tracked one is refused as the
branch's text rather than this run's, and the helper removes both once the PR
is open, so a successful run leaves nothing for `/ship`'s clean-tree gate.

## Report

The PR URL, its title, and the commits it carries. If any check you named above
was skipped, say which.
