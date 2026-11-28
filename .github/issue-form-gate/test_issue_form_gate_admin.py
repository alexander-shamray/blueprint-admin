"""This repository's ci.yml wiring of the issue-form gate, which the payload's suite cannot read."""

import re
import unittest

import issue_form_gate as gate

CI = gate.REPO_ROOT / ".github" / "workflows" / "ci.yml"


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
        at = job.index(name)
        begin = job.rindex("\n      - ", 0, at) + 1
        end = re.compile(r"\n      [-#]").search(job, at)
        step = job[begin:] if end is None else job[begin:end.start() + 1]
        self.assertTrue(step.startswith("      - if: runner.os == 'Linux'\n" + name), step)
        self.assertNotIn("continue-on-error", step)
        self.assertRegex(step, r"(?m)^          set -o pipefail$")
        self.assertRegex(step, r"gh label list [^\n]*--json name \|\n\s*python \.github/issue-form-gate/issue_form_gate\.py")

if __name__ == "__main__":
    unittest.main()
