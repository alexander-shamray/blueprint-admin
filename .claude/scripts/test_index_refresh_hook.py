"""The `PostToolUse` hook that keeps the local index current (#26).

**The subject is the wiring, because the command's failures are invisible.**
The hook discards its output and runs in the background so that it can never
block an edit — which also means a hook that fails on every call looks exactly
like one that works. The example this repository shipped did precisely that:
`--quiet` is not an option the CLI has, and nothing would ever have said so.
So every property that decides whether the refresh runs is asserted here from
the files the harness reads, and none from a run whose exit nobody sees.
"""

import importlib.util
import json
import re
import shlex
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
SETTINGS = CLAUDE / "settings.json"
EXAMPLE = CLAUDE / "skills" / "codebase-index" / "examples" / "hooks" / "settings.json"
MCP = CLAUDE.parent / ".mcp.json"
EDIT_GUARD = CLAUDE / "hooks" / "guard-edit-target.py"
GUARD_ENV = "CBX_NO_SKILL_AUTO_UPDATE"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def editing_tools():
    """The tools that write, read from the edit guard rather than listed here."""
    spec = importlib.util.spec_from_file_location("guard_edit_target", EDIT_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.EDITING_TOOLS


class TheIndexRefreshHook(unittest.TestCase):

    def entries(self):
        return read_json(SETTINGS).get("hooks", {}).get("PostToolUse", [])

    def refresh(self):
        found = [
            (entry.get("matcher") or "", h)
            for entry in self.entries() for h in (entry.get("hooks") or [])
            if "codebase-index update" in (h.get("command") or "")
        ]
        self.assertEqual(1, len(found), f"expected one refresh hook: {found}")
        return found[0]

    def test_it_runs_after_every_tool_that_writes(self):
        # Derived from the edit guard's own list, so a tool added there is a red
        # case here rather than a file the index silently stops following.
        matcher, _ = self.refresh()
        tools = editing_tools()
        self.assertGreaterEqual(len(tools), 4, f"EDITING_TOOLS shrank: {tools}")
        for tool in tools:
            with self.subTest(tool=tool):
                self.assertTrue(re.fullmatch(matcher, tool),
                                f"{matcher!r} does not select {tool}")
        for alternative in matcher.split("|"):
            with self.subTest(alternative=alternative):
                self.assertIn(alternative, tools)

    def test_it_keeps_the_skill_rewrite_guard_the_index_server_sets(self):
        # The value is read from `.mcp.json`, its owner, rather than restated.
        _, hook = self.refresh()
        expected = read_json(MCP)["mcpServers"]["codebase-index"]["env"][GUARD_ENV]
        self.assertIn(f"{GUARD_ENV}={expected}", hook["command"])

    def test_it_can_never_block_an_edit(self):
        _, hook = self.refresh()
        command = hook["command"].rstrip()
        self.assertTrue(command.endswith("&"), command)
        self.assertIn(">/dev/null 2>&1", command)
        self.assertLessEqual(hook.get("timeout", 60), 5)

    def test_it_passes_no_option_the_cli_lacks(self):
        # `update` takes `--since` and `--all`; the refresh wants neither, so any
        # flag at all is the `--quiet` defect arriving in another spelling.
        _, hook = self.refresh()
        words = shlex.split(hook["command"].split(">", 1)[0])
        start = words.index("codebase-index")
        self.assertEqual(["codebase-index", "update"], words[start:], words)

    def test_the_shipped_example_is_the_wiring(self):
        # The example is what a reader copies; it drifted from anything that
        # ran, so it is held equal to the one that does.
        self.assertEqual(self.entries(),
                         read_json(EXAMPLE).get("hooks", {}).get("PostToolUse"))


if __name__ == "__main__":
    unittest.main()
