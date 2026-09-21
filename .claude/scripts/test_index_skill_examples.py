"""Every `search` example carries the limit `SKILL.md` owns.

**The subject is the set of examples, not any one of them.** A rule that only
the examples execute cannot be held by the prose above them, so an example
written without the option is a red case when it lands rather than when a
reviewer happens to read that block. `CLAUDE.md` owns the argument: a gate's
subject is what it is looking at.

**The value is read from `SKILL.md` and never spelled here.** That file owns
the number; a copy in a test is exactly the restatement the one-owner rule is
about, and it would go stale in the direction that reports success.
"""

import re
import shlex
import unittest
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent.parent / "skills" / "codebase-index"
SKILL = SKILL_DIR / "SKILL.md"

# The route table's own row: the first line anyone copies, and the owner of
# the value every other example has to match.
ROUTE_ROW = re.compile(
    r"^\| Where is X implemented\? \| `(codebase-index search [^`]*)` \|", re.M)


def limits_in(line):
    """Every `--limit` value on a command line, whole rather than by prefix."""
    tokens = shlex.split(line)
    return [tokens[index + 1]
            for index, token in enumerate(tokens)
            if token == "--limit" and index + 1 < len(tokens)]


def owned_limit():
    """The one whole `--limit` value `SKILL.md`'s route row carries.

    Read by `limits_in`, the same rule the examples are held to. A prefix
    match would take `5` from both `--limit 5 --limit 50` and `--limit 5oops`
    and hand it out as the owned value, so the row could contradict itself or
    name nothing numeric while every example matching five passed — the gate
    enforcing a contract its own source was exempt from.
    """
    row = ROUTE_ROW.search(SKILL.read_text(encoding="utf-8"))
    assert row, "SKILL.md has no `Where is X implemented?` route row"
    found = limits_in(row.group(1))
    assert len(found) == 1, f"the route row names {len(found)}: {row.group(1)}"
    assert found[0].isdigit(), f"the route row's limit is not a number: {found[0]}"
    return found[0]


def search_examples():
    """Every runnable `search` line under the skill, with where it was found.

    A line rather than a fenced block, because the frontmatter's
    `allowed-tools` names the same command and is a grant rather than an
    example: it never starts a line with the binary.
    """
    for path in sorted(SKILL_DIR.rglob("*.md")):
        for number, line in enumerate(
                path.read_text(encoding="utf-8").splitlines(), 1):
            if line.lstrip().startswith("codebase-index search "):
                yield path, number, line.strip()


class EverySearchExampleCarriesTheOwnedLimit(unittest.TestCase):

    def test_the_skill_owns_a_limit(self):
        self.assertRegex(owned_limit(), r"^\d+$")

    def test_there_are_examples_to_check(self):
        # Without this the assertion below passes on an empty set, which is the
        # fail-open shape: a rename or a reflow that hides every example would
        # report the rule enforced by finding nothing to enforce it against.
        found = list(search_examples())
        self.assertGreaterEqual(len(found), 4, found)

    def test_the_owner_is_read_by_the_rule_it_owns(self):
        # The two spellings a prefix match hands out as five: a row naming two
        # limits, and one whose value is not a number. Neither is a value to
        # own, and both used to pass unseen.
        self.assertEqual(
            ["5", "50"], limits_in('codebase-index search "X" --limit 5 --limit 50'))
        self.assertEqual(
            ["5oops"], limits_in('codebase-index search "X" --limit 5oops'))

    def test_a_substring_of_the_value_is_not_the_value(self):
        # The owned value is a prefix of longer ones, so a substring test
        # admits an example that has drifted to `--limit 50` — the fail-open
        # this whole file is about, one level down.
        drifted = 'codebase-index search "X" --limit 50 --session <tag> --json'
        self.assertEqual(["50"], limits_in(drifted))
        self.assertNotEqual([owned_limit()], limits_in(drifted))

    def test_every_example_passes_the_owned_limit(self):
        # Whole values, and exactly one of them: a second `--limit` would let
        # an example carry the owned value and contradict it in the same line.
        for path, number, line in search_examples():
            with self.subTest(file=path.name, line=number):
                self.assertEqual([owned_limit()], limits_in(line),
                                 f"{path.name}:{number}: {line}")


if __name__ == "__main__":
    unittest.main()
