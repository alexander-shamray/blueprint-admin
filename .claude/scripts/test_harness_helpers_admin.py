"""The harness's helpers, guards and command grants, tested where they decide.

The scripts in this directory carry the judgements the whole /ship chain and
both sweeps rest on, and until this suite existed none of them had a test.

**Almost every subject below shipped wrong**, and each of those is reproduced
as a case that fails against the old behaviour — a gate only ever observed
green is one nobody has established is looking at anything, which is the rule
the .github/**-gate suites are already written to. One subject is the
exception and is marked as such: the label helper's confinement never shipped
wrong, and its cases keep a closed grant closed rather than catching anything.

**No count opens this list, in this file or in the two that mirror it.**
`.github/workflows/ci.yml` and `docs/testing.md` enumerate the same subjects,
and the numeral that used to lead all of them said four, then five, then six,
and was stale again inside the pull request that added the last of them. A
figure restated in several places goes stale in all of them at once; the
enumerations are what a reader compares.

**The regression negatives are paired with positive controls, and those are not
decoration.** A negative that passes because a pattern matches *nothing* is
indistinguishable from one that works — a trap this suite fell into once, when
`\\b402\\b`'s negatives all tested digits *around* the number and none tested it
alone.

What is under test, and which issue each half closes:

  blueprint-backend#75   the sweeps' worktree shape check was not the direct-child check its
        comment claimed, because `?` matches `/` in a bash `case`.
  blueprint-backend#75   `gh label create` is create-or-overwrite, so the grant reached any
        label in any repository. This one is the odd entry: it never shipped
        wrong. It is a grant closed by moving it into a helper, and these cases
        are what keep it closed rather than what caught it.
  blueprint-backend#75   `gh issue create` was the same shape one helper over: `--repo` and
        `--label` free under a prefix grant, and a `/`-title filed as a path
        because the grant could not carry an env prefix. Closed the same way,
        and the confinement never shipped wrong either; the title did, and
        the case that reads the child's environment is the one for it.
  blueprint-backend#33   the deny list guarded the helpers and not the files that grant them.
        The cases cover every control-surface path in both spellings, that no
        rule is spelled `Write(`, and that the worktree root is never denied —
        the over-reach that would deny editing the repository itself.
  blueprint-backend#57   the sweeps' de-duplication gate treated any open issue as tracking, so
        a stranger could suppress a finding and end the sweep. The cases cover
        both copies of the gate and every phrasing it has retired.
  blueprint-backend#60   two commands stated editing boundaries their grants did not enforce.
        The cases read every tracked tree and every tracked root file from
        `git ls-files` and assert each is denied, because the alternative is a
        list that rots the way every deny-list here has.
  blueprint-backend#30   `git log --output=` is an arbitrary file write that reads as
        inspection, and the settings deny matches only the unquoted spelling.
  blueprint-backend#23   the push deny-list enumerated spellings, and git's refspec grammar is
        larger than any list. Both are the argv guard's, and its cases are the
        longest set here because two review rounds took the first design apart:
        bundled shorts, abbreviations, wildcard destinations, pushes naming no
        destination, heredoc bodies read as arguments, command substitutions
        read as inert.
  blueprint-backend#150  what suppresses a sweep finding was prose in two files. The cases cover
        the helper's three exit codes, that neither sweep can read an issue's
        author at all, and that the one legitimate output stays writable.
  #181  a path deny is matched on the SPELLING, so an edit through a link
        inside an allowed tree lands where no deny has judged it. Its cases
        live in `test_edit_target_guard.py` beside this file, which the same
        discover picks up — a link into a denied tree, a link out of the
        checkout, a denied tree spelled as ITSELF (admitted, because the guard
        holds no copy of any deny list), and a checkout reached through a link
        (admitted, or every edit in a worktree under a linked temp root would
        be refused).
  #124  the external reviewers' helpers, hooks and suites left with them for
        the tag `archive/external-reviewers`; what stays is the grants and the
        agent list those commands were checked against.

**This inventory is a third copy of a list `ci.yml` and `docs/testing.md` also
keep, and it goes stale exactly as a redundant copy does.** Reconcile it with
those two, or the next reader of the suite gets the shortest version.

Two rules the suite is written to, both of them this repository's:

  *The engine under test is the engine that ships.* Every pattern assertion
  shells out to the same `grep -E` the scripts call. Re-implementing the regex
  in Python's `re` would be a second specification, and a hand-written double
  cannot disagree with itself.

  *A declared pattern needs a test whose subject is where it is APPLIED.* A
  pattern declared once and then quietly not used, or used alongside a second
  literal copy, is the drift `SOURCE_INPUTS` was invented for. So the pattern
  cases are paired with structural cases over the call sites.

Run: py -3.12 -m unittest discover -s .claude/scripts
Needs bash, grep, git and jq on PATH; needs no network, no Docker, no gh
and no SDK. `git` because one case drives a real worktree round trip.
"""

