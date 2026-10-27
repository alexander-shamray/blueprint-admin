---
description: Start a correctly named working branch — in its own worktree under .claude/worktrees/ from a clean main, in place when the tree is dirty or that directory is not writable
argument-hint: "[what the change does] — omit to derive it from the uncommitted work"
allowed-tools: Read, Grep, EnterWorktree, Bash(git status:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git log:*), Bash(git fetch origin:*), Bash(bash .claude/scripts/git-branch-create.sh:*), Bash(bash .claude/scripts/git-worktree-fork.sh:*), Bash(bash .claude/scripts/git-switch-existing.sh:*), Bash(git rev-parse:*), Bash(git worktree list:*), Bash(ls:*), Bash(bash .claude/scripts/spa-ci.sh)
---

Create a branch for: $ARGUMENTS — if empty, derive it from the uncommitted
work.

`main` is not a working branch. If work has already started on it, this command
moves it off: uncommitted changes follow a `checkout -b`, so nothing is lost
and nothing is stashed.

## A branch is a workspace, not a HEAD

A new branch gets its own worktree and the session moves into it; the
exceptions are named in steps 1 and 5, and both branch in place (why:
docs/commands/branch.md, *A branch is a workspace, not a HEAD*).

```
C:/dev/ashamray/blueprint-admin                                main — stays clean, stays put
C:/dev/ashamray/blueprint-admin/.claude/worktrees/ratelimit    feat/usage-limit-guard
C:/dev/ashamray/blueprint-admin/.claude/worktrees/masstransit  feat(template)/masstransit-registration
```

- `.claude/worktrees/<slug>` is the only shape: any other path, a sibling
  included, makes `EnterWorktree` ask a question that no rule suppresses.
- It is inside the checkout, so `.gitignore` carries `.claude/worktrees/`, and
  `git-worktree-fork.sh` refuses a path git does not ignore.
- The slug is the branch summary cut to its first word or two, without the
  `<type>/` prefix or a scope: `masstransit`, not `feat(template)`.
- A worktree carries committed files only: it needs its own `npm ci`, and
  anything else untracked that a build needs is said, never copied quietly.
- The host's sibling clones resolve from the main checkout, not the worktree.
- The sweeps' detached `mktemp -d` worktrees are a different design; do not
  make one match the other.

## Steps

0. **Ask whether this is already an isolated workspace**, before creating
   anything:

   ```bash
   git rev-parse --git-dir --git-common-dir --show-superproject-working-tree
   ```

   The first two differing, with no superproject, means a linked worktree:
   report its path and branch. It is this change's workspace, so step 5 forks
   nothing — but go on to step 1, which still judges the branch (why:
   docs/commands/branch.md, *Step 0*).
1. **Read the current state.** `git branch --show-current` and
   `git status --short`. Four cases:
   - **On `main`, clean** — fetch, then cut from `origin/main`, normally as a
     worktree.
   - **On `main`, dirty** — **branch in place, with no worktree**, from `HEAD`,
     carrying the changes; never stash or patch them. Say so, and that it
     lives in the main checkout. An in-place branch forgoes the worktree for
     good, and re-entering `/branch` will not get one back; the user's manual
     route is below (why: docs/commands/branch.md, *Step 1*):

     ```bash
     git switch main
     git worktree add .claude/worktrees/<slug> <branch>
     ```

     then `EnterWorktree` on the new path.
   - **Detached — `git branch --show-current` prints nothing.** Make a branch
     here with `bash .claude/scripts/git-branch-create.sh <name> HEAD`,
     carrying any changes, and say the workspace is this directory.
   - **Already on a branch** — stop and say so. Report the branch, its
     upstream, its worktree if any and whether the tree is dirty, then ask
     whether this is a second change or a continuation. Never branch off a
     feature branch unasked.
2. **Derive the description if none was given.** Read `git status --short`,
   then `git diff`, `git diff --stat` and the untracked files: the branch names
   the *change*, which only the diff says. A clean tree and no argument leaves
   nothing to derive from: stop and ask. Two unrelated changes cannot both be
   in the name: name the dominant one and flag the other (why:
   docs/commands/branch.md, *Step 2*). The type falls out of what was touched:

   | | |
   |---|---|
   | `docs/**` only | `docs/` |
   | `.editorconfig`, `CLAUDE.md`, `AGENTS.md`, `.claude/**`, `tools/**`, CI, `.mcp.json`, `.codeindexignore`, `src/Admin.Web/package.json`, `src/Admin.Web/eslint.config.js`, `src/Admin.Web/.prettierrc` | `chore/` |
   | `src/Admin.Host/**`, `src/Admin.Mcp/**`, `src/Admin.Web/**`, `tests/**` | `feat(<scope>)/`, `fix/` or `refactor/` — the diff decides which |

   A mixed tree takes the type of the change that carries the argument, not
   the one with the most files.
