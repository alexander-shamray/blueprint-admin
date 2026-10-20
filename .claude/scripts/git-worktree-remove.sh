#!/usr/bin/env bash
# Remove the worktree `/branch` step 5 forked, and nothing else.
#
# **`Bash(git worktree remove:*)` is a PREFIX grant, so a trailing `-f` is
# inside it (#16)** — and `-f` is precisely the refusal that matters here.
# `ship.md` step 8 relies on `git worktree remove` declining a dirty worktree:
# that refusal is what stops the teardown from deleting work the run did not
# commit, and the step's own text says the directory is left for inspection
# when it fires. A grant that admits the flag which defeats it leaves the
# guarantee resting on the model not typing three characters.
#
# The path shape is `git-worktree-fork.sh`'s, because the fork is the only
# thing that creates what this removes: `.claude/worktrees/<name>`.
# Neither the sweeps' detached worktrees nor anything else reaches this —
# `git-worktree-drop.sh` owns those, with its own shape check.
#
# **Registration is not ownership, and the first form took it as ownership.**
# `git-worktree-drop.sh` says so of its own paths; this helper checked only
# that the path was a registered worktree of the right shape, so a prefix
# grant with a caller-chosen path could remove ANY clean forked worktree —
# someone else's PR checkout included. So the caller names the branch the run
# forked too, and the worktree registered at that path must have that branch
# checked out, which is the pair `git-worktree-fork.sh <path> <branch>` wrote.
# Raised by Copilot.
#
# **What that does not reach**: a caller naming another run's path AND its
# branch together passes. `/ship` passes the branch it created, recorded at
# step 1, so what is left is a caller that already knows both names — not a
# path a poisoned string can pick blind.
set -euo pipefail
[ "$#" -eq 2 ] ||
  { echo "usage: git-worktree-remove.sh <path> <branch>" >&2; exit 2; }
path="$1"
branch="$2"

# The same grammar the fork helper writes with, so nothing can be removed that
# this repository's own commands did not create — and neither `-f` nor any
# other flag can arrive, since neither argument can begin with `-`.
[[ "$path" =~ ^\.claude/worktrees/[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be .claude/worktrees/<name>" >&2; exit 2; }
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] && [[ "$branch" != *..* ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
[ "$branch" != main ] ||
  { echo "main is never a forked branch" >&2; exit 2; }
# **No slug is derived from the branch here, and the first form derived one.**
# `/branch` cuts the directory name to the first word or two of the change —
# `feat(template)/masstransit-registration` forks
# `.claude/worktrees/masstransit` — so requiring the full branch basename
# refused every such worktree and `/ship`'s teardown with it. The registered
# worktree holding the named branch, checked below, is the binding. Raised by
# Copilot.
[ -d "$path" ] || { echo "no such directory: $path" >&2; exit 3; }

# It must be a worktree of THIS repository, not merely a directory with the
# right name, and it must hold the named branch. `git worktree list` is the
# register, and its porcelain puts each `worktree` line before that
# worktree's `branch` line.
resolved=$(cd "$path" && pwd -P)
registered=$(git worktree list --porcelain)
found=no
current=
# Read in THIS shell, not through a pipe: a `while` on the right of a pipe runs
# in a subshell, so a flag set inside it is lost and the check would answer
# `no` on every path — refusing the teardown it exists to permit.
while IFS= read -r line; do
  case "$line" in
    "worktree "*)
      current=
      dir="${line#worktree }"
      [ -d "$dir" ] && [ "$(cd "$dir" && pwd -P)" = "$resolved" ] && current=yes
      ;;
    "branch "*)
      [ "$current" = yes ] && [ "${line#branch }" = "refs/heads/$branch" ] && found=yes
      ;;
  esac
done <<<"$registered"
[ "$found" = yes ] ||
  { echo "not the worktree of this repository holding ${branch}: $resolved" >&2; exit 3; }

# No `--force`. The refusal on a dirty worktree is the point of this endpoint,
# and `ship.md` step 8 reports the directory as left for inspection when it
# fires rather than treating it as an error to get past.
#
# **A refusal and a half-finished removal are different failures, and on Windows only the
# first was handled (#329).** git unregisters the worktree and then deletes its tree; a
# process holding `<worktree>/.claude` (measured here: it let go about 40 s after a build,
# with the build servers already shut down) makes the delete fail with `Permission denied`
# or `Invalid argument` after the worktree is gone from the register. Leaving that
# directory behind is how `.claude/worktrees` and the temp root accumulated remnants. So
# when git fails, the register is asked again: still registered means git refused (a dirty
# tree), and the failure stands; not registered means only the directory is left, and it
# is deleted here, retrying for a bounded time, since the path matched the fork helper's
# shape and was registered to the named branch above.
if ! git worktree remove "$path"; then
  still=no
  while IFS= read -r line; do
    case "$line" in
      "worktree "*)
        dir="${line#worktree }"
        [ -d "$dir" ] && [ "$(cd "$dir" && pwd -P)" = "$resolved" ] && still=yes
        ;;
    esac
  done <<<"$(git worktree list --porcelain)"
  [ "$still" = no ] || exit 1
  tries="${CLAUDE_WORKTREE_REMOVE_TRIES:-18}"
  pause="${CLAUDE_WORKTREE_REMOVE_PAUSE:-5}"
  for ((attempt = 1; attempt <= tries; attempt++)); do
    rm -rf -- "$resolved" 2>/dev/null || true
    [ ! -e "$resolved" ] && break
    sleep "$pause"
  done
  [ ! -e "$resolved" ] ||
    { echo "$path is unregistered but a process still holds it; delete it by hand" >&2; exit 1; }
  echo "removed $path after git left it behind"
fi
