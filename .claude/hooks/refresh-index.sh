#!/usr/bin/env sh
# Refresh the local codebase index after an edit, coalescing overlapping calls.
#
# **Run from the `PostToolUse` hook in `.claude/settings.json`, backgrounded
# and silenced there** so it can never block an edit; that is also why nothing
# here may fail loudly — nobody would hear it. `test_index_refresh_hook.py`
# runs this file against a fake CLI for the same reason.
#
# **Every edit starts one of these, and a detached `update` per edit could miss
# the newest one.** An updater that scanned before edit B, and a second updater
# that lost to the first on the index's lock, would leave B unindexed with both
# outcomes discarded. So each call marks the index pending and only one call at
# a time runs `update`, going round again whenever the mark reappeared while it
# ran: whichever edit comes last, an `update` that started after it runs.
# `update` re-scans by mtime and hash, so one pass after the last edit covers
# every edit before it.
#
# **`mkdir` is the lock because it is atomic on every platform the harness CI
# runs**, and it needs no `flock`, which macOS and Git for Windows lack. The
# check after `rmdir` is the whole of the coalescing: a mark made while the
# holder ran is seen there, and a mark made after it comes from a call that
# then finds the lock free and takes it itself.
#
# **A lock older than ten minutes is taken to be a killed holder's.** Left, it
# would stop every later refresh without a sound. An `update` of this tree
# takes seconds; `index`, which can take longer, never runs through here.
#
# **`CBX_NO_SKILL_AUTO_UPDATE=1` is the guard `.mcp.json` sets**, and it is not
# optional: without it the CLI may rewrite the tracked skill, widening its
# `allowed-tools`, which one unguarded run was measured doing.
set -u

root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$root" || exit 0

# No index yet — a fresh worktree — means nothing to refresh; `index` builds it.
cache=.claude/cache/codebase-index
[ -d "$cache" ] || exit 0

pending="$cache/refresh.pending"
lock="$cache/refresh.lock"

find "$cache" -maxdepth 1 -name refresh.lock -type d -mmin +10 \
  -exec rmdir {} \; 2>/dev/null

: > "$pending"
while mkdir "$lock" 2>/dev/null; do
  rm -f "$pending"
  CBX_NO_SKILL_AUTO_UPDATE=1 codebase-index update
  rmdir "$lock"
  [ -e "$pending" ] || break
done
exit 0
