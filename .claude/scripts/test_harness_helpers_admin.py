"""This repository's own cases from the old helpers suite: its stack, its tree and its local scripts.

The payload's suites hold everything shared. What stays here reads this repository's tracked tree, its .NET and
npm toolchain, the settings its config renders, or a script `.harness/config.json` keeps local.

Run: py -3.12 -m unittest test_harness_helpers_admin, from .claude/scripts.
Needs bash, git and jq on PATH; needs no network, no Docker, no gh and no SDK.
"""

import importlib.util
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
HOOK = SCRIPTS.parent / "hooks" / "guard-git-argv.py"

BASH = shutil.which("bash")
GIT = shutil.which("git")
JQ = shutil.which("jq")


def setUpModule():
    # Not a skip. A skip on a missing tool reports a pass, which is the
    # fail-open every gate in this repository is written against — a check
    # that cannot fail makes everything behind it unverified. Absent any of
    # these, this suite has established nothing and says so.
    missing = [
        name for name, path in
        (("bash", BASH), ("git", GIT), ("jq", JQ))
        if path is None
    ]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests exercise "
            "the same tools the scripts do, and asserting through Python "
            "equivalents instead would be testing a second specification"
        )


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

    def test_the_auto_load_surface_is_denied(self):
        # `tracked_root_files` enumerates what exists, and the dangerous file is one that does not: the Node
        # toolchain loads these names by executing them, and `/review-branch` holds `Write` and a build runner.
        for name in self.SUBJECTS:
            rules = self.disallowed(name)
            for target in self.AUTO_IMPORTED:
                with self.subTest(command=name, target=target):
                    self.assertIn(f"Edit({target})", rules)
                    self.assertIn(f"Edit(./{target})", rules)

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
        # this list: `/style-pass` and the owner's own sessions edit it by design.
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
        # That a `py` satisfying the floor only as `-3` gets as far as running the suite at all.
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
