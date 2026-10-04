"""The prompt-time hook that points a locate or impact question at the index.

**A hint that never fires looks exactly like one that is working**, because
its whole effect is a line of context nobody is shown. So this file runs
`index-query-hint.sh` itself — on every pattern it answers, on prompts it must
not answer, on slash commands, on unreadable input and on an `awk` that fails
— and asserts the wiring from the file the harness reads.

**What the hint tells an agent to run is read out of the hook's `emit` calls**,
never written out here: each command must be a row of the skill's route table,
spelled as the table spells it, under a subcommand its `allowed-tools`
approves, and each must be reached by a case below. A row re-pointed in the
skill, or a branch added to the hook with no case, is a red here.

**A hint is not the only door, because work here starts with a slash
command** (#97). `/ship` and `/branch` have their arguments read; every other
slash command stays silent. And `/ship` names the index itself, so a run
started from an issue number — which reads as no question — still locates
through it. The commands each names are held to their own grant and the
skill's, and the CLI's skill rewrite is held off for every session.

**Not a skip where `sh` is missing.** A skip reports a pass, which is the
fail-open `test_index_refresh_hook.py` refuses for the same tool.
"""

import json
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
SETTINGS = CLAUDE / "settings.json"
SKILL = CLAUDE / "skills" / "codebase-index" / "SKILL.md"
HINT = CLAUDE / "hooks" / "index-query-hint.sh"
SHIP = CLAUDE / "commands" / "ship.md"
REVIEW_BRANCH = CLAUDE / "commands" / "review-branch.md"
EVENT = "UserPromptSubmit"

# The full path, never the bare name: on Windows `subprocess` searches System32
# before PATH, where `bash` is WSL's.
SH = shutil.which("sh")

# One case per alternative in the hook's patterns, each with the subcommand it
# must be pointed at.
CASES = {
    "what breaks if I rename ExploreLink": "impact",
    "What depends on GrafanaClient?": "impact",
    "what is the impact of": "impact",
    "who calls JobRegistry": "refs",
    "What uses the token service?": "refs",
    "what calls Publish": "refs",
    "How does the SSE stream work?": "explain",
    "how does the stack page keep working": "explain",
    "how does the broker drain works": "explain",
    "How does ProcessRunner.cs work?": "explain",
    "find the handler for publish": "symbol",
    "find class ExploreLink": "symbol",
    "find methods named Start": "symbol",
    "find the classes that publish": "symbol",
    "Where is the broker service implemented?": "search",
    "where are the fakes recorded": "search",
    "where does the token come from": "search",
}

# Each pattern's boundary, broken on one side at a time.
NOT_QUESTIONS = (
    "it is somewhere is it not",       # where is: leading
    "say where isolated runs go",      # where is: trailing
    "somewhat breaks the layout",      # what breaks: leading
    "what callsign is it",             # what calls: trailing
    "How does that sound? Then work on the fix",
    "how does that end. Then work on the fix",
    "how does that end.. Then work on the fix",
    "how does /branch name the worktree",
    "find the classification bug in the parser",
    "rename the field and fix the test",
)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def hint_hooks():
    """The entries running the hint, keyed by the event that runs them."""
    running = {}
    for event, entries in (read_json(SETTINGS).get("hooks") or {}).items():
        matched = [
            (entry.get("matcher") or "", h)
            for entry in entries
            for h in (entry.get("hooks") or [])
            if HINT.name in (h.get("command") or "")
        ]
        if matched:
            running[event] = matched
    return running


def emitted_commands():
    """The command in every `emit` call, with the awk escaping undone."""
    found = re.findall(r'emit\("[a-z-]+", "`([^`]*)`"\)',
                       HINT.read_text(encoding="utf-8"))
    return [command.replace('\\\\\\"', '"') for command in found]


def named_subcommands():
    return {command.split()[1] for command in emitted_commands()}


def run(payload, cwd=None, awk_status=None):
    """The hook's stdout, stderr and status for one payload.

    With `awk_status`, the hook is sourced in a shell where `awk` is a function
    that fails with that status. A fake on PATH cannot stand in: Git for
    Windows' `bin\\sh.exe` puts `/usr/bin` first, so the real `awk` wins there.
    """
    if SH is None:
        raise AssertionError("no `sh` on PATH; the hook cannot run here")
    stdin = payload if isinstance(payload, str) else json.dumps(payload)
    argv = [SH, str(HINT)]
    if awk_status is not None:
        prelude = f'awk() {{ echo "awk: fatal" >&2; return {awk_status}; }}'
        argv = [SH, "-c", prelude + '; . "$0"', HINT.as_posix()]
    done = subprocess.run(argv, input=stdin.encode("utf-8"),
                          capture_output=True, cwd=cwd, timeout=30)
    return (done.stdout.decode("utf-8"), done.stderr.decode("utf-8"),
            done.returncode)


