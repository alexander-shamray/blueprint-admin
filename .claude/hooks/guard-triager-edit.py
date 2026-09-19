#!/usr/bin/env python3
"""Refuse the /review-grok triager every edit `/ship` refuses, in every turn.

**`/ship`'s `disallowed-tools` was the triager's editing boundary, and it
lasts only the turn `/ship` was loaded in (#27).** Measured: a triager spawned
in that turn was refused `.github/**` and `README.md`; one spawned a user
message later, `/ship` not re-invoked, wrote into both. Step 5 spawns the
triager async, so every round after the first starts in exactly such a turn —
the rounds reading an untrusted review, holding `Edit` over every tree the
list names.

**So the boundary moves onto the profile, where a turn cannot end it.** This
hook sits in `review-grok-triager.md`'s own `hooks:`, like
`guard-triager-dispatch.py` beside it, so it judges the triager's edits and
nobody else's.

**It holds no copy of the list.** The trees are read from `/ship`'s
`disallowed-tools` on every call — the `Edit(...)` entries, from the
`ship.md` beside this file — so the list has one owner and a path added there
is refused here with no second edit to forget. `/ship` keeps stating it; this
file is what makes it hold outside the turn.

**One question, and the other guard answers the rest.** Whether a path is the
file it spells — a link, a junction, an alternate spelling — is
`guard-edit-target.py`'s subject, wired session-wide in `settings.json` and so
in force here too. This file asks only whether the target, read relative to
the checkout that holds it, falls under one of those patterns; it asks it of
the spelled path and of its resolution alike, and matches without regard to
case, because the file system this runs on does too. A target in no checkout
is refused outright: the triager edits the branch and nothing else.

**It fails closed, like the dispatch guard and unlike the session-wide
two.** An unreadable event, or a `ship.md` yielding no `Edit(...)` patterns,
refuses the edit: a guard that cannot find its rules and admits everything is
#27 again. Exit 2 is the only code that blocks a `PreToolUse` call.
"""
import json
import os
import re
import sys

EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")
HERE = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))
SHIP = os.path.join(HERE, ".claude", "commands", "ship.md")


def patterns():
    """`/ship`'s `Edit(...)` denies as compiled patterns, or `None`."""
    try:
        with open(SHIP, encoding="utf-8") as handle:
            text = handle.read().replace("\r\n", "\n")
    except OSError:
        return None
    if not text.startswith("---\n"):
        return None
    front = text[4:].split("\n---", 1)[0]
    line = next((entry for entry in front.split("\n")
                 if entry.startswith("disallowed-tools:")), None)
    if line is None:
        return None
    found = []
    for spelled in re.findall(r"Edit\(([^)]*)\)", line):
        spelled = spelled.strip()
        while spelled.startswith("./"):
            spelled = spelled[2:]
        if spelled and spelled not in found:
            found.append(spelled)
    return [(spelled, compile_glob(spelled)) for spelled in found] or None


def compile_glob(glob):
    """A permission-rule glob as an anchored, case-insensitive regex.

    `**/` is zero or more directories, a trailing `**` everything beneath,
    `*` and `?` stay within one component. A pattern with no `**/` prefix is
    anchored at the checkout root, as the rule it copies is.
    """
    out = []
    i = 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("".join(out) + r"\Z", re.IGNORECASE)


def checkout_root(path):
    """The nearest ancestor of `path` holding a `.git` entry, or `None`."""
    current = path
    while True:
        if os.path.exists(os.path.join(current, ".git")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def relative(path):
    """`path` relative to its checkout, `/`-separated, or `None`."""
    root = checkout_root(os.path.dirname(path))
    if root is None:
        return None
    rel = os.path.relpath(path, root).replace(os.sep, "/")
    if rel == ".." or rel.startswith("../"):
        return None
    # Trailing dots and spaces are dropped by Windows, so `README.md.` is
    # `README.md` there; an alternate data stream names the file it is on.
    parts = [part.rstrip(". ").split(":", 1)[0] for part in rel.split("/")]
    return "/".join(parts)


def refusal(reason):
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    f"the /review-grok triager {reason} "
                    "(.claude/hooks/guard-triager-edit.py, #27)"),
            }
        },
        sys.stdout,
    )
    return 0


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        print("guard-triager-edit: unreadable hook event; refusing",
              file=sys.stderr)
        return 2
    if not isinstance(event, dict):
        print("guard-triager-edit: hook event is not an object; refusing",
              file=sys.stderr)
        return 2

    if event.get("tool_name") not in EDIT_TOOLS:
        return 0
    tool_input = event.get("tool_input")
    if not isinstance(tool_input, dict):
        print("guard-triager-edit: tool_input is not an object; refusing",
              file=sys.stderr)
        return 2
    spelled = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not isinstance(spelled, str) or not spelled:
        print("guard-triager-edit: no target path; refusing", file=sys.stderr)
        return 2

    rules = patterns()
    if rules is None:
        print(f"guard-triager-edit: no Edit(...) denies read from {SHIP}; "
              "refusing", file=sys.stderr)
        return 2

    cwd = event.get("cwd")
    if not isinstance(cwd, str) or not cwd:
        cwd = os.getcwd()
    lexical = os.path.abspath(
        spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled))
    for target in (lexical, os.path.realpath(lexical)):
        rel = relative(target)
        if rel is None:
            return refusal(f"edits inside a checkout only; refused {spelled!r}")
        for glob, pattern in rules:
            if pattern.match(rel):
                return refusal(
                    f"may not edit {rel!r}: it is under /ship's "
                    f"Edit({glob}) deny")
    return 0


if __name__ == "__main__":
    sys.exit(main())