3. **Name it `<type>/<kebab-summary>`**, the type the commit will carry:

   | | |
   |---|---|
   | `docs/` | Spec and architecture prose, structure, cross-references |
   | `chore/` | Repo config — linters, `CLAUDE.md`, commands, CI, pins |
   | `feat(<scope>)/` | Application code that adds behaviour |
   | `fix/` | A defect in either |
   | `refactor/` | Shape change, no behaviour change |

   Established names to match: `fix/loopback-origin-guard`,
   `feat(jobs)/process-runner`. Summarise the change, do not describe the
   files — `fix/trace-window-bounds`, not `fix/update-jobs`. For a task from
   the plan, grep `docs/superpowers/plans/` for its `## Task n:` heading and
   derive the name from it.
4. **Check the name and the directory are both free** — `git branch --list`,
   `git branch -a` and `git worktree list`. A name that exists locally or on the
   remote means the work may be underway: say so, never pick a variant. On the
   one path that uses the directory (clean `main` in the main checkout, step 5's
   first row) also look at the path itself, because `git worktree list` cannot
   see an ordinary file there:

   ```bash
   ls -d .claude/worktrees/<slug>
   ```

   Anything already there stops this command: report whether it is a registered
   worktree of this repository or something unrelated, and ask — never adopt it
   as this change's workspace (why: docs/commands/branch.md, *Step 4*).
5. **Create the workspace, then move into it.** Three of step 1's cases skip
   the fork and none skips the branch (why: docs/commands/branch.md, *Step 5*):

   | Step 1 said | This step does |
   |---|---|
   | On `main`, clean, in the main checkout with a writable `.claude/worktrees/` | Both halves — fork the worktree, enter it |
   | On `main` clean, but **already in a linked worktree** (step 0) | `bash .claude/scripts/git-branch-create.sh <name> origin/main` here. The workspace exists; forking a second is what step 0 refused |
   | On `main` clean, `.claude/worktrees/` not writable | `bash .claude/scripts/git-branch-create.sh <name> origin/main` where you are |
   | On `main` dirty, or **detached** | `bash .claude/scripts/git-branch-create.sh <name> HEAD` — the point is to carry what is in this tree |
   | Already on a branch | Nothing — step 1 stopped |

   Every clean-`main` row cuts from `origin/main`, never local `main`; the
   dirty and detached rows stay on `HEAD`. From a clean `main` at the main
   checkout's root, one helper does both halves; it fixes its own flags,
   including `--no-track`, so `/pr` later sets the right upstream:

   ```bash
   bash .claude/scripts/git-worktree-fork.sh .claude/worktrees/<slug> <name>
   ```

   Then switch the session in with `EnterWorktree`, passing the directory as
   `path`. Everything after this command runs there. The task list is the
   caller's to move, not this command's: a direct run for an issue reports the
   row's move as owed (`docs/working-rules.md` owns the rule).

   **`src/Admin.Web/node_modules` is required before `npm-checks.sh`**, on every
   path that created a branch. Run `bash .claude/scripts/spa-ci.sh` once if it
   is missing, or if `src/Admin.Web/package.json` or `package-lock.json` is
   dirty; skip only when the directory is present and neither file is dirty.
   The helper runs `npm ci`, never `install`.

   **If the fork fails**, read the message first: fall back to the in-place
   branch only when git printed `fatal: could not create leading directories`
   with `Permission denied`. Any other failure stops the command and is
   reported verbatim. A fallback reports the worktree could not be created.
   `git worktree add -b` makes the branch before the directory, so look before
   creating (why: docs/commands/branch.md, *Step 5*):

   | After the failed fork | Take |
   |---|---|
   | `git branch --list <name>` prints it | `bash .claude/scripts/git-switch-existing.sh <name>` — already cut from `origin/main` and untracked |
   | It prints nothing | `bash .claude/scripts/git-branch-create.sh <name> origin/main` |

   **If the session cannot enter a worktree that was created, stop and report
   the path.** Never carry on with `git -C` against a directory the session is
   not in; leave the worktree standing. No upstream is set either way — `/pr`
   does that on the first push.

## Report

State the branch created and what it was cut from; **the worktree it lives in
and whether the session is now inside it**; and, if the tree was dirty, the
files carried across. For an issue, one line: its task-list row had moved
before this command ran, or the move is owed. A branch made in place says so
and why. Nothing here removes a worktree. A derived name is a guess: state that
no description was passed, give the one-line reading of the diff, and say
`git branch -m <better-name>` is free until `/pr` pushes (why:
docs/commands/branch.md, *Report*).