def prompt(text, **fields):
    """A payload shaped like the harness's, with `text` as the prompt."""
    return {"session_id": "s", "transcript_path": "t.jsonl", "cwd": ".",
            "hook_event_name": EVENT, "permission_mode": "default",
            "prompt": text, **fields}


class TheWiring(unittest.TestCase):

    def test_it_runs_on_every_prompt_and_under_no_other_event(self):
        # The registration surface is the subject, read across every event, so
        # the hint added to a second event is a red case rather than context
        # injected somewhere nobody decided it belongs.
        found = hint_hooks()
        self.assertEqual({EVENT}, set(found), found)
        self.assertEqual(1, len(found[EVENT]), found)

    def test_it_runs_under_plain_sh_and_cannot_hold_a_prompt(self):
        # Not backgrounded — its stdout is the whole of what it does — and not
        # through `run-guard.sh`, whose every failure is 2, which under this
        # event erases the prompt.
        _, hook = hint_hooks()[EVENT][0]
        self.assertEqual(
            'sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/index-query-hint.sh"',
            hook["command"])
        self.assertLessEqual(hook.get("timeout", 60), 5)


class TheHint(unittest.TestCase):

    def assertHints(self, payload, subcommand, cwd=None):
        if isinstance(payload, str) and not payload.startswith("{"):
            payload = prompt(payload)
        out, err, status = run(payload, cwd)
        self.assertEqual(("", 0), (err, status))
        answer = json.loads(out)["hookSpecificOutput"]
        self.assertEqual(EVENT, answer["hookEventName"])
        context = answer["additionalContext"]
        self.assertIn(f"`codebase-index {subcommand} ", context)
        self.assertIn("load the codebase-index skill", context)
        self.assertIn("session tag", context)
        return context

    def assertSilent(self, payload, awk_status=None):
        self.assertEqual(("", "", 0), run(payload, awk_status=awk_status))

    def test_each_kind_of_question_names_its_own_subcommand(self):
        for text, subcommand in CASES.items():
            with self.subTest(text=text):
                self.assertHints(text, subcommand)

    def test_every_command_it_emits_is_reached_by_a_case(self):
        # Read from the hook, so a branch added there with no case of its own,
        # or a case dropped here, is a red rather than an untested hint.
        self.assertGreaterEqual(len(emitted_commands()), 5)
        self.assertEqual(named_subcommands(), set(CASES.values()))

    def test_a_locate_question_is_pointed_at_a_tagged_search(self):
        context = self.assertHints("where is the broker service", "search")
        self.assertIn("--limit 5 --session <tag>", context)

    def test_a_phrase_that_is_not_the_question_is_silent(self):
        for text in NOT_QUESTIONS:
            with self.subTest(text=text):
                self.assertSilent(prompt(text))

    def test_ship_and_branch_are_read_for_the_task_they_carry(self):
        # The two slash commands whose arguments describe a change, and the
        # way this repository's work is started.
        self.assertHints("/ship where is the broker service", "search")
        self.assertHints("  /branch who calls JobRegistry", "refs")
        self.assertHints("/SHIP\thow does the SSE stream work", "explain")

    def test_every_other_slash_command_is_silent_whatever_follows_it(self):
        for text in ("  /commit who calls it", "/clear",
                     "/review-copilot where is it", "/shipit where is it",
                     "/branches where is it", "/ship", "/branch",
                     "/ship issue #97"):
            with self.subTest(text=text):
                self.assertSilent(prompt(text))

    def test_only_the_prompt_is_read_and_never_the_rest_of_the_payload(self):
        self.assertSilent(prompt("rename it", cwd="C:/where is/who calls"))
        self.assertSilent(prompt("rename it",
                                 transcript_path="what breaks/where is.jsonl"))

    def test_the_prompt_is_decoded_from_its_json_escapes(self):
        # An escaped quote before the question must not end the string early,
        # and a newline or a tab is whitespace, not the end of the prompt.
        self.assertHints('say "hi"\nwhere are the fakes', "search")
        self.assertHints("who\tcalls JobRegistry", "refs")

    def test_a_long_prompt_is_read_as_far_as_the_cap(self):
        self.assertHints("where is the broker " + "x" * 200_000, "search")
        self.assertSilent(prompt("x" * 5_000 + " where is the broker"))

    def test_input_it_cannot_read_is_silent_and_never_blocks(self):
        for payload in ("", "not json", '{"prompt": 7}', '{"prompt":"where is'):
            with self.subTest(payload=payload):
                self.assertSilent(payload)

    def test_an_awk_that_fails_leaves_the_prompt_alone(self):
        # The guarantee the header rests on: 2 under this event erases the
        # prompt, and an `awk` that dies says 2 itself unless the hook does not.
        # The prompt is one the real `awk` answers, so a shadow that failed to
        # take would print a hint and turn this red rather than pass it.
        for status in (2, 127):
            with self.subTest(status=status):
                self.assertSilent(prompt("where is the broker service"),
                                  awk_status=status)

    def test_it_writes_nothing(self):
        with tempfile.TemporaryDirectory() as scratch:
            self.assertHints("where is the broker service", "search",
                             cwd=scratch)
            self.assertEqual([], list(Path(scratch).iterdir()))


