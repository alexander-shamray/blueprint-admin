"""git-worktree-fork.sh: the one worktree shape /branch step 5 creates.

The helper is the whole of `/branch`'s `git worktree add`, so what it refuses
is the boundary: a path anywhere but `.claude/worktrees/<name>`, a run from
anywhere but the main checkout's root, a directory git does not ignore, and a
branch that already exists. Each case below runs the script against a real
repository with an `origin/main` to fork from, rather than a Python reading of
its checks.
"""

import os
import shutil
import subprocess
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
FORK = SCRIPTS / "git-worktree-fork.sh"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where either of these may be WSL's. `git` is run only by bash,
# so it is pinned by putting its directory first on the PATH bash gets.
BASH = shutil.which("bash")
GIT = shutil.which("git")


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass, and this suite would
    # then have established nothing about the helper.
    missing = [name for name, path in (("bash", BASH), ("git", GIT))
               if path is None]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests run "
            "the script rather than a Python equivalent of it"
        )


def run_bash(script, **env_extra):
    """Run a bash fragment with everything it needs in the environment.

    Nothing crosses as an argument: an argv element entering `bash.exe` is
    re-parsed by the MSYS runtime, so a fixture path would arrive mangled.
    Ambient git configuration is not this suite's subject, so the global and
    system files are replaced with nothing.
    """
    env = dict(os.environ)
    env["GIT_CONFIG_GLOBAL"] = "/dev/null"
    env["GIT_CONFIG_SYSTEM"] = "/dev/null"
    env["GIT_CONFIG_NOSYSTEM"] = "1"
    env["PATH"] = os.pathsep.join([str(Path(GIT).parent), env.get("PATH", "")])
    env.update(env_extra)
    return subprocess.run([BASH, "-c", script], capture_output=True,
                          text=True, env=env)


# An origin and a clone of it, so `origin/main` exists to fork from, made by
# bash so the paths are the ones the script sees. `IGNORE` decides whether the
# clone's .gitignore names the worktree directory, which is the check the
# ignored and unignored cases turn on.
FIXTURE = """
set -e
root=$(mktemp -d)
git init -q -b main "$root/origin"
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q --allow-empty -m base
if [ "$HOOK" = yes ]; then
  mkdir -p "$root/origin/.claude/hooks"
  printf '#!/bin/sh\\npwd > hook-ran\\n' > "$root/origin/.claude/hooks/refresh-index.sh"
  git -C "$root/origin" add .claude
  git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m hook
fi
git clone -q "$root/origin" "$root/checkout"
if [ "$IGNORE" = yes ]; then printf '.claude/worktrees/\\n' > "$root/checkout/.gitignore"; fi
mkdir -p "$root/checkout/src"
printf '%s\\n' "$root"
"""


class ForkShape(unittest.TestCase):
    """The helper forks `.claude/worktrees/<name>` from the main checkout only."""

    def fixture(self, ignore="yes", hook="no"):
        made = run_bash(FIXTURE, IGNORE=ignore, HOOK=hook)
        self.assertEqual(0, made.returncode, made.stderr)
        root = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=root))
        return root

    def fork(self, where, path, branch="feat/probe"):
        return run_bash('cd "$WHERE" && bash "$FORK" "$P" "$B"',
                        WHERE=where, FORK=str(FORK), P=path, B=branch)

    def test_the_documented_shape_forks_with_no_upstream(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        listed = run_bash('git -C "$C" worktree list --porcelain',
                          C=f"{root}/checkout")
        self.assertIn("refs/heads/feat/probe", listed.stdout)
        upstream = run_bash(
            'git -C "$C" rev-parse --abbrev-ref feat/probe@{upstream}',
            C=f"{root}/checkout")
        self.assertNotEqual(0, upstream.returncode)
        # The worktree sits inside the checkout, and being ignored is what
        # keeps it out of every `git status` the chain reads.
        status = run_bash('git -C "$C" status --porcelain --ignored=no',
                          C=f"{root}/checkout")
        self.assertEqual(0, status.returncode, status.stderr)
        self.assertNotIn(".claude", status.stdout)

    def test_the_new_worktree_runs_its_own_index_refresh(self):
        # /branch enters the worktree mid-session, where no `SessionStart`
        # fires, so the fork starts the refresh that hook would have. The stub
        # is committed on origin/main, so the copy that runs is the worktree's.
        root = self.fixture(hook="yes")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        ran = run_bash(
            'for _ in $(seq 300); do '
            '[ -s "$W/hook-ran" ] && exec cat "$W/hook-ran"; sleep 0.1; '
            'done; exit 1',
            W=f"{root}/checkout/.claude/worktrees/probe")
        self.assertEqual(0, ran.returncode, "the fork never ran the refresh")
        self.assertTrue(ran.stdout.strip().endswith("/.claude/worktrees/probe"),
                        ran.stdout)

    def test_any_other_path_is_refused(self):
        root = self.fixture()
        for path in ("../sibling", ".claude/worktrees/../x",
                     ".claude/worktrees/a/b", "/tmp/x",
                     ".claude/worktrees/-f"):
            with self.subTest(path=path):
                result = self.fork(f"{root}/checkout", path)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("must be .claude/worktrees/<name>", result.stderr)
        # Refused before git ran: nothing was created on the way.
        branches = run_bash('git -C "$C" branch --list feat/probe',
                            C=f"{root}/checkout")
        self.assertEqual("", branches.stdout.strip())

    def test_an_unignored_path_is_refused(self):
        root = self.fixture(ignore="no")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("is not ignored", result.stderr)

    def test_a_subdirectory_is_refused(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout/src", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("checkout root", result.stderr)

    def test_a_linked_worktree_is_refused(self):
        root = self.fixture()
        first = self.fork(f"{root}/checkout", ".claude/worktrees/first",
                          "feat/first")
        self.assertEqual(0, first.returncode, first.stderr)
        result = self.fork(f"{root}/checkout/.claude/worktrees/first",
                           ".claude/worktrees/second", "feat/second")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("not a linked worktree", result.stderr)

    def test_an_existing_branch_is_refused_rather_than_reset(self):
        root = self.fixture()
        made = run_bash('git -C "$C" branch feat/probe', C=f"{root}/checkout")
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("branch already exists", result.stderr)

    def test_outside_a_repository_says_so(self):
        made = run_bash('mktemp -d')
        self.assertEqual(0, made.returncode, made.stderr)
        outside = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=outside))
        result = self.fork(outside, ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("not in a git repository", result.stderr)


if __name__ == "__main__":
    unittest.main()
