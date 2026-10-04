#!/usr/bin/env bash
# Fork /branch step 5's worktree under .claude/worktrees/, and nothing else.
#
# The whole command is fixed here: `git worktree add --no-track -b <branch>
# <path> origin/main`. A `Bash(git worktree add:*)` grant would buy that and
# also `-B`, which does not create a branch but **resets** an existing one to
# the start point — the operation `.claude/settings.json` denies as
# `git branch --force` and `git branch -M`. A grant that reaches around the
# deny list is worth more than the deny list, and a prefix rule cannot exclude
# a flag: see git-switch-existing.sh, where the flags were shown to combine.
#
# `--no-track` is part of the fixed command rather than a caller's choice. The
# start point is a remote-tracking ref, so without it the new branch's upstream
# becomes origin/main and /pr never sets the right one. Checked at the pin:
# `git worktree add -h` lists `--[no-]track`, and a real add with it produced a
# branch with no upstream.
#
# origin/main is fixed for the same reason. Step 5 forks only from the fetched
# base, and a caller-supplied start point would be one more thing to validate
# for the sake of a case this command does not have.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-worktree-fork.sh <path> <branch>" >&2; exit 2; }
path="$1"
branch="$2"
# The checks below read `git rev-parse` inside `[ ]`, where a failure is an
# empty string that `set -e` never sees, so the repository is established first.
git rev-parse --git-dir >/dev/null 2>&1 ||
  { echo "not in a git repository" >&2; exit 2; }
# .claude/worktrees/<name>, which is the only shape step 5 creates: the one
# location EnterWorktree moves the session into without a confirmation no
# allow rule can pre-approve. Enforced here as well as there so the helper
# reads safely on its own terms: neither argument can begin with '-', so
# nothing a caller passes arrives as a flag.
[[ "$path" =~ ^\.claude/worktrees/[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be .claude/worktrees/<name>" >&2; exit 2; }
# The path is relative, so it means the main checkout's directory only when
# run from the main checkout's root. From a linked worktree it would nest a
# second worktree inside the first, which /branch step 0 refuses.
[ -z "$(git rev-parse --show-prefix)" ] ||
  { echo "run from the checkout root" >&2; exit 2; }
[ "$(git rev-parse --git-dir)" = "$(git rev-parse --git-common-dir)" ] ||
  { echo "run from the main checkout, not a linked worktree" >&2; exit 2; }
# A worktree inside the checkout is untracked content of it unless ignored,
# and every `git status` the chain reads — grok-review.sh's clean-tree
# refusal, /commit's unscoped sweep — would then see it.
git check-ignore -q "$path" ||
  { echo "$path is not ignored — add .claude/worktrees/ to .gitignore" >&2; exit 2; }
[ ! -e "$path" ] || { echo "path already exists: $path" >&2; exit 2; }
# Branch names here are <type>/<kebab>, and feat(scope)/ carries parentheses.
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# It must NOT exist: this helper only ever creates. Refusing here is what makes
# the missing -B harmless rather than merely unavailable — a caller who wanted
# to reset a branch cannot get there by passing its name.
! git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "branch already exists: $branch" >&2; exit 3; }
git show-ref --verify --quiet refs/remotes/origin/main ||
  { echo "no refs/remotes/origin/main — fetch first (step 1)" >&2; exit 4; }
git worktree add --no-track -b "$branch" "$path" origin/main
# Seed the new worktree's code index now rather than at its first edit: /branch
# enters it mid-session, where no `SessionStart` fires, and a query there before
# any edit would build from scratch. The worktree's own hook does the work,
# silenced as its hook entries are; with no event on stdin it refreshes this
# directory, and it detaches its own refresh, so the `&` only spares a wait.
(cd "$path" && sh .claude/hooks/refresh-index.sh) </dev/null >/dev/null 2>&1 &