class TheSkillAgrees(unittest.TestCase):

    def test_every_command_it_emits_is_a_row_of_the_route_table(self):
        skill = SKILL.read_text(encoding="utf-8")
        for command in emitted_commands():
            with self.subTest(command=command):
                self.assertTrue(command.startswith("codebase-index "), command)
                self.assertIn(f"| `{command}` |", skill)

    def test_every_subcommand_it_names_is_one_the_skill_approves(self):
        skill = SKILL.read_text(encoding="utf-8")
        allowed = set(re.findall(r"Bash\(codebase-index ([a-z-]+):\*\)", skill))
        self.assertGreaterEqual(len(allowed), 5, f"allowed-tools shrank: {allowed}")
        for subcommand in named_subcommands():
            with self.subTest(subcommand=subcommand):
                self.assertIn(subcommand, allowed)


def body_of(path):
    """A command file's text after its frontmatter."""
    return path.read_text(encoding="utf-8").split("---", 2)[2]


def granted(path):
    """The `codebase-index` subcommands a file's frontmatter approves."""
    frontmatter = path.read_text(encoding="utf-8").split("---", 2)[1]
    return set(re.findall(r"Bash\(codebase-index ([a-z-]+):\*\)",
                          frontmatter))


def named(text):
    """The `codebase-index` subcommands a command's prose tells it to run."""
    return set(re.findall(r"codebase-index ([a-z][a-z-]*)", text))


class TheShipChainReadsTheIndex(unittest.TestCase):
    """`/ship` says it itself, because the hint cannot read an issue number as
    a question, and every review round starts from the diff's blast radius."""

    def test_it_names_the_index_before_any_grep(self):
        body = body_of(SHIP)
        search = body.find("`codebase-index search")
        self.assertNotEqual(-1, search)
        grep = body.lower().find("grep")
        if grep != -1:
            self.assertLess(search, grep)

    def test_every_review_round_starts_from_the_blast_radius(self):
        body = body_of(SHIP)
        # Inside item (1), which step (3) loops back to, so every round reads
        # it and not only the first; against the merge base, which an exact
        # grant lets the command read.
        loop = body.index("6. **The Copilot loop.**")
        item = body.find("\n   1. **", loop)
        base = body.find("git merge-base origin/main HEAD", loop)
        impact = body.find("codebase-index diff-impact --base <that sha>", loop)
        request = body.find("bash .claude/scripts/copilot-request.sh <n>", loop)
        self.assertTrue(-1 < item < base < impact < request,
                        (item, base, impact, request))
        # A resumed run that waits on an outstanding review never passes
        # through item (1), and its predecessor's scratchpad is gone.
        self.assertIn("run step 6 item (1)'s blast-radius read",
                      " ".join(body[:loop].split()))
        self.assertIn("Bash(git merge-base origin/main HEAD)",
                      SHIP.read_text(encoding="utf-8").split("---", 2)[1])

    def test_a_full_review_reads_the_blast_radius_before_it_greps(self):
        body = body_of(REVIEW_BRANCH)
        full = body.index("## Full review mode")
        impact = body.find("`codebase-index diff-impact", full)
        self.assertNotEqual(-1, impact)
        self.assertLess(impact, body.index("Grep `src/`", full))
        # A shell variable set in one call is empty in the next, and an
        # empty base reads as an empty range: the sha is carried instead.
        self.assertNotIn("$MERGE_BASE", body)
        self.assertIn("--base <merge-base>", body)

    def test_every_subcommand_a_command_names_is_granted_there_and_by_the_skill(self):
        skill = granted(SKILL)
        self.assertGreaterEqual(len(skill), 5, f"allowed-tools shrank: {skill}")
        for path in (SHIP, REVIEW_BRANCH):
            names = named(body_of(path))
            with self.subTest(command=path.name):
                self.assertIn("diff-impact", names)
                self.assertEqual(names, granted(path))
                self.assertLessEqual(names, skill)


class TheTrackedSkillIsNeverRewritten(unittest.TestCase):
    """The CLI re-materialises its packaged skill over this one whenever the
    ignored `.skill_version` stamp differs from the installed package, and a
    `/branch` worktree has no stamp: the first call the hint or `/ship` sends
    there rewrote the tracked skill and widened its `allowed-tools`.
    `.mcp.json` and `refresh-index.sh` turn that off for their own calls; an
    agent's Bash call is covered by `settings.json`'s `env` and nothing else."""

    def test_every_session_runs_the_cli_with_the_auto_update_off(self):
        env = read_json(SETTINGS).get("env") or {}
        self.assertEqual("1", env.get("CBX_NO_SKILL_AUTO_UPDATE"), env)


if __name__ == "__main__":
    unittest.main()
