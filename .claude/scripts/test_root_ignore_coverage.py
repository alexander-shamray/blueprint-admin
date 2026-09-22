"""Directories excluded from the inside only, which the index still reads.

`.superpowers/sdd/.gitignore` holds a bare `*`. Git honours it, so nothing
under it is ever committed and it looks excluded. The codebase index reads the
root ignore files its config names and no nested ones, so it indexed that
directory in full, where scratch competed with code in every search result. A
nested blanket ignore is therefore not a substitute for a root rule, and this
is where that is asserted.

**Two halves, because only one of them can bind in a clone.** The directories
this rule is about are ignored, so CI never checks them out: a scan of the tree
finds nothing to judge and passes for the wrong reason. So the scan itself is
exercised against fixtures — the check is on what the scan looks at rather than
on what it happened to find, which CLAUDE.md names as this repository's
most-repeated failure — and the coverage of the two directories the tooling
actually creates is asked of `git check-ignore`, which answers for a path
whether or not it is on disk.

**Not a skip where `git` is missing.** A skip reports a pass, which is the
fail-open `test_grok_helpers.py` refuses for the same tool.
"""

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parent.parent

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where `git` may be WSL's.
GIT = shutil.which("git")

# The root ignore file both readers share. `.codeindexignore` would satisfy the
# index alone, which is the weaker half of the problem: a directory git does
# not ignore is a directory somebody commits.
ROOT_IGNORE = ".gitignore"

# Patterns that exclude a directory's whole contents. A file carrying one of
# these and no negation says "nothing here is code", which is exactly the claim
# the root has to be making too.
BLANKET = {"*", "/*", "**", "/**"}

# The directories this repository's own tooling creates and never commits.
# The first two carry a blanket ignore inside them; the third holds machine-
# local state and is covered whole, so an entry added under it needs no new
# rule. None is reliably present in a clone, so the scan below cannot see
# them and they are named instead — `.gitignore` owns the rules, and these
# are the assertions that the rules are still there.
TOOLING_SCRATCH = (".remember/", ".superpowers/", ".claude/cache/")


def blanket_ignore(path):
    """Whether this ignore file excludes everything beneath its directory."""
    lines = [line.strip() for line in path.read_text(encoding="utf-8").splitlines()]
    rules = [line for line in lines if line and not line.startswith("#")]
    return any(rule in BLANKET for rule in rules) and not any(
        rule.startswith("!") for rule in rules)


def root_covered(repo, paths):
    """Of <paths>, those the repository's root ignore file excludes.

    The trailing slash each path carries is load-bearing: a `dir/` pattern
    matches only a path git takes for a directory, and for one that is not on
    disk — every case this file exists for — the pathspec's own slash is the
    only thing that says so.

    `-v` is what makes this a question about the *root* rule rather than about
    git's answer, which a nested file is equally able to give: it reports the
    ignore file credited with the match, and only the one at the root counts.

    A negated match needs no handling of its own: check-ignore lists what is
    ignored, so a path re-included by a `!` rule draws no line at all and
    never reaches the source comparison. That is a behaviour this helper
    leans on rather than one it enforces, so a fixture holds it.

    `-z` and bytes on both sides, because the text pipe is not transparent
    here: on Windows it writes `\n` out as `\r\n`, and git took the carriage
    return for part of the path — every answer came back for a path nothing
    could match, which reads as "covered by nothing". NUL separation also
    spares the reply git's own quoting of unusual names.
    """
    if not paths:
        return set()
    proc = subprocess.run([GIT, "check-ignore", "-v", "-z", "--stdin"],
                          cwd=str(repo), capture_output=True,
                          input="\0".join(paths).encode("utf-8"))
    # 0 is some path matched, 1 is none did; anything else is git failing, and
    # a failed read must not read as "nothing is covered".
    if proc.returncode not in (0, 1):
        raise AssertionError(
            f"check-ignore failed ({proc.returncode}): {proc.stderr.decode()}")
    # <source> NUL <linenum> NUL <pattern> NUL <pathname> NUL, per path.
    fields = proc.stdout.decode("utf-8").split("\0")[:-1]
    return {path for source, _, _, path in zip(*[iter(fields)] * 4)
            if source == ROOT_IGNORE}


