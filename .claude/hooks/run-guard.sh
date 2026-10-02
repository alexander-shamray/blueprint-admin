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
#
# **The probe is paid once rather than once per call, and the mark that
# remembers it holds nothing (#44).** Running a candidate before choosing it
# is a second interpreter start on every `Bash`, `Edit` and `Write` call, and
# its answer is a property of the host, not of the event. So the first call
# leaves a mark saying that the candidate probed first passed, and a later
# call that finds the mark skips the probe. The mark lives under
# `.claude/cache/`, which the session being guarded can write, so it is an
# empty directory named for that candidate and this file never opens it:
# there is no content to choose a program with, nothing to write through and
# nothing to block on. Whether it exists is all it says, and the name asked
# about is worked out here. A forged one buys a skipped probe of the program
# the probe would have run.
#
# **A remembered interpreter runs as a child and has to prove it judged,
# because an `exec` cannot fail closed.** Once `exec` has replaced this shell
# nothing is left to notice that the interpreter was uninstalled, repointed
# below the floor, or was the Store alias all along: it exits 103, 49 or 127,
# none of them 2, and the tool runs unguarded. So the remembered candidate
# runs `run-guard.py`, which checks the floor in the interpreter that is about
# to judge, runs the guard there, and answers with one of three statuses that
# nothing else produces. Any other status — 0 included — is the call refused
# and the mark forgotten, so the next call probes again. Never a second
# candidate in the same call: the first may have read the event, and the two
# guards `settings.json` wires allow an event they cannot read.
#
# **What that costs is one refused call when the host changes under a mark**,
# where the probe moved on to the next candidate unnoticed. A mark that cannot
# be removed, because something was put inside it, refuses every call until
# somebody removes it, and says so. And a host whose first candidate is not
# its working one — a `py` with no 3.12 registered, the Store alias with no
# `py` — is never remembered: it pays the probe on every call, as before,
# because a mark that could name a later candidate would be a mark that
# chooses.
set -eu

# **From here this shell leaves with 2 unless a guard's verdict says
# otherwise.** `set -e`, an unset variable and a refused redirection each end
# a shell with a status that is not reliably 2 — 1 under `bash`, 2 under
# `dash` — and so does a refusal printed to a stderr nobody is reading,
# which arrives as SIGPIPE. A `PreToolUse` hook reads every such status as
# leave to run the tool. The two ways past these lines are an `exec`, after
# which the guard's status is the hook's, and a proven verdict below, which
# lifts the trap itself. A signal sent from outside is not covered: it ends
# this shell as it would end the guard.
trap 'exit 2' EXIT
trap 'exit 2' PIPE

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

# The candidate the probes below reach first on this host, found the way they
# find it, and the name its mark goes by. It is the only candidate a mark may
# speak for, and both come from this file.
if command -v py >/dev/null 2>&1; then first='py -3.12'; mark=py-3.12
elif command -v python3 >/dev/null 2>&1; then first=python3; mark=python3
elif command -v python >/dev/null 2>&1; then first=python; mark=python
else first=; mark=none
fi
mark="$dir/../cache/run-guard.$mark"

# **A directory, because a directory is never opened.** A file would have to
# be read, and what a session can leave at a path is not only text: a FIFO
# blocks whoever opens it, a file with no newline keeps `read` reading, and a
# hook that hangs is timed out, which does not block the tool. A link would be
# written through. `mkdir` and `rmdir` do none of that, and `[ -d ]` asks only
# what the name is.
#
# So the mark carries no `PATH` and nothing else that would need reading. One
# made in one environment and met in another — a container and its host
# sharing a checkout — costs the one refused call the header names.
remember() {
  [ "$1" = "$first" ] || return 0
  [ -f "$dir/run-guard.py" ] || return 0
  [ ! -e "$mark" ] || return 0
  [ -d "$dir/../cache" ] || mkdir "$dir/../cache" 2>/dev/null || return 0
  mkdir "$mark" 2>/dev/null || return 0
}

if [ -n "$first" ] && [ -d "$mark" ]; then
  # One line per candidate, each spelled here, so the interpreter is this
  # file's text whatever is in the cache. `|| status=$?` is how a non-zero
  # status is read under `set -e`; it runs nothing.
  status=0
  case "$first" in
    'py -3.12') py -3.12 "$dir/run-guard.py" "$1" || status=$? ;;
    python3) python3 "$dir/run-guard.py" "$1" || status=$? ;;
    python) python "$dir/run-guard.py" "$1" || status=$? ;;
  esac
  # `run-guard.py` owns these three and says what each one means. It reached
  # the guard and this is the guard's status, a crash's 1 included: which way
  # a guard's own failure falls is the guard's decision, and this path answers
  # as the `exec` below would have.
  case "$status" in
    91) trap - EXIT; exit 0 ;;
    92) trap - EXIT; exit 2 ;;
    93) trap - EXIT; exit 1 ;;
  esac
  # `rmdir`, which removes an empty directory and nothing else: whatever was
  # put inside the mark is still there afterwards, and then so is the mark.
  rmdir "$mark" 2>/dev/null || :
  if [ -d "$mark" ]; then
    next="The mark could not be removed, so every call is refused until it is"
  else
    next="The mark is forgotten, so the next call probes again"
  fi
  echo "run-guard.sh: a mark for $first was found and $first has not proved it ran $1 on this call (status $status); refusing the call rather than running it unguarded. $next: $mark" >&2
  exit 2
fi

# **A name on PATH is not a working interpreter, so each candidate is RUN
# before it is chosen.** `command -v` alone picked a `py` with no 3.12
# registered while a good `python3` sat beside it, and on Windows it picks the
# Store `python3` alias whenever `py` is absent — in both cases the `exec`
# failed and no fallback was ever reached. The probe is a harmless `-c` that
# exits non-zero below the 3.12 floor, and it judges no event, so the hook
# itself still runs exactly once. Raised by Copilot.
probe='import sys; sys.exit(sys.version_info < (3, 12))'

if command -v py >/dev/null 2>&1 && py -3.12 -c "$probe" >/dev/null 2>&1; then
  remember 'py -3.12'
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
  remember python3
  exec python3 "$dir/$1"
fi
if command -v python >/dev/null 2>&1 && python -c "$probe" >/dev/null 2>&1; then
  remember python
  exec python "$dir/$1"
fi
# **Exit 2, because it is the only code that blocks.** A `PreToolUse` hook
# that exits with anything else is a non-blocking error: the harness reports
# it and runs the tool unguarded. The unprobed last `exec` this replaces would
# have run a 3.11 `python`, or failed with 127 and let the call through —
# neither the loud refusal the header promises. Raised by Copilot.
echo "run-guard.sh: no Python 3.12 or newer found as py -3.12, py -3, python3 or python; refusing the call rather than running it unguarded" >&2
exit 2
