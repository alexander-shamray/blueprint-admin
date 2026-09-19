"""The `PostToolUse` hook that keeps the local index current (#26).

**The hook's failures are invisible, so this file checks both what it is and
what it does.** The wiring discards its output and runs in the background so
that it can never block an edit — which also means a hook that fails on every
call looks exactly like one that works. The example this repository shipped
did precisely that: `--quiet` is not an option the CLI has, and nothing would
ever have said so. So the wiring is asserted from the files the harness reads,
and `refresh-index.sh` — the hook command's whole body — is run against a fake
`codebase-index` that records the arguments and the guard it was handed.

**Not a skip where `sh` or `git` is missing.** A skip reports a pass, which is
the fail-open `test_grok_helpers.py` refuses for the same tools.
"""

import importlib.util
import json
import os
import re
import shlex
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
ROOT = CLAUDE.parent
SETTINGS = CLAUDE / "settings.json"
EXAMPLE = CLAUDE / "skills" / "codebase-index" / "examples" / "hooks" / "settings.json"
MCP = ROOT / ".mcp.json"
EDIT_GUARD = CLAUDE / "hooks" / "guard-edit-target.py"
REFRESH = CLAUDE / "hooks" / "refresh-index.sh"
GUARD_ENV = "CBX_NO_SKILL_AUTO_UPDATE"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where `bash` is WSL's — measured while writing this hook.
SH = shutil.which("sh")
GIT = shutil.which("git")


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def editing_tools():
    """The tools that write, read from the edit guard rather than listed here."""
    spec = importlib.util.spec_from_file_location("guard_edit_target", EDIT_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.EDITING_TOOLS


def guard_value():
    """The guard's value, read from `.mcp.json`, its owner."""
    return read_json(MCP)["mcpServers"]["codebase-index"]["env"][GUARD_ENV]


def refresh_hook():
    found = [
        (entry.get("matcher") or "", h)
        for entry in read_json(SETTINGS).get("hooks", {}).get("PostToolUse", [])
        for h in (entry.get("hooks") or [])
        if REFRESH.name in (h.get("command") or "")
    ]
    assert len(found) == 1, f"expected one refresh hook: {found}"
    return found[0]


class TheWiring(unittest.TestCase):

    def test_it_runs_after_every_tool_that_writes(self):
        # Derived from the edit guard's own list, so a tool added there is a red
        # case here rather than a file the index silently stops following.
        matcher, _ = refresh_hook()
        tools = editing_tools()
        self.assertGreaterEqual(len(tools), 4, f"EDITING_TOOLS shrank: {tools}")
        for tool in tools:
            with self.subTest(tool=tool):
                self.assertTrue(re.fullmatch(matcher, tool),
                                f"{matcher!r} does not select {tool}")
        for alternative in matcher.split("|"):
            with self.subTest(alternative=alternative):
                self.assertIn(alternative, tools)

    def test_it_can_never_block_an_edit(self):
        # Backgrounded and silenced, whole: a dropped `&` holds every edit for
        # the length of an `update`, and anything the script prints would be
        # the hook's output.
        _, hook = refresh_hook()
        self.assertEqual(
            'sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/refresh-index.sh"'
            " >/dev/null 2>&1 &",
            hook["command"])
        self.assertLessEqual(hook.get("timeout", 60), 5)

    def test_the_update_line_is_exactly_the_guard_and_the_verb(self):
        # A token comparison, not a substring: `=10` contains `=1`, and any flag
        # after `update` is the `--quiet` defect in another spelling.
        lines = [line.strip() for line in REFRESH.read_text(encoding="utf-8")
                 .splitlines() if "codebase-index update" in line
                 and not line.lstrip().startswith("#")]
        self.assertEqual(1, len(lines), lines)
        self.assertEqual(
            [f"{GUARD_ENV}={guard_value()}", "codebase-index", "update"],
            shlex.split(lines[0]))

    def test_the_shipped_example_is_a_self_contained_guarded_refresh(self):
        # The example is what a reader copies into another project, where this
        # repository's `refresh-index.sh` does not exist — so it stays a
        # one-liner, and is held to the same guard, verb and matcher as the
        # wiring. It shipped with a `--quiet` the CLI lacks and no guard.
        entries = read_json(EXAMPLE).get("hooks", {}).get("PostToolUse", [])
        hooks = [(e.get("matcher"), h) for e in entries for h in e.get("hooks", [])]
        self.assertEqual(1, len(hooks), hooks)
        matcher, hook = hooks[0]
        self.assertEqual(refresh_hook()[0], matcher)
        self.assertEqual(
            [f"{GUARD_ENV}={guard_value()}", "codebase-index", "update",
             ">/dev/null", "2>&1", "&"],
            shlex.split(hook["command"]))
        self.assertNotIn(REFRESH.name, hook["command"])


class TheRefresh(unittest.TestCase):
    """`refresh-index.sh` run for real, against a fake CLI in a scratch repo."""

    @classmethod
    def setUpClass(cls):
        missing = [name for name, path in (("sh", SH), ("git", GIT)) if not path]
        if missing:
            raise AssertionError(f"required on PATH: {missing}")

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="index-refresh-"))
        self.addCleanup(shutil.rmtree, str(self.tmp), ignore_errors=True)
        self.repo = self.tmp / "repo"
        self.repo.mkdir()
        subprocess.run([GIT, "init", "-q", str(self.repo)], check=True)
        self.cache = self.repo / ".claude" / "cache" / "codebase-index"
        self.cache.mkdir(parents=True)
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        self.record = self.tmp / "calls"
        self.fake()

    def fake(self, extra=""):
        # One line per call: the guard's value as received, then the argv.
        cli = self.bin / "codebase-index"
        cli.write_text(
            "#!/bin/sh\n"
            f'printf "%s %s\\n" "${{{GUARD_ENV}:-unset}}" "$*" '
            f">> {self.record.as_posix()!r}\n"
            f"{extra}"
            "exit 0\n",
            encoding="utf-8", newline="\n")
        cli.chmod(0o755)

    def env(self):
        return {**os.environ,
                "PATH": os.pathsep.join([str(self.bin), os.environ["PATH"]]),
                "CLAUDE_PROJECT_DIR": str(ROOT)}

    def run_refresh(self):
        return subprocess.run([SH, str(REFRESH)], cwd=str(self.repo),
                              env=self.env(), capture_output=True, text=True,
                              timeout=60)

    def calls(self):
        if not self.record.exists():
            return []
        return self.record.read_text(encoding="utf-8").splitlines()

    def test_it_runs_update_once_with_the_guard_and_leaves_nothing_behind(self):
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual([], sorted(p.name for p in self.cache.iterdir()))

    def test_the_hook_command_itself_reaches_the_cli(self):
        # The string from settings.json, through the shell that runs it, so an
        # error in the command line itself is a red case and not a quiet one.
        _, hook = refresh_hook()
        started = subprocess.run([SH, "-c", hook["command"]], cwd=str(self.repo),
                                 env=self.env(), capture_output=True, text=True,
                                 timeout=30)
        self.assertEqual(0, started.returncode, started.stderr)
        deadline = time.monotonic() + 30
        while not self.calls() and time.monotonic() < deadline:
            time.sleep(0.2)
        self.assertEqual([f"{guard_value()} update"], self.calls())

    def test_an_edit_during_an_update_gets_an_update_after_it(self):
        # The fake marks the index pending from inside its first run, which is
        # what a second edit's hook does while the first update is scanning.
        pending = (self.cache / "refresh.pending").as_posix()
        self.fake(extra=f'[ -e {self.record.as_posix()!r}.once ] || '
                        f'{{ : > {self.record.as_posix()!r}.once; '
                        f': > {pending!r}; }}\n')
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(self.calls()), self.calls())

    def test_a_call_that_finds_the_lock_held_leaves_the_work_to_the_holder(self):
        (self.cache / "refresh.lock").mkdir()
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertTrue((self.cache / "refresh.pending").exists())

    def test_a_stale_lock_is_taken_back(self):
        lock = self.cache / "refresh.lock"
        lock.mkdir()
        old = time.time() - 3600
        os.utime(lock, (old, old))
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual([], sorted(p.name for p in self.cache.iterdir()))

    def holder_token(self, alive):
        """A token naming a real shell: running for the test, or already gone.

        A shell's own `$$`, not this process's PID: on Windows the two
        namespaces differ, and `kill -0` in `sh` reads the shell's.
        """
        record = self.tmp / "holder"
        script = f'echo "$$.0" > {record.as_posix()!r}' + ("; sleep 30" if alive else "")
        shell = subprocess.Popen([SH, "-c", script])
        if alive:
            self.addCleanup(shell.wait)
            self.addCleanup(shell.kill)
        else:
            shell.wait()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if record.exists() and record.read_text(encoding="utf-8").strip():
                return record.read_text(encoding="utf-8").strip()
            time.sleep(0.1)
        raise AssertionError("the holder shell never wrote its PID")

    def old_lock(self, token, minutes=30):
        lock = self.cache / "refresh.lock"
        lock.mkdir()
        (lock / "owner").write_text(token + "\n", encoding="utf-8")
        old = time.time() - minutes * 60
        os.utime(lock, (old, old))
        return lock

    def test_an_old_lock_whose_holder_is_alive_is_left_alone(self):
        # Age alone would displace a holder still inside a slow update and
        # start a second update beside it.
        lock = self.old_lock(self.holder_token(alive=True))
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertTrue(lock.is_dir())
        self.assertTrue((self.cache / "refresh.pending").exists())

    def test_an_old_lock_whose_holder_is_gone_is_taken_back(self):
        self.old_lock(self.holder_token(alive=False))
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual([], sorted(p.name for p in self.cache.iterdir()))

    def test_a_young_lock_whose_holder_is_gone_is_taken_back_at_once(self):
        # Waiting for the lock to age would strand this call's edit: it is the
        # last one, and nothing else comes back for it.
        self.old_lock(self.holder_token(alive=False), minutes=0)
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual([], sorted(p.name for p in self.cache.iterdir()))

    def test_a_lock_past_its_lease_is_taken_back_even_if_its_pid_is_alive(self):
        # A live PID on a two-hour-old lock is a hung holder or a reused PID;
        # either way, honouring it would stop every refresh for good.
        self.old_lock(self.holder_token(alive=True), minutes=120)
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())

    def test_a_holder_taken_over_as_stale_leaves_its_successors_lock(self):
        # A holder that outlasted the threshold returns to find a successor's
        # token in the lock. Freeing it would let a third call start an update
        # beside the successor's, so the holder must leave it and exit.
        owner = (self.cache / "refresh.lock" / "owner").as_posix()
        self.fake(extra=f"printf 'successor\\n' > {owner!r}\n")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(self.calls()), self.calls())
        self.assertEqual(
            "successor",
            (self.cache / "refresh.lock" / "owner").read_text(
                encoding="utf-8").strip())

    def test_a_failed_update_is_retried(self):
        # The fake fails its first call only, as a transient lock timeout would.
        once = self.record.as_posix() + ".failed"
        self.fake(extra=f'[ -e {once!r} ] || {{ : > {once!r}; exit 1; }}\n')
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(self.calls()), self.calls())
        self.assertEqual([], sorted(p.name for p in self.cache.iterdir()))

    def test_a_failure_that_persists_gives_up_and_frees_the_lock(self):
        self.fake(extra="exit 1\n")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(3, len(self.calls()), self.calls())
        self.assertFalse((self.cache / "refresh.lock").exists())

    def test_no_index_means_no_update(self):
        shutil.rmtree(self.cache)
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())


if __name__ == "__main__":
    unittest.main()