import contextlib
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
from unittest import mock
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
DETACH = SCRIPTS / "git-worktree-detach.sh"
NEWLINE = chr(10)  # spelled this way so patch scripts cannot mangle it
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
DROP = SCRIPTS / "git-worktree-drop.sh"
HOOK = SCRIPTS.parent / "hooks" / "guard-git-argv.py"
BRANCH_COMMAND = SCRIPTS.parent / "commands" / "branch.md"
SUPPRESSES = SCRIPTS / "gh-issue-suppresses.sh"
ISSUE_TEXT = SCRIPTS / "gh-issue-text.sh"
ISSUE_LIST = SCRIPTS / "gh-issue-list.sh"

BASH = shutil.which("bash")
GREP = shutil.which("grep")
GIT = shutil.which("git")
JQ = shutil.which("jq")


def setUpModule():
    # Not a skip. A skip on a missing tool reports a pass, which is the
    # fail-open every gate in this repository is written against — a check
    # that cannot fail makes everything behind it unverified. Absent any of
    # these, this suite has established nothing and says so.
    #
    # The list grew with the suite and is checked rather than assumed: `git`
    # arrived with the worktree round trip and `jq` with the parsed verdict, and
    # a prerequisite that is used but not declared is the drift this repository
    # keeps finding one file at a time.
    missing = [
        name for name, path in
        (("bash", BASH), ("grep", GREP), ("git", GIT), ("jq", JQ))
        if path is None
    ]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests exercise "
            "the same tools the scripts do, and asserting through Python "
            "equivalents instead would be testing a second specification"
        )


def run_bash(script, subject="", **env_extra):
    """Run a bash fragment with the subject on stdin and everything else in env.

    Nothing is passed as an argument, and that is not fastidiousness. Under MSYS
    — the host this repository is developed on — an argv element crossing into
    `bash.exe` is re-parsed by the MSYS runtime, so a pattern containing `"`
    arrives with its quotes eaten and matches nothing. That failure is silent in
    the direction that matters: `grep` finding no stopReason reads exactly like
    a payload that carries none, so the first version of this suite reported the
    allow-list as broken when the allow-list was fine. Environment variables and
    stdin are not re-parsed, so they mean the same thing on both platforms.
    """
    env = dict(os.environ)
    env.update(env_extra)
    return subprocess.run(
        [BASH, "-c", script],
        input=subject,
        capture_output=True,
        text=True,
        env=env,
    )


def grep_matches(pattern, subject, ignore_case=True):
    """`grep -qE` (optionally -i) on the real engine the scripts call."""
    flag = "-i" if ignore_case else ""
    return run_bash(f'grep -q {flag} -E "$PAT"', subject, PAT=pattern).returncode == 0


