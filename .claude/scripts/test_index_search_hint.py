"""The hook that asks the index before a session's first symbol search.

**A hint that never fires looks exactly like one that is working**, and this
one has already failed that way once in a sibling: shipped as a note after the
search's result, it was read and ignored (#119, #120). So this file runs the
hook itself, through its launcher, on the searches it must refuse once, on the
searches it must never touch, and on the wiring that decides whether it runs
at all.

The subject is *what the hook looks at*, not only what it finds: a grep for
text, a message or one file must stay unrefused, and the first search for a
name must be refused exactly once per session however it is spelled.

**Not a skip where `bash` or Python is missing.** A skip reports a pass, which
is the fail-open `test_index_refresh_hook.py` refuses for the same tools.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
ROOT = CLAUDE.parent
SETTINGS = CLAUDE / "settings.json"
HOOK = CLAUDE / "hooks" / "index-search-hint.py"
LAUNCHER = CLAUDE / "hooks" / "index-hint-run.sh"
PROMPT_HINT = CLAUDE / "hooks" / "index-query-hint.sh"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where `bash` is WSL's.
BASH = shutil.which("bash")

# How long the launcher may let the hook run in these tests. Its own bound is 10 s,
# which a loaded Windows runner overruns while Python starts, and the launcher then
# prints its "did not run" note instead of the verdict (#471). Under the 60 s each
# `subprocess.run` below allows, so a hung hook still ends as that note rather than
# as a kill by the test that leaves an orphaned child holding a pipe.
LOAD_BOUND = "50"


def setUpModule():
    if BASH is None:
        raise RuntimeError("bash required and not on PATH: these tests run "
                           "the launcher rather than a Python equivalent of it")


def load_hook():
    import importlib.util
    spec = importlib.util.spec_from_file_location("index_search_hint", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class Session:
    """A throwaway checkout, a transcript and a private temp directory, so one
    case's refusal record can never be another's."""

    def __init__(self, case, worktree_of=None):
        self.case = case
        self.dir = Path(tempfile.mkdtemp())
        case.addCleanup(shutil.rmtree, self.dir, True)
        self.tmp = self.dir / "tmp"
        self.tmp.mkdir()
        self.main = self.dir / "main"
        (self.main / ".git").mkdir(parents=True)
        (self.main / "src").mkdir()
        (self.main / "src" / "Program.cs").write_text("class Program {}\n")
        (self.main / "docs").mkdir()
        self.transcript = self.dir / "session.jsonl"
        self.transcript.write_text("")
        # A linked worktree: a `.git` file whose gitdir holds a `commondir`.
        self.tree = self.main
        if worktree_of:
            gitdir = self.main / ".git" / "worktrees" / "wt"
            gitdir.mkdir(parents=True)
            (gitdir / "commondir").write_text("../..\n")
            self.tree = self.dir / "wt"
            (self.tree / "src").mkdir(parents=True)
            (self.tree / ".git").write_text(f"gitdir: {gitdir}\n")
        self.name = f"s{abs(hash(case.id())) % 10**8}"

    def event(self, tool, **given):
        return {"session_id": self.name, "transcript_path": str(self.transcript),
                "cwd": str(self.tree), "hook_event_name": "PreToolUse",
                "tool_name": tool, "tool_input": given}

    def run(self, event, project=None, launcher=True):
        """The hook's stdout, stderr and status for one event, through the
        launcher the wiring names or, for the verdict cases, directly. The
        session's start folder is `project`, which is what the MCP server
        serves."""
        env = dict(os.environ)
        env.update({"CLAUDE_PROJECT_DIR": str(project or (ROOT if launcher else self.main)),
                    "HINT_BOUND_SECONDS": LOAD_BOUND,
                    "TMP": str(self.tmp), "TEMP": str(self.tmp), "TMPDIR": str(self.tmp)})
        if launcher:
            argv = [BASH, str(LAUNCHER), "PreToolUse", "index-search-hint.py"]
        else:
            argv = [sys.executable, "-P", str(HOOK)]
        done = subprocess.run(argv, input=json.dumps(event).encode("utf-8"),
                              capture_output=True, env=env, timeout=60)
        return done.stdout.decode("utf-8"), done.stderr.decode("utf-8"), done.returncode

    def verdict(self, event, project=None):
        out, err, status = self.run(event, project, launcher=False)
        self.case.assertEqual((0, ""), (status, err))
        if not out.strip():
            return None
        answer = json.loads(out)["hookSpecificOutput"]
        self.case.assertEqual("PreToolUse", answer["hookEventName"])
        self.case.assertEqual("deny", answer["permissionDecision"])
        return answer["permissionDecisionReason"]


class TheFirstSymbolSearchIsRefusedOnce(unittest.TestCase):

    def test_a_grep_for_a_name_is_refused_with_the_lookup_that_answers_it(self):
        s = Session(self)
        reason = s.verdict(s.event("Grep", pattern="RetentionMapRule", path="src"))
        self.assertIn("`RetentionMapRule`", reason)
        self.assertIn("mcp__codebase-index__find_refs", reason)
        self.assertIn("mcp__codebase-index__find_symbol", reason)
        self.assertIn("select:", reason)
        self.assertIn("refused once", reason)

    def test_the_same_search_rerun_passes(self):
        s = Session(self)
        first = s.event("Grep", pattern="RetentionMapRule", path="src")
        self.assertIsNotNone(s.verdict(first))
        self.assertIsNone(s.verdict(first))
        # And so does a different name: one refusal per session, not per name.
        self.assertIsNone(s.verdict(s.event("Grep", pattern="OtherRule", path="src")))

    def test_another_session_is_refused_in_its_own_right(self):
        s = Session(self)
        s.name = "one"
        self.assertIsNotNone(s.verdict(s.event("Grep", pattern="RetentionMapRule")))
        s.name = "two"
        self.assertIsNotNone(s.verdict(s.event("Grep", pattern="RetentionMapRule")))

    def test_an_alternation_of_names_is_one_refusal_naming_each(self):
        s = Session(self)
        reason = s.verdict(s.event("Grep", pattern=r"JobRegistry\|ProcessRunner"))
        self.assertIn("`JobRegistry`", reason)
        self.assertIn("`ProcessRunner`", reason)

    def test_a_shell_grep_over_the_tree_is_refused_like_the_tool(self):
        for command in ('grep -rn "RetentionMapRule" src',
                        'rg RetentionMapRule src | head',
                        'cd src && grep -rln "RetentionMapRule" .',
                        'git grep -n RetentionMapRule',
                        'grep -rn -e RetentionMapRule src --include=*.cs'):
            with self.subTest(command=command):
                s = Session(self)
                reason = s.verdict(s.event("Bash", command=command))
                self.assertIn("`RetentionMapRule`", reason)

    def test_the_wiring_reaches_the_hook_through_the_launcher(self):
        s = Session(self)
        out, err, status = s.run(s.event("Grep", pattern="RetentionMapRule", path="src"),
                                 project=ROOT)
        self.assertEqual((0, ""), (status, err))
        self.assertEqual("deny", json.loads(out)["hookSpecificOutput"]["permissionDecision"])


class WhatItNeverTouches(unittest.TestCase):

    def silent(self, tool, **given):
        s = Session(self)
        self.assertIsNone(s.verdict(s.event(tool, **given)), given)

    def test_text_a_message_or_a_pattern_is_grep_to_list_whole(self):
        for pattern in ("timeout", "TODO", "not found", "CLAUDE.md",
                        r"Error: .*failed", "foo.bar"):
            with self.subTest(pattern=pattern):
                self.silent("Grep", pattern=pattern, path="src")

    def test_a_single_file_is_a_read(self):
        self.silent("Grep", pattern="RetentionMapRule", path="src/Program.cs")
        # Each recurses, so only the file test can save it: a plain `grep -n`
        # has no recursion and returns before that test is reached.
        self.silent("Bash", command='grep -rn "RetentionMapRule" src/Program.cs')
        self.silent("Bash", command="rg RetentionMapRule src/Program.cs")
        self.silent("Bash", command="cd src && rg RetentionMapRule Program.cs")

    def test_a_grep_with_no_recursion_reads_a_pipe(self):
        self.silent("Bash", command='cat src/Program.cs | grep RetentionMapRule')

    def test_prose_is_not_a_question_the_index_answers(self):
        self.silent("Grep", pattern="RetentionMapRule", path="docs")
        self.silent("Grep", pattern="RetentionMapRule", glob="*.md")

    def test_other_tools_and_other_trees_are_never_judged(self):
        self.silent("Read", file_path="src/Program.cs")
        s = Session(self)
        elsewhere = str(s.dir)
        self.assertIsNone(s.verdict(
            s.event("Grep", pattern="RetentionMapRule", path=elsewhere)))

    def test_a_subagent_is_never_refused(self):
        s = Session(self)
        event = s.event("Grep", pattern="RetentionMapRule", path="src")
        event["agent_id"] = "a1"
        self.assertIsNone(s.verdict(event))
        self.assertFalse((s.tmp / "claude-index-ask").exists())

    def test_input_it_cannot_read_goes_through(self):
        s = Session(self)
        for payload in ("", "not json", "[1, 2]", '{"tool_name": 7}'):
            with self.subTest(payload=payload):
                env = dict(os.environ, CLAUDE_PROJECT_DIR=str(ROOT), HINT_BOUND_SECONDS=LOAD_BOUND)
                done = subprocess.run(
                    [BASH, str(LAUNCHER), "PreToolUse", "index-search-hint.py"],
                    input=payload.encode(), capture_output=True, env=env, timeout=60)
                self.assertEqual((0, b""), (done.returncode, done.stdout))

    def test_a_session_that_already_asked_the_index_is_not_refused(self):
        calls = [
            {"type": "tool_use", "id": "t1", "name": "mcp__codebase-index__find_refs",
             "input": {"symbol": "X"}},
            {"type": "tool_result", "tool_use_id": "t1", "content": "ok"},
        ]
        s = Session(self)
        s.transcript.write_text(
            "\n".join(json.dumps({"message": {"content": [c]}}) for c in calls) + "\n")
        self.assertIsNone(s.verdict(s.event("Grep", pattern="RetentionMapRule")))

    def test_a_lookup_that_was_itself_refused_does_not_count(self):
        calls = [
            {"type": "tool_use", "id": "t1", "name": "mcp__codebase-index__find_refs",
             "input": {"symbol": "X"}},
            {"type": "tool_result", "tool_use_id": "t1", "is_error": True,
             "content": "denied by a hook"},
        ]
        s = Session(self)
        s.transcript.write_text(
            "\n".join(json.dumps({"message": {"content": [c]}}) for c in calls) + "\n")
        self.assertIsNotNone(s.verdict(s.event("Grep", pattern="RetentionMapRule")))

    def test_the_cli_run_through_bash_counts_as_a_lookup(self):
        call = {"type": "tool_use", "id": "t1", "name": "Bash",
                "input": {"command": 'codebase-index refs "X" --json'}}
        s = Session(self)
        s.transcript.write_text(json.dumps({"message": {"content": [call]}}) + "\n")
        self.assertIsNone(s.verdict(s.event("Grep", pattern="RetentionMapRule")))


class TheRouteFollowsTheServer(unittest.TestCase):

    def test_from_the_checkout_the_session_started_in_it_names_the_mcp_tools(self):
        s = Session(self)
        reason = s.verdict(s.event("Grep", pattern="RetentionMapRule"), project=s.main)
        self.assertIn("mcp__codebase-index__find_refs", reason)
        self.assertNotIn("codebase-index refs", reason)

    def test_from_a_worktree_entered_mid_session_it_names_the_cli_alone(self):
        s = Session(self, worktree_of=True)
        reason = s.verdict(s.event("Grep", pattern="RetentionMapRule"), project=s.main)
        self.assertIn('`codebase-index refs "RetentionMapRule" --json`', reason)
        self.assertIn("`--kind callers`", reason)
        self.assertNotIn("mcp__", reason)

    def test_a_session_started_in_the_worktree_is_served_by_it(self):
        s = Session(self, worktree_of=True)
        reason = s.verdict(s.event("Grep", pattern="RetentionMapRule"), project=s.tree)
        self.assertIn("mcp__codebase-index__find_refs", reason)

    def test_with_no_project_dir_a_linked_worktree_is_taken_to_be_off_it(self):
        s = Session(self, worktree_of=True)
        hook = load_hook()
        saved = os.environ.pop("CLAUDE_PROJECT_DIR", None)
        try:
            self.assertTrue(hook.off_server(str(s.tree)))
            self.assertFalse(hook.off_server(str(s.main)))
        finally:
            if saved is not None:
                os.environ["CLAUDE_PROJECT_DIR"] = saved


class TheLauncherFailsOpen(unittest.TestCase):

    def launch(self, *args, project=ROOT):
        env = dict(os.environ, CLAUDE_PROJECT_DIR=str(project))
        done = subprocess.run([BASH, str(LAUNCHER), *args], input=b"{}",
                              capture_output=True, env=env, timeout=60)
        return done.stdout.decode(), done.returncode

    def test_it_runs_only_the_one_hook_it_names(self):
        for args in (("PreToolUse", "guard-git-argv.py"), ("PreToolUse", "../x.py"),
                     ("UserPromptSubmit", "index-search-hint.py"), ()):
            with self.subTest(args=args):
                out, status = self.launch(*args)
                self.assertEqual(0, status)
                self.assertIn("did not run", json.loads(out)["hookSpecificOutput"]["additionalContext"])

    def test_an_interpreter_that_cannot_find_the_hook_says_so_and_exits_zero(self):
        with tempfile.TemporaryDirectory() as empty:
            out, status = self.launch("PreToolUse", "index-search-hint.py", project=empty)
        self.assertEqual(0, status)
        context = json.loads(out)["hookSpecificOutput"]["additionalContext"]
        self.assertIn("did not run", context)
        self.assertIn("exit", context)


class TheHintsAgree(unittest.TestCase):

    def test_the_prompt_hint_carries_the_same_map_as_this_hook(self):
        awk = PROMPT_HINT.read_text(encoding="utf-8")
        mapped = dict(re.findall(r'TOOL\["([a-z]+)"\] = "([a-z_]+)"', awk))
        self.assertEqual(load_hook().TOOLS, mapped)

    def test_every_subcommand_it_maps_is_a_row_of_the_skill_route_table(self):
        skill = (CLAUDE / "skills" / "codebase-index" / "SKILL.md").read_text(encoding="utf-8")
        for subcommand in load_hook().TOOLS:
            with self.subTest(subcommand=subcommand):
                self.assertRegex(skill, rf"`codebase-index {subcommand} ")


class TheWiring(unittest.TestCase):

    def entries(self):
        hooks = json.loads(SETTINGS.read_text(encoding="utf-8")).get("hooks") or {}
        return [(event, entry.get("matcher") or "", h)
                for event, group in hooks.items() for entry in group
                for h in entry.get("hooks") or []
                if "index-search-hint.py" in (h.get("command") or "")]

    def test_it_runs_before_a_search_and_under_no_other_event(self):
        # The event is the whole change: after the search it was read and
        # ignored. Read across every event, so a second registration is red.
        found = self.entries()
        self.assertEqual(1, len(found), found)
        event, matcher, hook = found[0]
        self.assertEqual("PreToolUse", event)
        for tool in ("Grep", "Bash"):
            self.assertRegex(tool, f"^(?:{matcher})$")
        self.assertEqual({"Grep", "Bash"}, set(matcher.split("|")))

    def test_it_goes_through_the_launcher_that_cannot_block_a_call(self):
        _, _, hook = self.entries()[0]
        self.assertEqual(
            'bash "${CLAUDE_PROJECT_DIR}/.claude/hooks/index-hint-run.sh" '
            "PreToolUse index-search-hint.py", hook["command"])
        # Longer than the launcher's own bound, so the harness never kills it
        # and discards the note the launcher prints past that bound.
        self.assertGreater(hook.get("timeout", 60), 10)


if __name__ == "__main__":
    unittest.main()
