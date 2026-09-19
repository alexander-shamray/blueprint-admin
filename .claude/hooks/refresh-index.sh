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
# check after release is the whole of the coalescing: a mark made while the
# holder ran is seen there, and a mark made after it comes from a call that
# then finds the lock free and takes it itself.
#
# **A lock is taken over only when it is older than ten minutes AND its holder
# is gone** — `kill -0` on the PID the token leads with fails, or no token was
# ever written. Left, a killed holder's lock would stop every later refresh
# without a sound; but age alone would also displace a live holder inside a
# slow `update` and start a second one beside it, so age is only when to ask.
# `kill -0` was measured across separately launched shells on Git for Windows:
# alive while the holder runs, dead once it and any orphaned child are gone.
# The takeover is a rename, which only one caller can win.
#
# **Each holder releases the lock only while its token is still in it.** A
# holder in its release is alive, so nothing takes its lock over; the check is
# for the one race left — two callers reclaiming the same dead lock at the same
# moment — where it keeps the loser from freeing the winner's lock. The cost
# of that race is at most one missed refresh, which the skill's per-query
# `stale: true` check repairs.
#
# **A failed `update` is retried, up to three attempts a second apart.** The
# pending mark is already cleared by then and the hook discards the status, so
# without a retry a transient failure — the index database's own five-second
# busy timeout is the likely one — would leave the edit unindexed until the
# next edit. Three, because a failure that survives them is not transient,
# and a holder that retried for ever would hold the lock for ever.
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
token="$$.$(date +%s)"

refresh() {
  CBX_NO_SKILL_AUTO_UPDATE=1 codebase-index update
}

stale=$(find "$lock" -prune -type d -mmin +10 2>/dev/null)
if [ -n "$stale" ]; then
  holder=$(cat "$lock/owner" 2>/dev/null)
  if [ -z "$holder" ] || ! kill -0 "${holder%%.*}" 2>/dev/null; then
    mv "$lock" "$lock.stale.$token" 2>/dev/null &&
      rm -rf "$lock.stale.$token"
  fi
fi

: > "$pending"
while mkdir "$lock" 2>/dev/null; do
  printf '%s\n' "$token" > "$lock/owner"
  rm -f "$pending"
  tries=0
  until refresh; do
    tries=$((tries + 1))
    [ "$tries" -lt 3 ] || break
    sleep 1
  done
  [ "$(cat "$lock/owner" 2>/dev/null)" = "$token" ] || exit 0
  rm -rf "$lock"
  [ -e "$pending" ] || break
done
exit 0
