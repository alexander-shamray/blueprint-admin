"""The sweeps' partition over this repository's tree, and the lockfile's two promises the payload's helper does not read."""

import json
import unittest
from pathlib import Path

from test_sweep_slices import REPO, Sweep, git, listed, parse, run_bash, setUpModule  # noqa: F401

BUDGET = 240000
LOCKFILE = "src/Admin.Web/package-lock.json"


def lockfile_faults(text):
    """One line per fault in a lockfile's text, read as JSON because `npm ci` ignores its whitespace."""
    try:
        data = json.loads(text)
    except ValueError:
        return ["the lockfile is not valid JSON"]
    version = data.get("lockfileVersion") if isinstance(data, dict) else None
    if type(version) is not int or version not in (2, 3):
        return ["lockfileVersion is not 2 or 3, so packages is not what npm ci reads"]
    packages = data.get("packages")
    if not isinstance(packages, dict) or not packages:
        return ["no package entries read"]
    faults = []
    for name, entry in packages.items():
        if not isinstance(entry, dict):
            faults.append(f"malformed entry: {name}")
            continue
        # The root is this package, and a link's `resolved` is a path.
        if name == "" or entry.get("link") is True:
            continue
        resolved = entry.get("resolved")
        if "resolved" in entry and not (isinstance(resolved, str) and resolved.startswith("https://registry.npmjs.org/")):
            faults.append(f"resolved off the registry: {name}")
        integrity = entry.get("integrity")
        if not (isinstance(integrity, str) and integrity.startswith("sha512-")) and entry.get("inBundle") is not True:
            faults.append(f"no integrity: {name}")
    return faults


class TheLockfile(unittest.TestCase):
    """A sweep counts the lockfile and does not read it (`.harness/sweep-rows.tsv`), so these two promises are read here."""

    GOOD = {"resolved": "https://registry.npmjs.org/a/-/a-1.0.0.tgz", "integrity": "sha512-AAAA"}

    def faults(self, **entry):
        return lockfile_faults(json.dumps({"lockfileVersion": 3, "packages": {"": {}, "node_modules/a": entry}}))

    def test_the_tracked_lockfile_keeps_both_promises(self):
        text = (Path(REPO) / LOCKFILE).read_text(encoding="utf-8")
        self.assertGreater(len(json.loads(text)["packages"]), 100, "the walk found almost nothing")
        self.assertEqual([], lockfile_faults(text))

    def test_an_entry_with_both_promises_has_no_fault(self):
        self.assertEqual([], self.faults(**self.GOOD))

    def test_an_off_registry_url_is_a_fault(self):
        faults = self.faults(**{**self.GOOD, "resolved": "https://example.test/a.tgz"})
        self.assertEqual(["resolved off the registry: node_modules/a"], faults)

    def test_a_missing_or_weak_integrity_is_a_fault(self):
        for integrity in ({"integrity": "sha1-AAAA"}, {"integrity": None}):
            with self.subTest(integrity=integrity):
                self.assertEqual(["no integrity: node_modules/a"], self.faults(**{**self.GOOD, **integrity}))

    def test_a_link_and_a_bundled_entry_are_exempt_where_they_should_be(self):
        self.assertEqual([], self.faults(link=True, resolved="../a"))
        self.assertEqual([], self.faults(inBundle=True))

    def test_a_file_it_cannot_read_fails_closed(self):
        for text in ("not json", json.dumps({"lockfileVersion": 1}), json.dumps({"lockfileVersion": 3, "packages": {}})):
            with self.subTest(text=text):
                self.assertEqual(1, len(lockfile_faults(text)))


class TheRealTree(unittest.TestCase):
    """Both partitions hold over this repository, within the budget."""

    def test_every_tracked_path_is_listed_or_counted_and_every_slice_fits(self):
        # `full`, so a ref a real sweep left in this checkout does not narrow it.
        sweep = Sweep(self, REPO)
        tracked = len(git(REPO, "ls-tree -r --name-only --full-tree HEAD").splitlines())
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                run_bash('rm -rf "$W.slices"', W=sweep.path)
                result = sweep.slices(kind, "full")
                self.assertEqual(0, result.returncode, result.stderr)
                head, slices = parse(result.stdout)
                self.assertEqual("full", head["mode"])
                counts = head["counts"]
                self.assertEqual(tracked, counts["tracked"])
                skipped = sum(v for k, v in counts.items() if k not in ("tracked", "listed"))
                self.assertEqual(counts["tracked"], counts["listed"] + skipped)
                for row, files, size, _ in slices:
                    self.assertTrue(size <= BUDGET or files == 1, (row, files, size))
                paths = listed(slices)
                self.assertEqual(counts["listed"], len(paths))
                self.assertEqual(len(paths), len(set(paths)), "a path in two slices")
                for top in ("src/", "tests/", "tools/", ".claude/", ".github/", "docs/"):
                    self.assertTrue(any(p.startswith(top) for p in paths), top)
                self.assertTrue(any("/" not in p for p in paths), "the root files")
                self.assertFalse(any(p.startswith("docs/superpowers/plans/") for p in paths))
                self.assertFalse(any(p.endswith("package-lock.json") for p in paths))


if __name__ == "__main__":
    unittest.main()