def code_lines(text):
    """The executable lines of a shell script — comments and blanks dropped.

    Shared rather than duplicated per class, because the two callers assert
    opposite things with it and both are load-bearing: one that a pattern has no
    second literal copy, the other that an exit code exists in the *code*. That
    second one is why this is here at all — asserted against the whole file, it
    passed on the explanatory comments alone, so deleting the executable line
    would not have failed it.
    """
    return [
        line for line in text.splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]


# A `git` that does what a Windows removal does when a handle is held: `worktree remove` really
# removes, leaves a directory behind and fails; and, when GIT_SHIM_BLIND is set, `worktree list`
# fails afterwards, a register that cannot be read. It is a bash FUNCTION sourced through
# BASH_ENV, not a `git` earlier on PATH: PATH resolution of an extensionless script differed on
# the Windows CI runner, and a function does not depend on it. `command git` is the real one.
GIT_FAILING_AFTER_REMOVAL = """\
git() {
  if [ "$1" = worktree ] && [ "$2" = remove ]; then
    command git worktree remove "$3" && mkdir -p "$3/held" && : > "$3/held/file"
    : > "$GIT_SHIM_MARK"
    return 1
  fi
  if [ "$1" = worktree ] && [ "$2" = list ] && [ -n "${GIT_SHIM_BLIND:-}" ] && [ -e "$GIT_SHIM_MARK" ]; then
    return 1
  fi
  command git "$@"
}
"""


def git_failing_after_removal(directory):
    """The environment that makes a helper's `git` behave so: BASH_ENV and the mark file's path."""
    directory = Path(directory)
    shim = directory / "git-shim.sh"
    shim.write_text(GIT_FAILING_AFTER_REMOVAL, encoding="utf-8", newline="\n")
    return {"BASH_ENV": shim.as_posix(), "GIT_SHIM_MARK": (directory / "removed.mark").as_posix()}


def private_tmproot(test):
    """A fresh temp root for `test`, removed when the test ends (#356), as BASH resolves it.

    That is not the root Python resolves. On this host `/tmp` is `D:\\tmp\\alexa` for the
    shell and `C:\\tmp` for every Python and built-in reader — different directories,
    both populated. A test that made its fixture with `pathlib` and then handed the path
    to a shell script was checking a directory the script could not see, and the refusal
    it asserted on was `not an existing directory` rather than the shape check it meant
    to exercise. So the root is returned in bash's spelling and the fixtures are made by
    bash too.
    """
    root = tempfile.mkdtemp(prefix="sweeproot-")
    test.addCleanup(shutil.rmtree, root, ignore_errors=True)
    return run_bash('cd "$ROOT" && pwd -P', ROOT=root).stdout.strip()


