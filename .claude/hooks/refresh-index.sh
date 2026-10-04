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
# **A linked worktree with no index is seeded from its main checkout's, and
# never built.** `.claude/cache/` is ignored, so `/branch`'s worktree starts
# with none, and a full `index` is minutes inside an agent's turn where a copy
# and an `update` are seconds. Only `index.sqlite` crosses — the main
# checkout's other cache files are its own state, a customised `config.json`
# included — and it crosses inside the winning call, so a burst seeds once. A
# main checkout with no index leaves the worktree with none, and `update`
# refuses to run without one, so nothing here builds from scratch.
# `git-worktree-fork.sh` also runs this once in each new worktree, because
# `/branch` enters one mid-session, where no `SessionStart` fires.
#
# **`CBX_NO_SKILL_AUTO_UPDATE=1` is the guard `.mcp.json` sets**, and it is not
# optional: without it the CLI may rewrite the tracked skill, widening its
# `allowed-tools`.
set -u

root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$root" || exit 0

cache=.claude/cache/codebase-index
index="$cache/index.sqlite"
# A seed no `update` has opened yet, which is suspect until one does.
suspect="$index.seeded"

# A linked worktree with no index takes its main checkout's or nothing: the
# git dir differs from the common one, and the common one is a checkout's
# `.git` rather than a bare repository's. A cache directory left without an
# index gets nothing either, since `update` would only refuse it three times.
seed=
if [ ! -f "$index" ]; then
  git_dir=$(git rev-parse --path-format=absolute --git-dir 2>/dev/null) || exit 0
  common=$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null) || exit 0
  if [ "$git_dir" != "$common" ]; then
    [ "${common##*/}" = .git ] && [ -f "${common%/*}/$index" ] || exit 0
    seed="${common%/*}/$index"
    mkdir -p "$cache" || exit 0
  fi
fi

# No cache in a checkout means nothing to refresh; `index` builds it.
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

# Copied beside it and renamed in, so `update` never opens a partial file, and
# skipped when an earlier winner seeded during the wait. A WAL or shared-memory
# file left from an earlier seed would be replayed into this one, so they go.
# The marker is laid first, so no seed is ever in place without it, and on a
# failure it goes only if no index arrived: another seeder's may have.
if [ -n "$seed" ] && [ ! -e "$index" ]; then
  rm -f "$index-wal" "$index-shm"
  cp "$seed" "$index.seed.$token" && : > "$suspect" &&
    mv -f "$index.seed.$token" "$index" ||
    { rm -f "$index.seed.$token"; [ -e "$index" ] || rm -f "$suspect"; exit 0; }
fi

tries=0
until refresh; do
  tries=$((tries + 1))
  if [ "$tries" -ge 3 ]; then
    # A seed no `update` has opened — torn by a copy taken mid-checkpoint —
    # goes, so the next call copies again instead of every call failing on it.
    # The marker says it is a seed, whichever call made it; only the published
    # caller acts on it, because a superseded one may be failing on contention
    # with the call now updating this very file. The marker goes last, and only
    # once the files are gone: an index another process holds open survives the
    # `rm` on Windows, and must not survive it unmarked.
    [ ! -e "$suspect" ] || [ "$(cat "$pending" 2>/dev/null)" != "$token" ] ||
      { rm -f "$index" "$index-wal" "$index-shm" && rm -f "$suspect"; }
    exit 0
  fi
  sleep 2
done
# An `update` that ran over the seed vouches for it.
rm -f "$suspect"
exit 0