def excluded_from_inside_only(repo):
    """Directories holding a blanket ignore that no root rule covers.

    Descent stops at every directory the root already covers: a blanket ignore
    nested under one of those is covered by construction, and pruning them is
    also what keeps this walk out of `node_modules`.
    """
    found = []
    for base, dirs, files in os.walk(repo):
        rel = Path(base).relative_to(repo).as_posix()
        prefix = "" if rel == "." else f"{rel}/"
        if prefix and ROOT_IGNORE in files and blanket_ignore(Path(base, ROOT_IGNORE)):
            found.append(prefix)
        dirs[:] = sorted(d for d in dirs if d != ".git")
        covered = root_covered(repo, [f"{prefix}{d}/" for d in dirs])
        dirs[:] = [d for d in dirs if f"{prefix}{d}/" not in covered]
    return sorted(found)


class TheScan(unittest.TestCase):
    """`excluded_from_inside_only` against trees built to be judged.

    The repository cannot supply these cases — the directories are ignored, so
    they are never checked out — and a scan nobody has watched find something
    is the gate this repository keeps catching asleep.
    """

    @classmethod
    def setUpClass(cls):
        if not GIT:
            raise AssertionError("required on PATH: git")

    def setUp(self):
        self.repo = Path(tempfile.mkdtemp(prefix="root-ignore-"))
        self.addCleanup(shutil.rmtree, str(self.repo), ignore_errors=True)
        subprocess.run([GIT, "init", "-q", str(self.repo)], check=True)

    def write(self, rel, text):
        path = self.repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")

    def test_it_reports_a_directory_excluded_only_from_the_inside(self):
        self.write(".gitignore", "/artifacts/\n")
        self.write("scratch/.gitignore", "*\n")
        self.assertEqual(excluded_from_inside_only(self.repo), ["scratch/"])

    def test_a_root_rule_takes_that_directory_off_the_list(self):
        self.write(".gitignore", "/artifacts/\nscratch/\n")
        self.write("scratch/.gitignore", "*\n")
        self.assertEqual(excluded_from_inside_only(self.repo), [])

    def test_a_blanket_ignore_under_a_covered_directory_is_never_reached(self):
        # `node_modules` in miniature: covered at the root, and full of nested
        # ignore files that are somebody else's business.
        self.write(".gitignore", "vendor/\n")
        self.write("vendor/pkg/.gitignore", "*\n")
        self.assertEqual(excluded_from_inside_only(self.repo), [])

    def test_an_ignore_file_that_excludes_only_some_paths_is_not_a_finding(self):
        # The shape `src/Admin.Web/.gitignore` has: a project's own build
        # output, beside files that are committed.
        self.write(".gitignore", "/artifacts/\n")
        self.write("web/.gitignore", "# Compiled output\n/dist\n/node_modules\n")
        self.assertEqual(excluded_from_inside_only(self.repo), [])

    def test_a_blanket_ignore_with_an_exception_is_not_a_finding(self):
        # `*` plus `!keep.md` keeps a tracked file, so the directory is not the
        # scratch this rule is about and a root rule would hide that file.
        self.write(".gitignore", "/artifacts/\n")
        self.write("partly/.gitignore", "*\n!keep.md\n")
        self.assertEqual(excluded_from_inside_only(self.repo), [])

    def test_a_nested_rule_does_not_count_as_root_coverage(self):
        # The defect itself: git is satisfied by `outer/.gitignore` and the
        # index is not, so only the root file may answer for `outer/inner/`.
        self.write(".gitignore", "/artifacts/\n")
        self.write("outer/.gitignore", "inner/\n")
        self.write("outer/inner/.gitignore", "*\n")
        self.assertEqual(excluded_from_inside_only(self.repo), ["outer/inner/"])

    def test_a_root_negation_does_not_count_as_root_coverage(self):
        # `scratch-keep/` matches `scratch*` and is then re-included, so it is
        # not ignored and must not be pruned — a blanket ignore below it is
        # still this gate's business. The helper never reads the pattern field,
        # so this case is what says it does not have to.
        self.write(".gitignore", "scratch*\n!scratch-keep/\n")
        self.write("scratch-keep/deep/.gitignore", "*\n")
        self.assertEqual(excluded_from_inside_only(self.repo),
                         ["scratch-keep/deep/"])


class ThisRepository(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        if not GIT:
            raise AssertionError("required on PATH: git")

    def test_nothing_is_excluded_from_the_inside_only(self):
        # Vacuous in a clone and not in a developer's checkout, which is where
        # a new scratch directory is added and where this has to catch it.
        self.assertEqual(excluded_from_inside_only(ROOT), [])

    def test_the_root_covers_the_scratch_directories_the_tooling_creates(self):
        covered = root_covered(ROOT, list(TOOLING_SCRATCH))
        self.assertEqual(covered, set(TOOLING_SCRATCH))


if __name__ == "__main__":
    unittest.main()
