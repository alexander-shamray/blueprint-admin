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
# one replaces this shell.
#
# **A closed set of hook names, like every helper in `.claude/scripts/`.**
# Its callers are hook wirings — `settings.json` and an agent profile's
# `hooks:` — and each names one of the files in the `case` below; a launcher
# taking any path would be a way to run an arbitrary script through the hook
# wiring, which is the shape the fixed-endpoint rule exists to refuse.
set -eu

[ "$#" -eq 1 ] ||
  { echo "usage: run-guard.sh <guard-git-argv.py|guard-edit-target.py|guard-triager-dispatch.py>" >&2; exit 2; }

case "$1" in
  guard-git-argv.py|guard-edit-target.py|guard-triager-dispatch.py) ;;
  *) echo "run-guard.sh: not a hook this launcher runs: $1" >&2; exit 2 ;;
esac

# Resolved from this file rather than taken from the caller: every guard it
# runs sits beside it, so the launcher and the module it runs cannot come from
# different checkouts. `CDPATH=` because a `CDPATH` set in the environment makes `cd`
# print the directory it chose and land somewhere else.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# **A name on PATH is not a working interpreter, so each candidate is RUN
# before it is chosen.** `command -v` alone picked a `py` with no 3.12
# registered while a good `python3` sat beside it, and on Windows it picks the
# Store `python3` alias whenever `py` is absent — in both cases the `exec`
# failed and no fallback was ever reached. The probe is a harmless `-c` that
# exits non-zero below the 3.12 floor, and it judges no event, so the hook
# itself still runs exactly once. Raised by Copilot.
probe='import sys; sys.exit(sys.version_info < (3, 12))'

if command -v py >/dev/null 2>&1 && py -3.12 -c "$probe" >/dev/null 2>&1; then
  exec py -3.12 "$dir/$1"
fi
# `py -3` after the exact selector: a Windows host with only 3.13 registered has
# no `-3.12` and satisfies the floor all the same, so the generic selector is
# probed with the same version check rather than the host being refused.
# Raised by Copilot.
if command -v py >/dev/null 2>&1 && py -3 -c "$probe" >/dev/null 2>&1; then
  exec py -3 "$dir/$1"
fi
# `python` after `python3`, because `docs/testing.md` lets a host expose 3.12 as
# either, and a POSIX host with only `python` otherwise failed every guarded
# call before the guard ran. Raised by Copilot.
if command -v python3 >/dev/null 2>&1 && python3 -c "$probe" >/dev/null 2>&1; then
  exec python3 "$dir/$1"
fi
if command -v python >/dev/null 2>&1 && python -c "$probe" >/dev/null 2>&1; then
  exec python "$dir/$1"
fi
# **Exit 2, because it is the only code that blocks.** A `PreToolUse` hook
# that exits with anything else is a non-blocking error: the harness reports
# it and runs the tool unguarded. The unprobed last `exec` this replaces would
# have run a 3.11 `python`, or failed with 127 and let the call through —
# neither the loud refusal the header promises. Raised by Copilot.
echo "run-guard.sh: no Python 3.12 or newer found as py -3.12, py -3, python3 or python; refusing the call rather than running it unguarded" >&2
exit 2
