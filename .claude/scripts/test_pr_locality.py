"""`pr-locality.sh` driven end to end against a fake `gh`, beside the gate it mirrors (#319).

`.github/locality-gate/locality_gate.py` is the CI gate and `pr-locality.sh` is the same verdict read
locally by `/review-branch` and `/ship`. They parse the same two rows and refuse for the same reasons, and
until this file nothing under `.claude/scripts` ran the shell side, so a change to one copy's grammar or
message passed with the other unchanged. The seam is the one the helper already has: it reaches GitHub only
through `gh`, so a stub `gh` first on PATH answers `pr view` and `api .../files` from canned values and the
script is run as it is.

Each refusal case asserts two things: the shell copy refuses with the phrase, and the Python gate's source
still carries the same phrase, so a message reworded in one copy alone fails here. **The second half is a
wording check, not a behaviour check**: a generic phrase can occur in another of the gate's messages, so the
gate's own refusal of the same input is `.github/locality-gate/test_locality_gate.py`'s subject.

The verdict cases run at the repository's HEAD against its real `.github/locality-gate/classes.yml`, which is
how the helper reads the map when the PR base is the checkout: class D holds no `src/**`, class A holds
`src/Admin.Host/**`. A map edit that moves either breaks them, deliberately: the helper and the map are one
verdict.
"""

import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REPO = SCRIPTS.parent.parent
HELPER = SCRIPTS / "pr-locality.sh"
GATE = REPO / ".github" / "locality-gate" / "locality_gate.py"
BASH = shutil.which("bash")
GIT = shutil.which("git")

STUB = r"""#!/usr/bin/env bash
case "$1" in
  pr)
    case "$*" in
      *changedFiles*) printf '%s\n' "$FAKE_CHANGED" ;;
      *baseRefOid*) printf '%s\n' "$FAKE_BASE" ;;
      *"--json body"*) cat "$FAKE_BODY_FILE" ;;
    esac ;;
  api)
    case "$*" in
      # A line is `filename` or `filename<TAB>previous_filename` (a rename); the count query
      # reads the first column, the path query both.
      *@json*) while IFS=$'\t' read -r n p; do
                 if [ -n "$n" ]; then printf '"%s"\n' "$n"; fi
                 if [ -n "$p" ]; then printf '"%s"\n' "$p"; fi
               done < "$FAKE_FILES_FILE" ;;
      *) cut -f1 "$FAKE_FILES_FILE" ;;
    esac ;;
esac
"""


def setUpModule():
    missing = [name for name, path in (("bash", BASH), ("git", GIT)) if path is None]
    if missing:
        raise RuntimeError(f"{', '.join(missing)} required: these tests run the script, not an equivalent")


def head():
    return subprocess.run([GIT, "rev-parse", "HEAD"], cwd=str(REPO), capture_output=True, text=True).stdout.strip()


def body(class_cell="D", touch_cell="docs/**"):
    return f"Intro.\n\n| | |\n|---|---|\n| Class | {class_cell} |\n| Touch set | {touch_cell} |\n\nMore text.\n"


