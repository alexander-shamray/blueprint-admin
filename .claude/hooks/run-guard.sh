#!/usr/bin/env sh
# Run one guard hook with whichever Python this host actually has, exactly once.
#
# **`py -3.12` is the Windows launcher and both hooks were wired to it (#23).**
# `py` ships with Python on Windows and nowhere else: a standard 3.12 on macOS
# or Linux provides `python3` and no `py`, so the command could not start and
# every `Bash`, `Edit` and `Write` call failed before the guard ran. Loud and
# total rather than silent, which is the right direction, and still unusable.
#
# **The order is measured rather than preferred, and it is the whole of what
# this file decides.** On Windows `python3` is present on PATH and is NOT
# Python: it is the Microsoft Store's execution alias, which prints "Python was
# not found; run without arguments to install from the Microsoft Store" and
# exits non-zero. So a launcher that probed `python3` first would find
# something on every host and the wrong thing on this one. `py` is probed
# first, exists only where it is right, and `python3` is what is left.
#
# **One `exec`, never a fallback chain.** `py -3.12 … || python3 …` re-runs the
# hook whenever the first invocation exits non-zero for a real reason — and for
# a `PreToolUse` hook a real reason includes printing a deny, so a refusal
# would be emitted twice and the second interpreter would judge the event a
# second time. The interpreter is chosen before anything runs, and the chosen
# one replaces this shell. The remembered branch below is that same rule with
# the choice already made: it execs, or it falls through to the probe.
#
# **A closed set of hook names, like every helper in `.claude/scripts/`.**
# Its callers are hook wirings — `settings.json` and an agent profile's
# `hooks:` — and each names one of the files in the `case` below; a launcher
# taking any path would be a way to run an arbitrary script through the hook
# wiring, which is the shape the fixed-endpoint rule exists to refuse.
set -eu

[ "$#" -eq 1 ] ||
  { echo "usage: run-guard.sh <guard-git-argv.py|guard-edit-target.py|guard-triager-dispatch.py|guard-triager-edit.py>" >&2; exit 2; }

case "$1" in
  guard-git-argv.py|guard-edit-target.py|guard-triager-dispatch.py|guard-triager-edit.py) ;;
  *) echo "run-guard.sh: not a hook this launcher runs: $1" >&2; exit 2 ;;
esac

# Resolved from this file rather than taken from the caller: every guard it
# runs sits beside it, so the launcher and the module it runs cannot come from
# different checkouts. `CDPATH=` because a `CDPATH` set in the environment makes `cd`
# print the directory it chose and land somewhere else.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# **The probe's answer is a property of the host, not of the event, so it is
# paid once and remembered.** Every `Bash`, `Edit` and `Write` call in every
# session ran a throwaway interpreter before the guard's own (#44). The mark
# below removes that one, and everything that reads it is a shell builtin, so
# the remembered path forks nothing at all.
#
# **It is validated rather than trusted, because a stale mark is exactly the
# case the probe exists to catch** — and a mark believed wrongly ends with an
# `exec` that fails, which for a `PreToolUse` hook means the tool runs
# unguarded. Three conditions, none of them a process: the `PATH` it was
# chosen under is the `PATH` now, because which candidate wins is a property
# of `PATH` and of nothing else; the interpreter's own executable is still
# there and still executable; and it has not been rewritten since, so an
# in-place upgrade, or a 3.12 uninstalled out from under a `py` that remains,
# both move its mtime past the mark's.
#
# **The mark names one of four spellings and can name nothing else.** It sits
# under `.claude/cache/`, which no `Edit(...)` deny covers, and the command it
# names is word-split into an `exec`; without the closed set below, a file the
# guarded session may write would be a command this launcher runs before every
# guarded tool call.
cache="$dir/../cache/run-guard"
mark="$cache/interpreter"
chosen=''
if [ -r "$mark" ] &&
   { read -r chosen; read -r interpreter; read -r under; } < "$mark"; then
  case "$chosen" in
    'py -3.12'|'py -3'|python3|python) ;;
    *) chosen='' ;;
  esac
  if [ -n "$chosen" ] && [ "$under" = "$PATH" ] && [ -x "$interpreter" ] &&
     [ ! "$interpreter" -nt "$mark" ]; then
    exec $chosen "$dir/$1"
  fi