class AdminStack(unittest.TestCase):
    """The cases the audit keeps for this repository's stack, its tree and its local scripts."""

    SUBJECTS = {
        "review-branch.md": set(),
    }

    GLOBALLY_DENIED = frozenset()

    @classmethod
    def tracked_trees(cls):
        """Every top-level directory git actually tracks.

        Read from git rather than listed here, because a list in this file is
        the same copy that rots one directory over.
        """
        out = subprocess.run(
            [GIT, "ls-files"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        return {
            line.split("/")[0] for line in out.stdout.splitlines() if "/" in line
        }

    @staticmethod
    def disallowed(name):
        text = (COMMANDS / name).read_text(encoding="utf-8")
        found = re.findall(r"^disallowed-tools:\s*(.+)$", text, re.MULTILINE)
        if len(found) != 1:
            raise AssertionError(
                f"expected exactly one disallowed-tools line in {name}, "
                f"found {len(found)}"
            )
        return [entry.strip() for entry in found[0].split(",")]

    def test_the_tree_listing_is_not_vacuous(self):
        # The positive control, and it carries every case below: a listing that
        # silently came back empty would satisfy all of them.
        trees = self.tracked_trees()
        self.assertGreater(len(trees), 4)
        for expected in ("src", "tests", "docs", ".github"):
            self.assertIn(expected, trees)

    def test_every_tracked_tree_is_denied_in_both_spellings(self):
        # The whole point. Not "the trees the issue named" — every tree that
        # exists, so adding one is a red build rather than a quiet widening.
        for name, subject in self.SUBJECTS.items():
            rules = self.disallowed(name)
            for tree in self.tracked_trees() - subject - self.GLOBALLY_DENIED:
                for prefix in ("", "./"):
                    with self.subTest(command=name, tree=tree, prefix=prefix):
                        self.assertIn(f"Edit({prefix}{tree}/**)", rules)

    @classmethod
    def tracked_root_files(cls):
        """Every tracked file at the repository root, read from git."""
        out = subprocess.run(
            [GIT, "ls-files"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        return {line for line in out.stdout.splitlines() if line and "/" not in line}

    def test_the_root_file_listing_is_not_vacuous(self):
        # The positive control for the case below.
        files = self.tracked_root_files()
        self.assertGreater(len(files), 4)
        for expected in ("CLAUDE.md", "README.md", "Directory.Build.props", "global.json"):
            self.assertIn(expected, files)

    def test_every_tracked_root_file_is_denied_in_both_spellings(self):
        # **Denying directories left every root file writable**, which a review
        # raised against the first version of this list: `CLAUDE.md`,
        # `package.json`, `angular.json` and `tsconfig.json` all sit at the
        # root, so a command promising not to fix findings could still apply
        # one to root configuration. A tree-only deny is a boundary with a hole
        # exactly where this repository keeps its build inputs.
        for name in self.SUBJECTS:
            rules = self.disallowed(name)
            for path in self.tracked_root_files():
                for prefix in ("", "./"):
                    with self.subTest(command=name, path=path, prefix=prefix):
                        self.assertIn(f"Edit({prefix}{path})", rules)

    AUTO_IMPORTED = (
        ".npmrc",
        "npm-shrinkwrap.json",
        "vitest.config.ts",
        "vitest.config.js",
        "vitest.config.mts",
        "vite.config.ts",
        "vite.config.js",
        "vite.config.mts",
        "eslint.config.mjs",
        "eslint.config.cjs",
        "prettier.config.js",
        "prettier.config.cjs",
        "prettier.config.mjs",
        ".prettierrc.js",
        ".prettierrc.cjs",
        ".prettierrc.json",
        "karma.conf.js",
        "jest.config.js",
    )

    MSBUILD_AUTO_IMPORTED = (
        "Directory.Build.targets", "Directory.Build.rsp", "MSBuild.rsp", "msbuild.rsp",
        "Directory.Solution.props", "Directory.Solution.targets",
        "NuGet.Config", "nuget.config", "NuGet.config",
        "BlueprintAdmin.slnx.targets", "before.BlueprintAdmin.slnx.targets",
        "after.BlueprintAdmin.slnx.targets",
    )

    MSBUILD_FAMILIES = (
        "**/Directory.Build.*", "**/Directory.Solution.*", "**/NuGet.Config",
        "**/nuget.config", "**/NuGet.config", "**/*.slnx.targets",
        "**/Directory.Packages.*", "**/*.csproj.user", "**/obj/**",
        "**/MSBuild.rsp", "**/msbuild.rsp", "**/global.json",
    )

    TRACKED_BY_DESIGN = ("nuget.config",)

    def _msbuild_rules(self):
        rules = []
        for name in self.MSBUILD_AUTO_IMPORTED:
            rules += [f"Edit({name})", f"Edit(./{name})"]
        return rules + [f"Edit({family})" for family in self.MSBUILD_FAMILIES]

    def test_settings_denies_the_msbuild_and_nuget_auto_imports(self):
        denied = set(json.loads(
            SETTINGS.read_text(encoding="utf-8"))["permissions"]["deny"])
        for rule in self._msbuild_rules():
            with self.subTest(rule=rule):
                self.assertIn(rule, denied)

    def test_settings_denies_the_files_claude_code_loads_as_instructions(self):
        # #343: `.claude/rules/**`, `.claude/output-styles/**` and `CLAUDE.local.md` are read
        # into the session's context unasked. Nothing here writes them. `CLAUDE.md` is NOT in
        # this list: the commands deny it themselves, and `/style-pass` and the owner's own
        # sessions edit it by design (docs/harness-boundaries.md).
        denied = set(json.loads(
            SETTINGS.read_text(encoding="utf-8"))["permissions"]["deny"])
        for rule in ("Edit(.claude/rules/**)", "Edit(./.claude/rules/**)",
                     "Edit(.claude/output-styles/**)", "Edit(./.claude/output-styles/**)",
                     "Edit(CLAUDE.local.md)", "Edit(./CLAUDE.local.md)"):
            with self.subTest(rule=rule):
                self.assertIn(rule, denied)
        self.assertNotIn("Edit(CLAUDE.md)", denied)
        self.assertNotIn("Edit(.claude/**)", denied)

    def test_the_msbuild_and_nuget_names_are_protected_files_and_untracked(self):
        # Untracked save TRACKED_BY_DESIGN; protected without exception.
        spec = importlib.util.spec_from_file_location("guard_git_argv", HOOK)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        tracked = self.tracked_root_files()
        for name in self.MSBUILD_AUTO_IMPORTED:
            with self.subTest(name=name):
                if name not in self.TRACKED_BY_DESIGN:
                    self.assertNotIn(name, tracked)
                self.assertIsNotNone(module.denied_path(name))
        for written in ("src/Admin.Host/Directory.Build.targets",
                        "src/Admin.Host/Admin.Host.csproj.user",
                        "artifacts/obj/Admin.Host/Admin.Host.csproj.nuget.g.targets"):
            with self.subTest(written=written):
                self.assertIsNotNone(module.denied_path(written))
        self.assertIsNone(module.denied_path("src/Admin.Host/Admin.Host.csproj"))

    def test_the_check_runner_helper_takes_no_free_parameter(self):
        # And the helper it was replaced by leaves nothing to steer: the script
        # names and the flags are literals, and the one argument is matched
        # against a fixed case rather than passed on.
        source = (SCRIPTS / "npm-checks.sh").read_text(encoding="utf-8")
        self.assertIn("npm run lint", source)
        self.assertIn("npm test", source)
        self.assertIn("npm run build", source)
        self.assertNotIn('"$@"', source)
        self.assertNotIn("$mode\"", source.replace('"$mode" in', ""))

    def test_the_check_runner_offers_no_e2e_mode(self):
        # Playwright needs a browser download and the backend's Compose stack.
        # A mode that ran it where neither exists would pass because nothing
        # was listening, which is the fail-open shape every gate in this
        # repository is written against. CI owns that suite.
        source = (SCRIPTS / "npm-checks.sh").read_text(encoding="utf-8")
        self.assertNotIn("npm run e2e", source)

    def test_the_repository_tracks_no_symbolic_link(self):
        # A path deny is held by spelling, and
        # a tracked symbolic link inside an allowed tree is a spelling the deny
        # never sees while its target can be anywhere. The command states the
        # premise that no such link is tracked and that an invocation whose
        # only writers are `Write` and `Edit` cannot add one; this case is what
        # makes the first half a gate on every push rather than a sentence
        # about one checkout. **It is defence in depth now rather than the
        # closure**: #181 put the check at edit time —
        # `.claude/hooks/guard-edit-target.py`, whose own suite is
        # `test_edit_target_guard.py` — because this case is a statement about
        # `main` and the exposure was always the branch under review. Keeping
        # it costs nothing and the two fail in different directions: this one
        # goes red when a link is committed, the guard when one is written
        # through.
        out = subprocess.run(
            [GIT, "ls-files", "-s"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        entries = out.stdout.splitlines()
        # The positive control: the parse is over a real listing, so an empty
        # result would be a broken command rather than a clean tree.
        self.assertGreater(len(entries), 100, "git ls-files -s returned almost nothing")
        links = [e for e in entries if e.startswith("120000 ")]
        self.assertEqual([], links, f"tracked symbolic links: {links}")

    def test_this_repository_has_no_tracked_link_to_refuse(self):
        # The positive control, and the reason the case above is a source test:
        # if `main` carried a link the sweeps could not run at all, so this
        # asserts the refusal is not silently blocking every sweep.
        out = subprocess.run(
            [GIT, "ls-tree", "-r", "HEAD"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True)
        self.assertEqual(0, out.returncode, out.stderr)
        links = [l for l in out.stdout.splitlines() if l.startswith("120000 ")]
        self.assertEqual([], links)


class HarnessChecksFindsAGenericPyLauncher(unittest.TestCase):
    """Windows `py -3.12` is not the only 3.12+ the helper can use.

    A host with 3.13 registered and no 3.12 fails the exact selector;
    run-guard.sh already probes `py -3` next. harness-checks.sh did not, so
    /pr, /review-branch and /ship refused a machine that satisfied the floor.
    """

    HELPER = SCRIPTS / "harness-checks.sh"

    def test_the_probe_order_matches_the_hook_launcher(self):
        code = "\n".join(
            line for line in self.HELPER.read_text(encoding="utf-8").splitlines()
            if not line.lstrip().startswith("#"))
        py312 = code.find("py -3.12")
        py3 = code.find("py -3 ")
        python3 = code.find("command -v python3")
        python = code.find("command -v python ")
        self.assertNotEqual(py312, -1)
        self.assertNotEqual(py3, -1)
        self.assertLess(py312, py3)
        self.assertLess(py3, python3)
        self.assertLess(python3, python)

    def test_a_py_with_only_3_13_still_runs_the_suite(self):
        bin_dir = Path(tempfile.mkdtemp(prefix="harness-py-"))
        self.addCleanup(shutil.rmtree, str(bin_dir), ignore_errors=True)
        log = bin_dir / "ran"
        py = bin_dir / "py"
        py.write_text(
            "#!/bin/sh\n"
            'if [ "$1" = -3.12 ]; then exit 1; fi\n'
            'if [ "$1" = -3 ]; then shift; fi\n'
            'if [ "$1" = -c ]; then exit 0; fi\n'
            f'printf "%s\\n" "$*" >> {log.as_posix()!r}\n'
            "exit 0\n",
            encoding="utf-8", newline="\n")
        py.chmod(0o755)
        tools = {os.path.dirname(shutil.which(t)) for t in
                 ("dirname", "sh", "git")}
        path = os.pathsep.join([str(bin_dir), *sorted(p for p in tools if p)])
        out = subprocess.run(
            [BASH, str(self.HELPER)],
            capture_output=True, text=True,
            env={**os.environ, "PATH": path},
            cwd=str(SCRIPTS.parent.parent))
        self.assertEqual(0, out.returncode, out.stderr)
        ran = log.read_text(encoding="utf-8").splitlines()
        # The discovery roots moved into the shard runner, which holds the
        # only copy of what they are, and `test_harness_shards.py` is what
        # checks each is still reached. This case's own subject is narrower:
        # that a `py` satisfying the floor only as `-3` gets as far as running
        # the suite at all.
        self.assertTrue(
            any("shard-harness-suite.py" in line for line in ran), ran)

class TheLocalRoots(unittest.TestCase):
    """The roots the payload's runner does not know are run in two places, and the two lists are one fact."""

    def roots(self, text):
        return re.findall(r"discover -s (?!\.claude/scripts)(\S+) -p '(\S+)'", text)

    def test_the_check_and_the_ci_job_run_the_same_roots(self):
        checks = (SCRIPTS / "harness-checks.sh").read_text(encoding="utf-8")
        ci = (SCRIPTS.parent.parent / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        found = self.roots(checks)
        self.assertGreaterEqual(len(found), 5, found)
        self.assertEqual(found, self.roots(ci))


if __name__ == "__main__":
    unittest.main()
