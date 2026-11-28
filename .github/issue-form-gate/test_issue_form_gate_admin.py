"""This repository's ci.yml wiring of the issue-form gate, which the payload's suite cannot read."""

import re
import unittest

import issue_form_gate as gate

CI = gate.REPO_ROOT / ".github" / "workflows" / "ci.yml"


def step_of(job: str, text: str) -> str:
    """The step that holds `text`, less its comment and blank lines, ending at the next step or a shallower line.

    A comment at any indent does not end it, since YAML admits one inside a mapping; test_pin_gate.py's `step()`
    is the same reading, which this suite cannot import.
    """
    at = job.index(text)
    lines = job[job.rindex("\n      - ", 0, at + 2) + 1:].splitlines(keepends=True)
    kept = lines[:1]
    for line in lines[1:]:
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if line.startswith("      - ") or len(line) - len(line.lstrip(" ")) < 8:
            break
        kept.append(line)
    return "".join(kept)


class TheStepSlice(unittest.TestCase):
    def test_a_key_after_a_comment_inside_the_step_is_read(self) -> None:
        job = ("\n      - if: true\n        name: n\n      # inside\n        continue-on-error: true\n"
               "      - run: next\n")
        self.assertEqual("      - if: true\n        name: n\n        continue-on-error: true\n",
                         step_of(job, "        name: n\n"))


class TheWiring(unittest.TestCase):
    def test_ci_pipes_the_labels_into_the_gate_with_a_token_that_may_read_them(self) -> None:
        # The live half reads the labels over the network, which no case here
        # does; a workflow that stops running it, or whose token cannot list
        # issues' labels, would leave only the cases above.
        text = CI.read_text(encoding="utf-8")
        start = text.index("\n  harness:\n") + 1
        # The job ends at the next two-space key of any spelling, so a job added after it cannot answer for its wiring.
        following = re.search(r"^  \S", text[start + 1:], re.MULTILINE)
        job = text[start:start + 1 + following.start()] if following else text[start:]
        # Read from the uncommented mapping, not from anywhere in the job.
        permissions = re.search(r"(?m)^    permissions:\n((?:      .*\n)+)", job)
        self.assertIsNotNone(permissions, job)
        self.assertRegex(permissions.group(1), r"(?m)^      issues: read$")
        # The step by its name, so a condition, a failure let through or a pipefail in another step is a finding.
        name = "        name: Every issue-form choice is its owner's word and a label here\n"
        step = step_of(job, name)
        self.assertTrue(step.startswith("      - if: runner.os == 'Linux'\n" + name), step)
        self.assertNotIn("continue-on-error", step)
        self.assertRegex(step, r"(?m)^          set -o pipefail$")
        self.assertRegex(step, r"gh label list [^\n]*--json name \|\n\s*python \.github/issue-form-gate/issue_form_gate\.py")

if __name__ == "__main__":
    unittest.main()
