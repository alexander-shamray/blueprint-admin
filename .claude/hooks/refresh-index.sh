#!/usr/bin/env sh
# Refresh the local codebase index, coalescing a burst of calls.
#
# **Run from two hooks in `.claude/settings.json` — `PostToolUse` after every
# tool that writes, and `SessionStart` for the changes no edit makes —
# backgrounded and silenced in both** so it can never block an edit, and so a
# session start hears nothing from it; that is also why nothing here may fail
# loudly — nobody would hear it. It reads no event payload, resolving its
# repository from the working directory below, which is what lets one command
# serve both events. `test_index_refresh_hook.py` runs this file against a
# fake CLI for the same reason.
#
# **The rule is that the last call runs, and there is no lock.** Each call
# publishes a token of its own to `refresh.pending` by an atomic rename, waits,
# and runs `update` only if its token is still the one published. The last
# call to publish therefore always runs, and its `update` starts after every
# edit whose call published before it — `update` re-scans by mtime and hash, so
# that one pass covers them all. Every earlier call exits, because a later one
# is certain to run. A lock would have to be recovered from a holder that died
# holding it, and every recovery rule is a new interleaving; this has none.
#
# **Two updates overlap only when edits land further apart than the wait**, and
# then the index database serialises them with its own busy timeout. An
# `update` that still fails is retried, up to three attempts two seconds apart:
# the caller discards the status, so without a retry a transient failure would
# leave the last edit unindexed.
#
# **`CBX_NO_SKILL_AUTO_UPDATE=1` is the guard `.mcp.json` sets**, and it is not
# optional: without it the CLI may rewrite the tracked skill, widening its
# `allowed-tools`.
set -u

root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$root" || exit 0

# No index yet — a fresh worktree — means nothing to refresh; `index` builds it.
cache=.claude/cache/codebase-index
[ -d "$cache" ] || exit 0

pending="$cache/refresh.pending"
token="$$.$(date +%s)"

refresh() {
  CBX_NO_SKILL_AUTO_UPDATE=1 codebase-index update
}

printf '%s\n' "$token" > "$pending.$token" &&
  mv -f "$pending.$token" "$pending" ||
  { rm -f "$pending.$token"; exit 0; }

sleep 2
[ "$(cat "$pending" 2>/dev/null)" = "$token" ] || exit 0

tries=0
until refresh; do
  tries=$((tries + 1))
  [ "$tries" -lt 3 ] || break
  sleep 2
done
exit 0
