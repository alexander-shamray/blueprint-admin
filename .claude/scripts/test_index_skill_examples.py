"""Every `search` example carries the limit `SKILL.md` owns.

**The rule and the lines that run it drifted twice on one branch.** The skill
argued a result limit while its own route table and the reference's examples
kept the default; fixing the two lines the first review named left three more
in a section nobody had grepped, and the next review found those. Prose cannot
hold a rule that only the examples execute.

**So the subject here is the set of examples, not any one of them.** A search
example written tomorrow without the option is a red case when it lands rather
than when a reviewer happens to read that block — which is `CLAUDE.md`'s rule
about a gate whose subject is what it is looking at, applied to the one surface
on this branch that kept slipping.

**The value is read from `SKILL.md` and never spelled here.** That file owns
the number; a copy in a test is exactly the restatement the one-owner rule is
about, and it would go stale in the direction that reports success.
"""

import re
import unittest
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent.parent / "skills" / "codebase-index"
SKILL = SKILL_DIR / "SKILL.md"

# The route table's own row: the first line anyone copies, and the owner of
# the value every other example has to match.
ROUTE_ROW = re.compile(
    r"^\| Where is X implemented\? \| `(codebase-index search [^`]*)` \|", re.M)


def owned_limit():
    """The `--limit` value `SKILL.md`'s route row carries."""
    row = ROUTE_ROW.search(SKILL.read_text(encoding="utf-8"))
    assert row, "SKILL.md has no `Where is X implemented?` route row"
    found = re.search(r"--limit (\d+)", row.group(1))
    assert found, f"the route row names no --limit: {row.group(1)}"
    return found.group(1)


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

    def test_every_example_passes_the_owned_limit(self):
        expected = f"--limit {owned_limit()}"
        for path, number, line in search_examples():
            with self.subTest(file=path.name, line=number):
                self.assertIn(expected, line, f"{path.name}:{number}: {line}")


if __name__ == "__main__":
    unittest.main()
