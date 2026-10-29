#!/usr/bin/env python3
"""What the gate would catch, which running it against this repository does not
say, and the wiring that makes CI run it.

    py -3.12 -m unittest discover -s .github/issue-form-gate
"""
from __future__ import annotations

import json
import re
import tempfile
import unittest
from pathlib import Path

import issue_form_gate as gate

HELPER = """\
case "$kind" in
  security|bug) ;;
  *) exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) exit 2 ;;
esac
"""

LABELS = {"security", "bug", "critical", "high", "medium", "low", "harness", "code", "documentation"}

CI = gate.REPO_ROOT / ".github" / "workflows" / "ci.yml"


def form(kind: str = "bug security", severity: str = "critical high medium low",
         area: str = "harness code documentation", extra: str = "") -> str:
    def dropdown(name: str, words: str) -> str:
        options = "".join(f"        - {word}\n" for word in words.split())
        return (f"  - type: dropdown\n    id: {name}\n    attributes:\n      label: {name}\n"
                f"      options:\n{options}    validations:\n      required: true\n")
    return ("name: Finding\nbody:\n  - type: markdown\n    attributes:\n      value: text\n"
            + dropdown("kind", kind) + dropdown("severity", severity) + dropdown("area", area) + extra
            + "  - type: textarea\n    id: found\n    attributes:\n      label: What was found\n")


class IssueFormGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        root = Path(self._tmp.name)
        self.templates = root / "ISSUE_TEMPLATE"
        self.templates.mkdir()
        self.helper = root / "gh-issue-filing.sh"
        self.helper.write_text(HELPER, encoding="utf-8")
        self.write("config.yml", "blank_issues_enabled: false\n")
        self.write("finding.yml", form())

    def write(self, name: str, text: str) -> None:
        (self.templates / name).write_text(text, encoding="utf-8")

    def check(self, labels: set[str] = LABELS) -> list[str]:
        return gate.check(self.templates, self.helper, labels)

    def assert_one(self, needle: str, labels: set[str] = LABELS) -> None:
        problems = self.check(labels)
        self.assertEqual(1, len(problems), problems)
        self.assertIn(needle, problems[0])

    def test_a_form_matching_its_owners_passes(self) -> None:
        self.assertEqual([], self.check())

    def test_a_kind_the_helper_lacks_fails(self) -> None:
        self.write("finding.yml", form(kind="bug security enhancement"))
        self.assert_one("extra ['enhancement']", LABELS | {"enhancement"})

    def test_a_kind_the_helper_gains_fails(self) -> None:
        self.helper.write_text(HELPER.replace("security|bug", "security|bug|test-gap"), encoding="utf-8")
        self.assert_one("missing ['test-gap']")

    def test_a_kind_the_form_drops_fails(self) -> None:
        self.write("finding.yml", form(kind="bug"))
        self.assert_one("missing ['security']")

    def test_a_severity_the_helper_lacks_fails(self) -> None:
        self.write("finding.yml", form(severity="critical high medium low trivial"))
        self.assert_one("extra ['trivial']", LABELS | {"trivial"})

    def test_a_severity_the_helper_gains_fails(self) -> None:
        self.helper.write_text(HELPER.replace("critical|high", "blocker|critical|high"), encoding="utf-8")
        self.assert_one("missing ['blocker']")

    def test_a_repeated_option_fails(self) -> None:
        self.write("finding.yml", form(severity="critical high medium low low"))
        self.assert_one("repeats an option")

    def test_an_option_that_is_not_a_label_fails(self) -> None:
        self.assert_one("option harness is not a label", LABELS - {"harness"})

    def test_an_area_option_that_is_not_a_label_fails(self) -> None:
        self.write("finding.yml", form(area="harness code documentation test"))
        self.assert_one("area option test is not a label")

    def test_a_missing_dropdown_fails(self) -> None:
        self.write("finding.yml", form().replace("id: area", "id: areas"))
        problems = self.check()
        self.assertTrue(any("no area dropdown" in p for p in problems), problems)
        self.assertTrue(any("dropdown areas is not one of" in p for p in problems), problems)

    def test_every_form_is_read(self) -> None:
        self.write("second.yaml", form(kind="bug"))
        self.assert_one("second.yaml's kind")

    def test_blank_issues_left_on_fails(self) -> None:
        self.write("config.yml", "blank_issues_enabled: true\n")
        self.assert_one("blank_issues_enabled: false")

    def test_a_missing_config_fails(self) -> None:
        (self.templates / "config.yml").unlink()
        self.assert_one("blank_issues_enabled: false")

    def test_a_directory_with_no_form_fails(self) -> None:
        (self.templates / "finding.yml").unlink()
        self.assert_one("holds no issue form")

    def test_a_missing_directory_fails(self) -> None:
        self.templates = self.templates / "absent"
        self.assert_one("is not a directory")

    def test_options_after_a_blank_line_are_still_read(self) -> None:
        self.write("finding.yml", form().replace("        - low\n", "\n        - low\n        - trivial\n"))
        self.assert_one("extra ['trivial']", LABELS | {"trivial"})

    def test_a_flow_style_options_list_is_refused(self) -> None:
        self.write("finding.yml", form().replace("      options:\n        - critical\n        - high\n"
                                                 "        - medium\n        - low\n",
                                                 "      options: [critical, high, medium, low]\n"))
        with self.assertRaisesRegex(gate.InputRefused, "dropdown severity has no options"):
            self.check()

    def test_an_option_that_is_not_a_bare_name_is_refused(self) -> None:
        self.write("finding.yml", form().replace("        - low\n", "        - 'low'\n"))
        with self.assertRaisesRegex(gate.InputRefused, "not a bare label name"):
            self.check()

    def test_a_line_inside_an_options_list_that_is_not_an_option_is_refused(self) -> None:
        self.write("finding.yml", form().replace("        - low\n", "        - low\n        # trailing\n"))
        with self.assertRaisesRegex(gate.InputRefused, "inside an options list"):
            self.check()

    def test_options_outside_a_dropdown_are_refused(self) -> None:
        self.write("finding.yml", form(extra="  - type: checkboxes\n    id: ticks\n    attributes:\n"
                                             "      options:\n        - one\n"))
        with self.assertRaisesRegex(gate.InputRefused, "options outside a dropdown"):
            self.check()

    def test_a_dropdown_twice_is_refused(self) -> None:
        self.write("finding.yml", form() + form().split("body:\n", 1)[1])
        with self.assertRaisesRegex(gate.InputRefused, "appears twice"):
            self.check()

    def test_a_helper_without_the_arm_is_refused(self) -> None:
        self.helper.write_text("echo nothing\n", encoding="utf-8")
        with self.assertRaisesRegex(gate.InputRefused, "no `case"):
            self.check()

    def test_main_refuses_an_empty_label_list(self) -> None:
        self.assertEqual(1, self.main("[]"))

    def test_main_refuses_stdin_that_is_not_json(self) -> None:
        self.assertEqual(1, self.main(""))

    def test_main_refuses_rows_without_a_name(self) -> None:
        self.assertEqual(1, self.main('[{"label": "bug"}]'))

    def test_main_exits_by_the_check(self) -> None:
        labels = json.dumps([{"name": name} for name in LABELS])
        self.assertEqual(0, self.main(labels))
        self.write("config.yml", "blank_issues_enabled: true\n")
        self.assertEqual(1, self.main(labels))

    def main(self, stdin: str) -> int:
        return gate.main(["--templates", str(self.templates), "--helper", str(self.helper)], stdin=stdin)


class ThisRepositoryTests(unittest.TestCase):
    """The reader against the files the gate actually reads, so a reshaped file fails here first."""

    def test_this_repository_s_form_carries_every_governed_dropdown(self) -> None:
        found = gate.dropdowns(gate.DEFAULT_TEMPLATES / "finding.yml")
        self.assertEqual(set(gate.GOVERNED), set(found))

    def test_this_repository_s_helper_arms_are_read(self) -> None:
        self.assertIn("bug", gate.case_arm(gate.DEFAULT_HELPER, "kind"))
        self.assertIn("low", gate.case_arm(gate.DEFAULT_HELPER, "severity"))

    def test_this_repository_s_forms_pass_against_their_own_options_as_labels(self) -> None:
        labels = {option for options in gate.dropdowns(gate.DEFAULT_TEMPLATES / "finding.yml").values()
                  for option in options}
        self.assertEqual([], gate.check(gate.DEFAULT_TEMPLATES, gate.DEFAULT_HELPER, labels))

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
