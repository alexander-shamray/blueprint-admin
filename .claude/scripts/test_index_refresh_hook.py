"""The two hooks that keep the local index current.

**Their failures are invisible, so this file checks both what they are and
what they do.** The wiring discards its output, and the script detaches its
own refresh so that it can never block a tool call — which also means a hook
that fails on every call looks exactly like one that works. So the wiring is asserted from the
files the harness reads, and `refresh-index.sh` — the hook command's whole
body — is run against a fake
`codebase-index` that records the arguments and the guard it was handed.

**The registration is read across every event, not under the one being
asserted.** A read naming `PostToolUse` compares two files that agree there
and leaves them free to disagree under an event nobody has added yet, which is
how a gate stops covering the newest surface without saying so.

**Not a skip where `sh` or `git` is missing.** A skip reports a pass, which is
the fail-open `test_grok_helpers.py` refuses for the same tools.

**What `update` itself does after a Bash call is not tested here.** A commit
moving the recorded HEAD, a rewritten file becoming searchable and a call that
wrote nothing changing nothing are the CLI's behaviour, and no CI job installs
the CLI; what this file holds is that a Bash call reaches `update`, in the
checkout the event names.
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
# The tool that writes through commands — a commit, a formatter, an edit
# script — which the edit guard does not list, because it names no target.
SHELL_TOOL = "Bash"
REFRESH = CLAUDE / "hooks" / "refresh-index.sh"
GUARD_ENV = "CBX_NO_SKILL_AUTO_UPDATE"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where `bash` is WSL's.
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


def refresh_hooks(settings=None):
    """The entries running the refresh, keyed by the event that runs them."""
    document = read_json(settings or SETTINGS)
    running = {}
    for event, entries in (document.get("hooks") or {}).items():
        matched = [
            (entry.get("matcher") or "", h)
            for entry in entries
            for h in (entry.get("hooks") or [])
            if REFRESH.name in (h.get("command") or "")
        ]
        if matched:
            running[event] = matched
    return running


def example_command():
    """The one command the shipped example carries, under every event.

    A string rather than a token list, because the runtime test below says
    tokens cannot establish backgrounding: quoting `&` or a redirection gives
    `shlex.split` the same list while handing them to the CLI as arguments.
    Pinning every event to the literal that `TheRefresh` actually executes is
    what carries that runtime check to the event it does not run.
    """
    return f"{GUARD_ENV}={guard_value()} codebase-index update >/dev/null 2>&1 &"


def refresh_hook():
    """The `PostToolUse` registration — the one an edit runs."""
    found = refresh_hooks().get("PostToolUse", [])
    assert len(found) == 1, f"expected one refresh hook: {found}"
    return found[0]


class TheWiring(unittest.TestCase):

    def test_it_runs_after_every_tool_that_writes(self):
        # Derived from the edit guard's own list, so a tool added there is a red
        # case here rather than a file the index silently stops following. Bash
        # joins it, because a commit or a formatter writes as surely as an edit.
        matcher, _ = refresh_hook()
        tools = editing_tools()
        self.assertGreaterEqual(len(tools), 4, f"EDITING_TOOLS shrank: {tools}")
        tools = (*tools, SHELL_TOOL)
        for tool in tools:
            with self.subTest(tool=tool):
                self.assertTrue(re.fullmatch(matcher, tool),
                                f"{matcher!r} does not select {tool}")
        for alternative in matcher.split("|"):
            with self.subTest(alternative=alternative):
                self.assertIn(alternative, tools)

    def test_it_is_silenced_and_reads_its_event(self):
        # Silenced, whole, since anything the script prints would be the hook's
        # output. No `&`: an asynchronous list's stdin is `/dev/null`, so a
        # backgrounded hook would never see the event's `cwd`. The script
        # detaches its own refresh, and `TheEvent` runs this command to hold it
        # to returning before the `update` starts.
        _, hook = refresh_hook()
        self.assertEqual(
            'sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/refresh-index.sh"'
            " >/dev/null 2>&1",
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

    def test_it_also_runs_when_a_session_starts(self):
        # A merge, a switch or a pull rewrites the tree with no tool event
        # behind it. Both events carry the `cwd` the script reads, so the
        # command is the PostToolUse one unchanged, and a divergence between
        # them would mean one of the two events had stopped refreshing anything.
        found = refresh_hooks().get("SessionStart", [])
        self.assertEqual(1, len(found), found)
        matcher, hook = found[0]
        self.assertEqual("", matcher, "SessionStart selects every source")
        self.assertEqual(refresh_hook()[1]["command"], hook["command"])
        self.assertLessEqual(hook.get("timeout", 60), 5)

    def test_no_other_event_runs_it(self):
        # The registration surface is the subject here, not what it holds: a
        # third event added to either file is a red case rather than a silent
        # widening of when the index is rebuilt.
        self.assertEqual({"PostToolUse", "SessionStart"}, set(refresh_hooks()))

    def test_the_shipped_example_is_a_self_contained_guarded_refresh(self):
        # The example is what a reader copies into another project, where this
        # repository's `refresh-index.sh` does not exist — so it stays a
        # one-liner, and is held to the same guard, verb, events and matchers
        # as the wiring. Being one, it reads no event, and refreshes the
        # working directory's checkout.
        events = read_json(EXAMPLE).get("hooks", {})
        self.assertEqual(set(refresh_hooks()), set(events))
        for event, entries in events.items():
            hooks = [(e.get("matcher") or "", h)
                     for e in entries for h in e.get("hooks", [])]
            with self.subTest(event=event):
                self.assertEqual(1, len(hooks), hooks)
                matcher, hook = hooks[0]
                self.assertEqual(refresh_hooks()[event][0][0], matcher)
                # Equality, not tokens: `TheRefresh` runs this exact string
                # for one event only, and matching it is what holds the other
                # to a backgrounding no token list can assert.
                self.assertEqual(example_command(), hook["command"])
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
        return subprocess.run([SH, str(REFRESH), "--detached"], cwd=str(self.repo),
                              env=self.env(), capture_output=True, text=True,
                              timeout=60)

    def calls(self):
        if not self.record.exists():
            return []
        return self.record.read_text(encoding="utf-8").splitlines()

    def start_refresh(self):
        return subprocess.Popen([SH, str(REFRESH), "--detached"], cwd=str(self.repo),
                                env=self.env(), stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL)

    def wait_for(self, condition, what, seconds=30):
        deadline = time.monotonic() + seconds
        while not condition():
            if time.monotonic() > deadline:
                raise AssertionError(f"timed out waiting for {what}")
            time.sleep(0.05)

    def pending(self):
        # On Windows an open that meets the script's `mv -f` mid-rename is
        # refused with a sharing violation instead of reading either token, so
        # it is retried; a mark that stays locked is still a failure.
        path = self.cache / "refresh.pending"
        for _ in range(100):
            try:
                return (path.read_text(encoding="utf-8").strip()
                        if path.exists() else "")
            except PermissionError:
                time.sleep(0.02)
        raise AssertionError(f"{path} stayed locked")

    def mark_id(self):
        """The mark's file identity, read without opening it for data.

        A wait on a second publish polls this rather than `pending()`: on
        Windows a read open shares no delete access, so a read that lands on
        the script's `mv -f` refuses it the replace, and the script stands
        down unpublished. A stat takes no part in sharing, and a rename
        installs a new file, so this changes exactly when a call publishes.
        """
        try:
            return (self.cache / "refresh.pending").stat().st_ino
        except FileNotFoundError:
            return None

    def publish(self, token):
        """Put a later call's token on the mark.

        The mark is the whole of what `refresh-index.sh` reads to decide
        whether to run, so writing one is how a case establishes "a later call
        arrived" without racing a second process into a window two seconds
        wide. Written in place rather than renamed onto: the script's own
        atomic rename is what keeps two *calls* from tearing each other, and
        a torn read cannot make a case here pass by accident — the script
        compares the mark against its own token, and half of this one is not
        that.
        """
        (self.cache / "refresh.pending").write_text(
            token + "\n", encoding="utf-8", newline="\n")

    def held_until(self, release):
        """Fake-CLI lines that hold an update open until `release` appears.

        How long an update runs is the thing a concurrency case has to
        control, and `sleep` does not control it: a sleep long enough on an
        idle host is a stopwatch on a loaded one, and the stopwatch is what
        lost. The iteration bound is so a case that never releases fails on
        its own wait rather than leaving a shell running.
        """
        return (f'n=0\n'
                f'until [ -e {release.as_posix()!r} ] || [ "$n" -ge 300 ]; do\n'
                f'  n=$((n + 1)); sleep 0.1\n'
                f'done\n')

    def test_it_runs_update_once_with_the_guard_and_leaves_only_the_mark(self):
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual(["refresh.pending"],
                         sorted(p.name for p in self.cache.iterdir()))

    def test_the_hook_command_itself_reaches_the_cli(self):
        # The string from settings.json, through the shell that runs it, so an
        # error in the command line itself is a red case and not a quiet one.
        _, hook = refresh_hook()
        started = subprocess.run([SH, "-c", hook["command"]], cwd=str(self.repo),
                                 env=self.env(), capture_output=True, text=True,
                                 input="", timeout=30)
        self.assertEqual(0, started.returncode, started.stderr)
        self.wait_for(self.calls, "the fake CLI to be called")
        self.assertEqual([f"{guard_value()} update"], self.calls())

    def test_the_shipped_example_runs_backgrounded_and_silenced(self):
        # Its tokens alone cannot say this: quoting `&` and the redirections
        # gives `shlex.split` the same list while handing them to the CLI as
        # arguments. So the literal command runs, against a fake that prints
        # and outlasts the shell — a foreground call would be slow and loud.
        self.fake(extra="echo loud\nsleep 3\n")
        entries = read_json(EXAMPLE)["hooks"]["PostToolUse"]
        command = entries[0]["hooks"][0]["command"]
        # The line every event is pinned to, so running it here covers them
        # all. A divergence makes this test the one that fails, rather than
        # leaving an unrun event holding a command nothing executed.
        self.assertEqual(example_command(), command)
        began = time.monotonic()
        shell = subprocess.run([SH, "-c", command], cwd=str(self.repo),
                               env=self.env(), capture_output=True, text=True,
                               timeout=30)
        self.assertLess(time.monotonic() - began, 2, "the example did not background")
        self.assertEqual(0, shell.returncode, shell.stderr)
        self.assertEqual("", shell.stdout + shell.stderr)
        self.wait_for(self.calls, "the fake CLI to be called")
        self.assertEqual([f"{guard_value()} update"], self.calls())

    def test_a_later_call_makes_an_earlier_one_stand_down(self):
        # The coalescing rule on its own, with no second process in it: a call
        # runs only if its token is still the published one. The later token is
        # republished for as long as the first process lives, so whenever that
        # process reaches its check — after two seconds on an idle host, after
        # however long on a loaded one — the token it published is certainly
        # not the pending one. There is no window to lose.
        first = self.start_refresh()
        self.addCleanup(first.wait)
        self.wait_for(self.pending, "the first call to publish")
        deadline = time.monotonic() + 60
        while first.poll() is None:
            self.publish("a-later-call")
            if time.monotonic() > deadline:
                raise AssertionError("the first call never exited")
            time.sleep(0.02)
        self.assertEqual(0, first.returncode)
        self.assertEqual([], self.calls())

    def test_a_burst_of_edits_runs_one_update_and_it_is_the_last_calls(self):
        # Two real calls, and the relation that holds however they interleave:
        # the update the CLI ends on carries the token published last, and two
        # callers cannot produce three updates. Whether the first also ran is a
        # race between one process reaching its check and another reaching its
        # publish, which nothing here can settle — asserting the coalesced list
        # outright is what failed under load. The stand-down the list used to
        # carry is asserted above instead, where it can be made to hold.
        self.fake(extra=f'cat {(self.cache / "refresh.pending").as_posix()!r} '
                        f'>> {self.record.as_posix()!r}\n')
        first = self.start_refresh()
        self.addCleanup(first.wait)
        self.wait_for(self.pending, "the first call to publish")
        first_mark = self.mark_id()
        second = self.start_refresh()
        self.addCleanup(second.wait)
        self.wait_for(lambda: self.mark_id() != first_mark,
                      "the second call to publish")
        last_token = self.pending()
        self.assertEqual(0, first.wait(timeout=60))
        self.assertEqual(0, second.wait(timeout=60))
        calls = self.calls()
        self.assertEqual([f"{guard_value()} update", last_token], calls[-2:], calls)
        self.assertLessEqual(len(calls), 4, calls)

    def test_an_edit_during_an_update_gets_an_update_after_it(self):
        # The first update is held open until this test releases it, so the
        # second call provably publishes while the first is still running —
        # where `sleep 3` only made that likely, and lost. Both observables are
        # state: the record file gains a line when the update starts, and the
        # mark changes when the edit publishes.
        release = self.tmp / "release"
        self.fake(extra=self.held_until(release))
        first = self.start_refresh()
        self.addCleanup(first.wait)
        self.wait_for(self.calls, "the first update to start")
        first_mark = self.mark_id()
        second = self.start_refresh()
        self.addCleanup(second.wait)
        self.wait_for(lambda: self.mark_id() != first_mark,
                      "the edit to publish while the update runs")
        release.touch()
        self.assertEqual(0, first.wait(timeout=60))
        self.assertEqual(0, second.wait(timeout=60))
        self.assertEqual(2, len(self.calls()), self.calls())

    def test_a_failed_update_is_retried(self):
        # The fake fails its first call only, as a transient lock timeout would.
        once = self.record.as_posix() + ".failed"
        self.fake(extra=f'[ -e {once!r} ] || {{ : > {once!r}; exit 1; }}\n')
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(self.calls()), self.calls())

    def test_a_failure_that_persists_gives_up(self):
        self.fake(extra="exit 1\n")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(3, len(self.calls()), self.calls())

    def test_no_index_means_no_update(self):
        shutil.rmtree(self.cache)
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())


class TheSeed(unittest.TestCase):
    """A linked worktree's index, taken from its main checkout's, never built.

    The worktree is a real `git worktree add` of a scratch repository, because
    the script tells a worktree from a checkout by what `git rev-parse` says
    and a hand-made `.git` file would be a claim about git rather than git.
    The fake CLI records whether the seed was in place when `update` ran: an
    update over no index is the full build this exists to avoid, and a call
    count alone cannot tell the two apart.
    """

    setUpClass = TheRefresh.setUpClass
    fake = TheRefresh.fake
    env = TheRefresh.env
    calls = TheRefresh.calls

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="index-seed-"))
        self.addCleanup(shutil.rmtree, str(self.tmp), ignore_errors=True)
        self.main = self.tmp / "main"
        git = [GIT, "-c", "user.name=t", "-c", "user.email=t@example.invalid"]
        subprocess.run([GIT, "init", "-q", str(self.main)], check=True)
        subprocess.run([*git, "-C", str(self.main), "commit", "-q",
                        "--allow-empty", "-m", "init"], check=True)
        self.worktree = self.main / ".claude" / "worktrees" / "slug"
        subprocess.run([GIT, "-C", str(self.main), "worktree", "add", "-q",
                        "-b", "slug", str(self.worktree)], check=True)
        self.main_cache = self.main / ".claude" / "cache" / "codebase-index"
        self.cache = self.worktree / ".claude" / "cache" / "codebase-index"
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        self.record = self.tmp / "calls"
        self.fake(extra=self.probe())

    def probe(self):
        """A fake-CLI line recording that the seed was in place at the call."""
        return ('[ -f .claude/cache/codebase-index/index.sqlite ] '
                f'&& echo seeded >> {self.record.as_posix()!r}\n')

    def main_index(self, content="main's index"):
        self.main_cache.mkdir(parents=True)
        (self.main_cache / "index.sqlite").write_text(content, encoding="utf-8")
        # The main checkout's own state, which must stay where it is.
        for other in ("memory.sqlite", "index.sqlite-wal", "refresh.pending"):
            (self.main_cache / other).write_text(other, encoding="utf-8")

    def run_refresh(self):
        return subprocess.run([SH, str(REFRESH), "--detached"], cwd=str(self.worktree),
                              env=self.env(), capture_output=True, text=True,
                              timeout=60)

    def test_a_worktree_with_no_index_is_seeded_from_main_then_updated(self):
        self.main_index()
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update", "seeded"], self.calls())
        self.assertEqual("main's index", (self.cache / "index.sqlite")
                         .read_text(encoding="utf-8"))
        self.assertEqual(["index.sqlite", "refresh.pending"],
                         sorted(p.name for p in self.cache.iterdir()))

    def test_a_worktree_whose_main_has_no_index_gets_nothing(self):
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertFalse(self.cache.exists())

    def test_a_cache_without_an_index_and_nothing_to_seed_gets_nothing(self):
        # A directory left behind by anything else would otherwise pass the
        # checkout's guard, and `update` would refuse it three times over.
        self.cache.mkdir(parents=True)
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertEqual([], list(self.cache.iterdir()))

    def test_an_existing_worktree_index_is_left_alone(self):
        self.main_index()
        self.cache.mkdir(parents=True)
        (self.cache / "index.sqlite").write_text("its own", encoding="utf-8")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update", "seeded"], self.calls())
        self.assertEqual("its own", (self.cache / "index.sqlite")
                         .read_text(encoding="utf-8"))

    def test_the_main_checkout_keeps_its_own_rule(self):
        # Its git dir is its common dir, so a cache with no index still runs
        # `update` there, as it always has. Without that guard it would read
        # its own missing index as nothing to seed from, and stop.
        self.main_cache.mkdir(parents=True)
        result = subprocess.run([SH, str(REFRESH), "--detached"], cwd=str(self.main),
                                env=self.env(), capture_output=True, text=True,
                                timeout=60)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([f"{guard_value()} update"], self.calls())
        self.assertEqual(["refresh.pending"],
                         sorted(p.name for p in self.main_cache.iterdir()))

    def test_a_worktree_of_a_bare_repository_gets_nothing(self):
        # Its common dir is the bare repository, whose parent is no checkout;
        # the index planted there is what a missing `.git` check would copy.
        bare = self.tmp / "bare.git"
        subprocess.run([GIT, "clone", "-q", "--bare", str(self.main),
                        str(bare)], check=True)
        worktree = self.tmp / "from-bare"
        subprocess.run([GIT, "-C", str(bare), "worktree", "add", "-q",
                        "-b", "other", str(worktree)], check=True)
        planted = self.tmp / ".claude" / "cache" / "codebase-index"
        planted.mkdir(parents=True)
        (planted / "index.sqlite").write_text("no checkout's", encoding="utf-8")
        result = subprocess.run([SH, str(REFRESH), "--detached"], cwd=str(worktree),
                                env=self.env(), capture_output=True, text=True,
                                timeout=60)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertFalse((worktree / ".claude" / "cache").exists())

    def test_a_stale_wal_does_not_meet_a_fresh_seed(self):
        # SQLite replays a WAL into whichever database sits beside it.
        self.main_index()
        self.cache.mkdir(parents=True)
        (self.cache / "index.sqlite-wal").write_text("stale", encoding="utf-8")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["index.sqlite", "refresh.pending"],
                         sorted(p.name for p in self.cache.iterdir()))

    def test_a_seed_update_cannot_open_is_removed_for_the_next_call(self):
        self.main_index()
        self.fake(extra=self.probe() + "exit 1\n")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(3, self.calls().count(f"{guard_value()} update"))
        self.assertEqual(["refresh.pending"],
                         sorted(p.name for p in self.cache.iterdir()))

    def test_a_failed_seed_stays_while_a_later_call_may_be_using_it(self):
        # The fake publishes a later token as it fails, as a call started by an
        # edit during a long first update would; that call may hold the file.
        self.main_index()
        pending = self.cache / "refresh.pending"
        self.fake(extra=self.probe()
                  + f"printf 'later\\n' > {pending.as_posix()!r}\n"
                  + "exit 1\n")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(3, self.calls().count(f"{guard_value()} update"))
        self.assertEqual("main's index", (self.cache / "index.sqlite")
                         .read_text(encoding="utf-8"))
        self.assertTrue((self.cache / "index.sqlite.seeded").exists(),
                        "the later caller needs the marker to know")

    def test_a_later_caller_that_fails_on_a_seed_removes_it(self):
        # The seeding call stood down for this one, so the marker is all that
        # says the index is a seed no `update` has opened.
        self.main_index()
        self.fake(extra=self.probe() + "exit 1\n")
        self.cache.mkdir(parents=True)
        (self.cache / "index.sqlite").write_text("torn", encoding="utf-8")
        (self.cache / "index.sqlite.seeded").write_text("", encoding="utf-8")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["refresh.pending"],
                         sorted(p.name for p in self.cache.iterdir()))

    def test_its_own_index_survives_a_failed_update(self):
        # Only a seed this call made is suspect; an index the worktree already
        # had is kept through what may be a transient failure.
        self.main_index()
        self.fake(extra=self.probe() + "exit 1\n")
        self.cache.mkdir(parents=True)
        (self.cache / "index.sqlite").write_text("its own", encoding="utf-8")
        result = self.run_refresh()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("its own", (self.cache / "index.sqlite")
                         .read_text(encoding="utf-8"))


class TheEvent(unittest.TestCase):
    """The hook's own half: which checkout an event refreshes, and how fast.

    Run as the harness runs it — the command from `settings.json` through
    `sh -c`, the payload on stdin — from the main checkout, because that is
    where `${CLAUDE_PROJECT_DIR}` leaves a session that has entered a worktree.
    Both checkouts hold an index, so no seed is involved, and the one refreshed
    is the one whose cache gained the mark.
    """

    setUpClass = TheRefresh.setUpClass
    fake = TheRefresh.fake
    env = TheRefresh.env
    calls = TheRefresh.calls
    wait_for = TheRefresh.wait_for
    held_until = TheRefresh.held_until

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="index-event-"))
        self.addCleanup(shutil.rmtree, str(self.tmp), ignore_errors=True)
        self.main = self.tmp / "main"
        git = [GIT, "-c", "user.name=t", "-c", "user.email=t@example.invalid"]
        subprocess.run([GIT, "init", "-q", str(self.main)], check=True)
        subprocess.run([*git, "-C", str(self.main), "commit", "-q",
                        "--allow-empty", "-m", "init"], check=True)
        self.worktree = self.main / ".claude" / "worktrees" / "slug"
        subprocess.run([GIT, "-C", str(self.main), "worktree", "add", "-q",
                        "-b", "slug", str(self.worktree)], check=True)
        for checkout in (self.main, self.worktree):
            cache = checkout / ".claude" / "cache" / "codebase-index"
            cache.mkdir(parents=True)
            (cache / "index.sqlite").write_text("its own", encoding="utf-8")
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        self.record = self.tmp / "calls"
        self.fake()

    def payload(self, cwd, **fields):
        """A Bash event unless `fields` say otherwise, compact as the harness
        sends it, `cwd` last.

        Last, so any field passed here precedes the key in the text, which is
        the order a copy inside a tool's output would have to win in.
        """
        return json.dumps({"session_id": "s", "hook_event_name": "PostToolUse",
                           "tool_name": SHELL_TOOL, **fields, "cwd": str(cwd)},
                          separators=(",", ":"))

    def edit(self, cwd, path, key="file_path", **fields):
        """An edit tool's event naming `path`, from a session in `cwd`."""
        return self.payload(cwd, tool_name="Edit", **fields,
                            tool_input={key: str(path)})

    def hook(self, stdin, cwd=None, timeout=30):
        return subprocess.run([SH, "-c", refresh_hook()[1]["command"]],
                              cwd=str(cwd or self.main), env=self.env(),
                              capture_output=True, text=True, input=stdin,
                              timeout=timeout)

    def refreshed(self):
        self.wait_for(self.calls, "the fake CLI to be called")
        self.assertEqual([f"{guard_value()} update"], self.calls())
        return [name for name, checkout in (("main", self.main),
                                            ("worktree", self.worktree))
                if (checkout / ".claude" / "cache" / "codebase-index"
                    / "refresh.pending").exists()]

    def test_an_event_refreshes_the_checkout_it_names(self):
        # The issue's case: a Bash call in a worktree, from a session whose
        # project directory is the main checkout.
        result = self.hook(self.payload(self.worktree))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_without_an_event_the_working_directory_decides(self):
        # `git-worktree-fork.sh` runs it with no payload, from the worktree.
        result = self.hook("", cwd=self.worktree)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_directory_that_has_gone_falls_back_to_the_working_directory(self):
        result = self.hook(self.payload(self.tmp / "gone"), cwd=self.worktree)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_cwd_inside_a_tool_output_is_not_the_key(self):
        # A Bash call that printed an event of its own: the copy is escaped
        # inside the output's string, and comes first in the text.
        printed = json.dumps({"cwd": str(self.main)}, separators=(",", ":"))
        payload = self.payload(self.worktree, tool_response={"stdout": printed})
        self.assertLess(payload.index('\\"cwd\\":'), payload.index('"cwd":'),
                        "the copy must come first to be a case")
        result = self.hook(payload)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_an_edit_refreshes_the_checkout_of_the_file_it_names(self):
        # A session in the main checkout editing a worktree's file by
        # absolute path (#101): the worktree's index is refreshed, and the
        # main checkout's, which the edit never touched, is not.
        result = self.hook(self.edit(self.main, self.worktree / "a.txt"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_notebook_edit_names_its_file_the_same_way(self):
        result = self.hook(self.edit(self.main, self.worktree / "a.ipynb",
                                     key="notebook_path"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_relative_path_resolves_against_the_events_cwd(self):
        # Run from the worktree, where the same relative path names nothing:
        # resolved there it would fall back to `cwd`, the main checkout.
        relative = os.path.join(".claude", "worktrees", "slug", "a.txt")
        result = self.hook(self.edit(self.main, relative), cwd=self.worktree)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_path_through_a_worktree_into_main_refreshes_main(self):
        # The `..` is resolved before git looks for a checkout, so the file's
        # home decides and not the directory the path is spelled through.
        through = os.path.join(str(self.worktree), "..", "..", "..", "a.txt")
        result = self.hook(self.edit(self.worktree, through))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["main"], self.refreshed())

    def test_a_file_whose_directory_has_gone_falls_back_to_the_events_cwd(self):
        # A removed worktree's file: its nearest surviving ancestor is in the
        # main checkout, which is not where the session is working.
        gone = self.main / ".claude" / "worktrees" / "gone" / "a.txt"
        result = self.hook(self.edit(self.worktree, gone))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_a_file_path_inside_a_tool_output_is_not_the_key(self):
        printed = json.dumps({"file_path": str(self.main / "a.txt")},
                             separators=(",", ":"))
        payload = self.edit(self.main, self.worktree / "a.txt",
                            tool_response={"stdout": printed})
        self.assertLess(payload.index('\\"file_path\\":'),
                        payload.index('"file_path":'),
                        "the copy must come first to be a case")
        result = self.hook(payload)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["worktree"], self.refreshed())

    def test_the_hook_returns_while_the_update_is_held(self):
        # The update is held open until this test releases it, so a hook that
        # waited on it cannot return at all, and `run`'s timeout — well inside
        # the fake's own bound — is what fails. Nothing is timed: this class
        # shares a box with seven workers, where a stopwatch is what lost. An
        # `&` restored to the command fails below instead, on the checkout,
        # because it takes the payload with it.
        release = self.tmp / "release"
        self.fake(extra=self.held_until(release))
        try:
            result = self.hook(self.payload(self.worktree), timeout=15)
        finally:
            release.touch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout + result.stderr)
        self.assertEqual(["worktree"], self.refreshed())


if __name__ == "__main__":
    unittest.main()
