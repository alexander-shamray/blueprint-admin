"""The commands split into a runbook and its argument (docs/token-plan.md, step 6).

A runbook is resident in every turn its command runs and its argument is read
only when a rule is disputed; these cases hold the two halves to each other,
and hold the text other suites pin to the half that is always resident.
"""

import re
import unittest
from pathlib import Path

COMMANDS = Path(__file__).resolve().parent.parent / "commands"
ARGUMENTS = COMMANDS.parent.parent / "docs" / "commands"

# Bytes with CRLF counted as LF, so a Windows checkout reads what Linux CI does;
# each landed size and about a tenth: a runbook past it has regrown its argument.
CEILINGS = {
    "ship.md": 33300,
    "bug-sweep.md": 22000,
    "security-sweep.md": 18600,
    "branch.md": 10800,
    "review-branch.md": 12200,
}

CITATION = re.compile(r"docs/commands/([a-z-]+\.md)`?, \*([^*]+)\*")

# Every line another suite pins in a command's text. A phrase listed here is
# one a test reads out of the runbook, so a split that moves it into the
# argument turns that suite red; listing it here turns this one red first, with
# the name of the command that lost it.
PINNED = {
    "bug-sweep.md": (
        "opened by the repository owner",
        "is not tracking and blocks nothing",
        "deliberately NOT a second sufficient condition",
        "files normally",
        "tracked by the gate's test",
        "gh-sweep-issue-create.sh bug <severity>",
        "`git-worktree-detach.sh` now refuses a",
        "tracked link",
    ),
    "security-sweep.md": (
        "opened by the repository owner",
        "is not tracking and blocks nothing",
        "deliberately NOT a second sufficient condition",
        "files normally",
        "tracked by the gate's test",
        "gh-sweep-issue-create.sh security <severity>",
        "`git-worktree-detach.sh` now refuses a",
        "tracked link",
    ),
    "ship.md": (
        "one `branch-reviewer` agent",
        "git merge-base --is-ancestor HEAD <headRefOid>",
        "git merge-base --is-ancestor HEAD origin/main",
        "git fetch origin refs/pull/<n>/head || true",
        "bash .claude/scripts/gh-pr-merge.sh <n> <oid>",
    ),
}


def collapsed(path):
    return " ".join(path.read_text(encoding="utf-8").split())


class EachSplitCommandHasBothHalves(unittest.TestCase):

    def argued(self):
        names = sorted(p.name for p in ARGUMENTS.glob("*.md"))
        self.assertGreaterEqual(len(names), len(CEILINGS), "the argument glob found fewer files than were split")
        return names

    def test_every_split_command_has_a_runbook_that_cites_its_argument(self):
        for name in sorted(set(self.argued()) | set(CEILINGS)):
            with self.subTest(command=name):
                self.assertTrue((ARGUMENTS / name).is_file(), f"docs/commands/{name} is missing")
                runbook = COMMANDS / name
                self.assertTrue(runbook.is_file(), f"no runbook for docs/commands/{name}")
                self.assertIn(f"docs/commands/{name}", collapsed(runbook))

    def test_every_citation_names_a_heading_that_exists(self):
        cited = 0
        for runbook in sorted(COMMANDS.glob("*.md")):
            for target, heading in CITATION.findall(collapsed(runbook)):
                cited += 1
                with self.subTest(runbook=runbook.name, target=target, heading=heading):
                    path = ARGUMENTS / target
                    self.assertTrue(path.is_file(), f"cites a missing docs/commands/{target}")
                    headings = re.findall(r"^#{2,3} (.+?)\s*$", path.read_text(encoding="utf-8"), re.MULTILINE)
                    self.assertIn(heading, headings)
        self.assertGreater(cited, len(CEILINGS), "the citation pattern stopped matching the runbooks")

    def test_each_runbook_stays_under_its_ceiling(self):
        for name, ceiling in CEILINGS.items():
            with self.subTest(command=name):
                size = len((COMMANDS / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_argument_names_its_runbook(self):
        for name in self.argued():
            with self.subTest(argument=name):
                self.assertIn(f".claude/commands/{name}", collapsed(ARGUMENTS / name))

    def test_pinned_text_stays_in_the_runbook(self):
        for name, phrases in PINNED.items():
            text = collapsed(COMMANDS / name)
            for phrase in phrases:
                with self.subTest(command=name, phrase=phrase):
                    self.assertIn(" ".join(phrase.split()), text)


if __name__ == "__main__":
    unittest.main()