class Case(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="pr-locality-"))
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        stub = self.dir / "gh"
        stub.write_text(STUB, encoding="utf-8", newline="\n")
        stub.chmod(stub.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    def run_helper(self, text, files=("docs/a.md",), changed=None):
        body_file = self.dir / "body.md"
        files_file = self.dir / "files.txt"
        body_file.write_text(text, encoding="utf-8", newline="\n")
        files_file.write_text("\n".join(files) + "\n", encoding="utf-8", newline="\n")
        env = dict(os.environ)
        env["PATH"] = os.pathsep.join([str(self.dir), env.get("PATH", "")])
        env.update(
            FAKE_CHANGED=str(len(files) if changed is None else changed),
            FAKE_BASE=head(),
            FAKE_BODY_FILE=body_file.as_posix(),
            FAKE_FILES_FILE=files_file.as_posix(),
        )
        return subprocess.run([BASH, str(HELPER), "7"], capture_output=True, text=True, env=env, cwd=str(REPO))


class TheVerdict(Case):
    def test_a_path_in_both_the_declared_set_and_the_class_map_is_inside(self):
        result = self.run_helper(body("D", "docs/**"), files=("docs/a.md", "src/Admin.Host/Program.cs"))

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["class D", "inside docs/a.md", "outside src/Admin.Host/Program.cs"], result.stdout.splitlines())

    def test_the_declared_set_alone_is_not_enough(self):
        # Class D does not reach src/**; a touch set that names it is still outside.
        result = self.run_helper(body("D", "src/**"), files=("src/Admin.Host/Program.cs",))

        self.assertEqual(["class D", "outside src/Admin.Host/Program.cs"], result.stdout.splitlines())

    def test_a_joined_class_is_the_union_and_a_brace_glob_is_one_token(self):
        result = self.run_helper(
            body("A+D", "`src/Admin.Host/Jobs/**`, docs/{a,b}.md"),
            files=("src/Admin.Host/Jobs/X.cs", "docs/b.md", "docs/c.md"),
        )

        self.assertEqual(
            ["class A+D", "inside src/Admin.Host/Jobs/X.cs", "inside docs/b.md", "outside docs/c.md"], result.stdout.splitlines())

    def test_both_ends_of_a_rename_are_judged(self):
        result = self.run_helper(body("D", "docs/**"), files=("docs/b.md\tsrc/Admin.Host/Program.cs",), changed=1)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["class D", "inside docs/b.md", "outside src/Admin.Host/Program.cs"], result.stdout.splitlines())

    def test_the_touch_set_cell_is_never_printed(self):
        result = self.run_helper(body("D", "docs/**"))

        # Asserted against the whole expected output first, so a refusal that prints nothing cannot pass.
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["class D", "inside docs/a.md"], result.stdout.splitlines())
        self.assertNotIn("docs/**", result.stdout)

    def test_a_changed_path_that_is_not_a_plain_path_refuses_the_whole_run(self):
        for name, path in (
            ("a backslash", "docs\\a.md"),
            ("a dot-dot segment", "../x.md"),
            ("no slash or dot", "README"),
            ("an empty segment", "docs//a.md"),
        ):
            with self.subTest(case=name):
                result = self.run_helper(body("D", "docs/**"), files=("docs/ok.md", path))

                self.assertEqual(3, result.returncode, result.stdout)
                self.assertIn("a changed path is not a plain path", result.stderr)
                self.assertEqual("", result.stdout, "a verdict list with a path withheld would read as complete")

    def test_a_body_with_neither_row_prints_nothing(self):
        result = self.run_helper("No rows here.\n")

        self.assertEqual((0, ""), (result.returncode, result.stdout))

    def test_the_files_endpoint_disagreeing_with_changed_files_is_refused(self):
        result = self.run_helper(body(), files=("docs/a.md",), changed=3)

        self.assertEqual(3, result.returncode)
        self.assertIn("the files endpoint returned 1 names against changedFiles 3", result.stderr)


class TheRefusalsMirrorTheGate(Case):
    """(case, class cell, touch cell, phrase). The phrase is in both copies, or this fails."""

    REFUSALS = (
        ("an unbalanced backtick", "D", "`docs/a.md", "an unbalanced backtick, or text beside a path"),
        ("a reason beside a path", "D", "`docs/a.md` because", "text beside a path"),
        ("a reason as its own item", "D", "docs/a.md, because we need it", "is not a path list"),
        ("an unbalanced opening brace", "D", "docs/{a,b.md", "has an unbalanced brace"),
        ("an unbalanced closing brace", "D", "docs/a,b}.md", "has an unbalanced brace"),
        ("prose", "D", "just some words", "is not a path list"),
        ("a token with no slash or dot", "D", "docs", "is not a path list"),
        ("a leading slash", "D", "/etc/passwd", "names a path outside the repository"),
        ("a dot-dot segment", "D", "docs/../src/x.cs", "names a path outside the repository"),
        ("a dot-dot inside a brace", "D", "{../outside,docs/x.md}", "names a path outside the repository"),
        ("a second cell", "D", "docs/a.md | extra", "is not one cell"),
        ("a repeated class", "D+D", "docs/a.md", "repeats a class"),
        ("a class outside A to E", "F", "docs/a.md", "is not a class"),
        ("a lower-case class", "d", "docs/a.md", "is not a class"),
    )

    def test_each_refusal_is_in_the_shell_copy_and_the_gate(self):
        # Adjacent string literals are one message to Python, so they are joined before a phrase is looked for.
        gate = re.sub(r'"\s*\n\s*f?"', "", GATE.read_text(encoding="utf-8"))
        for name, class_cell, touch_cell, phrase in self.REFUSALS:
            with self.subTest(case=name):
                result = self.run_helper(body(class_cell, touch_cell))

                self.assertEqual(3, result.returncode, f"{name}: {result.stdout!r} {result.stderr!r}")
                self.assertIn(phrase, result.stderr)
                self.assertEqual("", result.stdout, "a refusal prints no verdict")
                self.assertTrue(phrase in gate, f"the Python gate no longer carries {phrase!r}")

    def test_two_class_rows_and_one_row_without_the_other_are_refused(self):
        two = "| Class | D |\n| Class | A |\n| Touch set | docs/a.md |\n"
        lone = "| Class | D |\n"
        for name, text, phrase in (
            ("two Class rows", two, "more than one"),
            ("a Class row without a Touch set row", lone, "one row without the other"),
        ):
            with self.subTest(case=name):
                result = self.run_helper(text)

                self.assertEqual(3, result.returncode, result.stderr)
                self.assertIn(phrase, result.stderr)

    def test_a_trailing_pipe_is_removed_only_when_it_ends_the_row(self):
        # `| Touch set | docs/a.md |x` must not read as `docs/a.md`.
        result = self.run_helper("| Class | D |\n| Touch set | docs/a.md |x\n")

        self.assertEqual(3, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main()