fi

# Written beside the mark and moved onto it, so a call reading it while
# another writes sees one or the other whole. Best effort and silent
# throughout: a cache that cannot be written costs the next call a probe, and
# must never cost this call the guard it is here to run.
remember() {
  # A candidate that answered the probe without naming an interpreter — a
  # stand-in, a stub, anything that is not Python — would leave a mark that
  # can never validate, and so would cost a write on every guarded call and
  # buy nothing. The suite's own stand-ins are that case.
  [ -n "$2" ] || return 0
  mkdir -p "$cache" 2>/dev/null || return 0
  if printf '%s\n%s\n%s\n' "$1" "$2" "$PATH" > "$mark.$$" 2>/dev/null; then
    mv -f "$mark.$$" "$mark" 2>/dev/null || :
  fi
  # Unconditionally: a rename that loses a race to a concurrent call
  # otherwise leaves its staging file sitting in the cache directory.
  rm -f "$mark.$$" 2>/dev/null || :
  return 0
}

# **A name on PATH is not a working interpreter, so each candidate is RUN
# before it is chosen.** `command -v` alone picked a `py` with no 3.12
# registered while a good `python3` sat beside it, and on Windows it picks the
# Store `python3` alias whenever `py` is absent — in both cases the `exec`
# failed and no fallback was ever reached. The probe is a harmless `-c` that
# exits non-zero below the 3.12 floor, and it judges no event, so the hook
# itself still runs exactly once. Raised by Copilot.
#
# It prints `sys.executable` as well, which is the file the mark is validated
# against: `py -3.12` is a launcher rather than an interpreter, so its own
# mtime says nothing about the 3.12 it dispatches to, and a candidate that
# prints nothing — a stand-in, a stub — simply never gets a usable mark.
probe='import sys; sys.stdout.write(sys.executable); sys.exit(sys.version_info < (3, 12))'

if command -v py >/dev/null 2>&1 && interpreter=$(py -3.12 -c "$probe" 2>/dev/null); then
  remember 'py -3.12' "$interpreter"
  exec py -3.12 "$dir/$1"
fi
# `py -3` after the exact selector: a Windows host with only 3.13 registered has
# no `-3.12` and satisfies the floor all the same, so the generic selector is
# probed with the same version check rather than the host being refused.
# Raised by Copilot.
if command -v py >/dev/null 2>&1 && interpreter=$(py -3 -c "$probe" 2>/dev/null); then
  remember 'py -3' "$interpreter"
  exec py -3 "$dir/$1"
fi
# `python` after `python3`, because `docs/testing.md` lets a host expose 3.12 as
# either, and a POSIX host with only `python` otherwise failed every guarded
# call before the guard ran. Raised by Copilot.
if command -v python3 >/dev/null 2>&1 && interpreter=$(python3 -c "$probe" 2>/dev/null); then
  remember python3 "$interpreter"
  exec python3 "$dir/$1"
fi
if command -v python >/dev/null 2>&1 && interpreter=$(python -c "$probe" 2>/dev/null); then
  remember python "$interpreter"
  exec python "$dir/$1"
fi
# **Exit 2, because it is the only code that blocks.** A `PreToolUse` hook
# that exits with anything else is a non-blocking error: the harness reports
# it and runs the tool unguarded. The unprobed last `exec` this replaces would
# have run a 3.11 `python`, or failed with 127 and let the call through —
# neither the loud refusal the header promises. Raised by Copilot.
echo "run-guard.sh: no Python 3.12 or newer found as py -3.12, py -3, python3 or python; refusing the call rather than running it unguarded" >&2
exit 2
