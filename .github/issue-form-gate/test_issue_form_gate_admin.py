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
        # The job ends at the next job key, so a job added after it cannot
        # answer for the harness job's wiring.
        following = re.search(r"^  [a-z][a-z0-9-]*:\n", text[start + 1:], re.MULTILINE)
        job = text[start:start + 1 + following.start()] if following else text[start:]
        self.assertIn("issues: read", job)
        self.assertRegex(job, r"gh label list [^\n]*--json name \|\n\s*python \.github/issue-form-gate/issue_form_gate\.py")
        self.assertIn("set -o pipefail", job)


if __name__ == "__main__":
    unittest.main()
