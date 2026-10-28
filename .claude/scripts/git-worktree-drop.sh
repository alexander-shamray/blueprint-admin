#!/usr/bin/env bash
# Remove the throwaway worktree a sweep created — /security-sweep or
# /bug-sweep — without -f.
#
# The missing flag is half the point. A sweep's teardown leans on git
# refusing to remove a checkout holding anything modified or untracked — "if
# `git worktree remove` refuses, leave the worktree standing and report what it
# is holding" — and `git worktree remove -f` defeats exactly that guard, which
# a `Bash(git worktree remove:*)` grant would license.
#
# The other half is WHICH path. Registration is not ownership: every PR
# worktree is registered too, and the audited tree is prompt-injection input, so
# a path arriving here may have been chosen by it. Accepting any non-main
# worktree would let a poisoned finding steer this at someone else's clean
# workspace. So the path must match `secsweep-` plus six characters under the
# canonical temp root.
#
# That EXCLUDES PR worktrees and anything outside the temp root, which
# is what it is for.
#
# It is a **direct-child** check now, and it was not before: the old form was one
# `case "$resolved" in "$tmproot"/secsweep-??????)`, and a bash `case` does no
# pathname expansion, so `?` matches `/` like any other character and
# `$tmproot/secsweep-a/bbbb` passed as readily as `$tmproot/secsweep-abc123`.
# Verified by running both through a `case`, with wrong-length, wrong-prefix and
# wrong-root controls all correctly refused — prefix and length held,
# direct-childness did not. Comparing `dirname` against the root and matching
# the basename alone is what fixes it, because a basename contains no `/`.
#
# It still does NOT mean a sweep created this path, and the two helpers differ
# on why — a distinction a revision of this comment briefly erased.
# `git-worktree-detach.sh` now creates the directory it hands to git, so for
# THAT helper the question does not arise: there is no caller-supplied path to
# doubt, and the `Bash(mktemp:*)` grant that made an arbitrary template
# reachable is gone from both sweeps. **This helper is the other case and is
# unchanged in that respect.** The teardown passes it `$posix`, and any
# registered worktree of the right shape satisfies the checks below — including
# one an abandoned earlier sweep left behind, which is not hypothetical: a stray
# `secsweep-` checkout from a previous session was sitting in the temp root
# while this was being written.
#
# `git-worktree-detach.sh` does meet the question once, from this side: before
# it forks, it hands this helper each day-old sweep-shaped worktree git lists
# for the repository, which is exactly the registered, right-shaped and not
# provably-ours case above, and it leans on these checks to decide (#577).
#
# So exclusion is the load-bearing half here and ownership is not proved.
# Generalising the detach helper's new property to this one would retire a
# residual by asserting it away, which is the same mistake as answering "what is
# it owed" by inference rather than by reading.
#
# The shape is checked here rather than assumed for a separate reason: a helper
# whose guard holds only while the other helper is unedited has no guard.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-drop.sh <path>" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
# Ask git whether this is a linked worktree of THIS repository, rather than
# comparing path strings against `git worktree list`. Under MSYS those strings
# are not comparable at all — git prints `C:/Users/…/Temp/x` where `pwd -P`
# prints `/tmp/x`, and a textual check refuses the sweep's own worktree on the
# host this repository is developed on. Both values below come from git in one
# format, so they compare.
common_here=$(git rev-parse --path-format=absolute --git-common-dir)
common_there=$(git -C "$path" rev-parse --path-format=absolute --git-common-dir 2>/dev/null) ||
  { echo "not a git worktree: $path" >&2; exit 3; }
[ "$common_there" = "$common_here" ] ||
  { echo "not a worktree of this repository: $path" >&2; exit 3; }
dir_there=$(git -C "$path" rev-parse --path-format=absolute --git-dir)
[ "$dir_there" != "$common_there" ] ||
  { echo "refusing to remove the main worktree: $path" >&2; exit 3; }
#
# **A refusal and a half-finished removal are different failures (#329).** git unregisters
# the worktree and then deletes its tree; on Windows a process holding the tree makes the
# delete fail after the worktree is gone from the register, and with `set -e` the `.slices`
# lists below were never reached either: 41 `secsweep-*` directories had accumulated. When
# git fails, the register is asked again: still registered means git refused (modified or
# untracked work), and the failure stands; not registered means only the directory is left,
# and it is deleted here for a bounded time, since the path passed the shape and same-repository
# checks above (ownership is not proved here, as the header says).
removed_status=0
git worktree remove "$resolved" || removed_status=$?
if [ "$removed_status" -ne 0 ]; then
  # **Unregistered is established twice and positively; any doubt is "still registered".** The
  # listing must run, name at least the main worktree, and not name this path; and git itself
  # must no longer recognise the directory as a worktree. A failed listing, an empty one or a
  # path-form mismatch must never read as "gone", because the next step deletes a tree.
  listing=$(git worktree list --porcelain) || exit "$removed_status"
  [ -n "$listing" ] || exit "$removed_status"
  still=no
  while IFS= read -r line; do
    case "$line" in
      "worktree "*)
        dir="${line#worktree }"
        [ -d "$dir" ] && [ "$(cd "$dir" && pwd -P)" = "$resolved" ] && still=yes
        ;;
    esac
  done <<<"$listing"
  [ "$still" = no ] || exit "$removed_status"
  # Asked of the directory itself, and compared git to git (never against a path string, which
  # differs in form under MSYS and on a case-folding volume): a leftover with no `.git` resolves
  # to the checkout that contains it, whose git dir IS the common dir; one whose `.git` points
  # at a removed admin directory makes git fail. A live linked worktree answers with its own
  # admin directory, which is not the common dir, and is kept.
  if there=$(git -C "$resolved" rev-parse --path-format=absolute --git-dir 2>/dev/null); then
    here=$(git rev-parse --path-format=absolute --git-common-dir) || exit "$removed_status"
    [ "$there" = "$here" ] || exit "$removed_status"
  fi
  tries="${CLAUDE_WORKTREE_REMOVE_TRIES:-18}"
  pause="${CLAUDE_WORKTREE_REMOVE_PAUSE:-5}"
  [[ "$tries" =~ ^[0-9]+$ && "$pause" =~ ^[0-9]+$ ]] ||
    { echo "CLAUDE_WORKTREE_REMOVE_TRIES and _PAUSE must be whole numbers" >&2; exit 2; }
  for ((attempt = 1; attempt <= tries; attempt++)); do
    rm -rf -- "$resolved" 2>/dev/null || true
    [ ! -e "$resolved" ] && break
    [ "$attempt" -lt "$tries" ] && sleep "$pause"
  done
  [ ! -e "$resolved" ] ||
    { echo "$path is unregistered but a process still holds it; delete it by hand" >&2; exit 1; }
  echo "removed $path after git left it behind"
fi
# The slice lists sweep-slices.sh wrote beside it; named from the checked path,
# never passed, and only once git has removed the worktree itself.
slices="$resolved.slices"
if [ -d "$slices" ] && [ ! -L "$slices" ]; then rm -rf -- "$slices"; fi
