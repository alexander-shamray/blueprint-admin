#!/usr/bin/env python3
"""Run one guard in this interpreter, and prove with the exit status that it ran.

**`run-guard.sh` runs this, and only for an interpreter it did not probe on
this call.** The launcher probes each candidate before it `exec`s one, and it
remembers when the first candidate passed so that the next call can skip the
probe (#44). What the probe established was true when it was made. By the next
call the interpreter may have been uninstalled or repointed below the floor,
and the mark that says it passed sits where the guarded session can write it —
so on that path nothing is taken on trust, and this file is what replaces it.

**The floor is checked here, in the interpreter that is about to judge**, and
before anything is read from stdin. An interpreter below it leaves with a
status that proves nothing and an event nobody has consumed.

**Three statuses mean the guard ran, and nothing else produces them.** A
`PreToolUse` hook is believed on its exit status alone, and so is this file by
its launcher — but 0 is also what a stand-in that ran nothing answers, and 2
is also Python's own answer to a script it could not open. So the guard's
status is translated: `ALLOWED` for its 0, `REFUSED` for its 2 and `ERRORED`
for anything else, a crash included. The launcher turns those back into 0, 2
and 1 and refuses the call on every other status, 0 among them. The Store
alias exits 49, the `py` launcher with no matching runtime exits 103, a
missing command 127; none is one of the three.

**The verdict itself is the guard's and is passed through unchanged**: its
stdout, its stderr, and which way its own crash falls. `guard-git-argv.py`
argues that direction for itself, and a launcher that answered differently
depending on whether a mark existed would be a second opinion nobody asked
for.

**Compiled from source on every call, as `python guard.py` compiles it.** No
import and so no `__pycache__`: a cached module under `.claude/hooks/` would be
one more file whose contents decide what the guard does, and that list is
kept at the guards themselves.

**A bare name, resolved beside this file.** The launcher's closed set is what
admits a guard; this file takes the name it admitted and refuses anything
carrying a directory, so the guard and the file that proves it ran come from
one checkout, as the launcher and the guard already do.
"""

import os
import sys

FLOOR = (3, 12)

# What the launcher's `case` reads. Chosen clear of the statuses a candidate
# that never ran this file leaves with: 0, 1 and 2 are everybody's, 49 is the
# Store alias, the `py` launcher's own start at 100, 126 and 127 are the
# shell's, and anything above 128 reads as a signal.
ALLOWED = 91
REFUSED = 92
ERRORED = 93


def judged(name):
    """Run the guard called `name` as `__main__` and return its proven status."""
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, name)
    # Outside the `try` on purpose. A guard that cannot be opened ran nothing,
    # so this leaves with a traceback and a status that proves nothing, and
    # the launcher refuses the call — as `python` refuses a script it cannot
    # open, with 2.
    with open(path, "rb") as handle:
        source = handle.read()

    # What `python guard.py` gives the guard: its own `__main__`, its own path
    # in `__file__` and in `sys.argv[0]`. `guard-edit-target.py` and
    # `guard-triager-edit.py` both find their checkout from `__file__`.
    module = type(sys)("__main__")
    module.__file__ = path
    sys.modules["__main__"] = module
    sys.argv = [path]

    try:
        exec(compile(source, path, "exec"), module.__dict__)
        status = 0
    except SystemExit as stop:
        status = stop.code
    except BaseException:  # noqa: BLE001 - reported as the interpreter would
        sys.excepthook(*sys.exc_info())
        status = 1

    # `sys.exit`'s own rules: nothing is success, a number is itself, and
    # anything else is printed and counts as a failure.
    if status is None:
        status = 0
    elif not isinstance(status, int):
        sys.stderr.write("%s\n" % (status,))
        status = 1

    # A verdict the harness never received is not a verdict delivered, and the
    # interpreter says so itself when its last flush fails.
    try:
        sys.stdout.flush()
        sys.stderr.flush()
    except Exception:  # noqa: BLE001 - whatever stopped the write
        status = 1

    if status == 0:
        return ALLOWED
    if status == 2:
        return REFUSED
    return ERRORED


def main(argv):
    if sys.version_info < FLOOR:
        sys.stderr.write(
            "run-guard.py: Python %d.%d or newer is required and this is "
            "%d.%d; judging nothing\n" % (FLOOR + tuple(sys.version_info[:2])))
        return 1
    if len(argv) != 2 or os.path.basename(argv[1]) != argv[1] or not argv[1]:
        sys.stderr.write("usage: run-guard.py <guard file name>\n")
        return 1
    return judged(argv[1])


if __name__ == "__main__":
    sys.exit(main(sys.argv))
