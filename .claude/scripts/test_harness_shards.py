"""The splitter's subject is what it is looking at, not what it found.

**A shard runner that quietly stops placing a class is this repository's
most-repeated failure in its cheapest form**: every worker green, and one
class never run. Nothing here compares the placement against a list written
here — a list is the thing that goes stale. It compares the placement back
against discovery, at one worker, at this host's count, and at more workers
than there are classes.

**The three tables are the second subject.** They are schedule hints, so a
wrong entry costs a slower run; but a name in one that no longer resolves to a
discovered class is a pinning that has silently stopped applying, which is the
same failure in a smaller place. Each of them is checked against discovery
too.
"""

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parent.parent
RUNNER = SCRIPTS / "shard-harness-suite.py"
CHECKS = SCRIPTS / "harness-checks.sh"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"

# One worker, a couple, this host's own count, the knee, and more workers than
# there are classes — the last is where an off-by-one in the packing shows.
COUNTS = (1, 2, 3, 8, 64)


def loaded():
    """The runner, whose filename is not an importable module name."""
    spec = importlib.util.spec_from_file_location("shard_harness_suite", RUNNER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


SHARD = loaded()
_found = []


def found():
    """Discovery, once per process: it imports every module in both roots."""
    if not _found:
        _found.extend(SHARD.discovered())
    return _found


def uncommented(path):
    return "\n".join(line for line in path.read_text(encoding="utf-8")
                     .splitlines() if not line.lstrip().startswith("#"))


class ThePlacementIsDerivedFromDiscovery(unittest.TestCase):

    def test_every_discovered_test_is_placed_exactly_once(self):
        # The whole claim. Placed twice is a test run twice, which is slow;
        # placed never is a test that stopped being a gate, which is worse.
        for count in COUNTS:
            with self.subTest(workers=count):
                buckets, tail = SHARD.plan(found(), count)
                placed = [i for bucket in buckets for i in bucket] + tail
                self.assertEqual(len(placed), len(set(placed)), "placed twice")
                self.assertEqual(sorted(found()), sorted(placed))

    def test_a_class_no_table_mentions_is_placed_all_the_same(self):
        # What happens when somebody adds a file: the splitter must not need
        # telling, or the tables become the thing that decides coverage.
        stranger = "test_nobody_has_written_yet.AClass.test_it"
        buckets, tail = SHARD.plan([*found(), stranger], SHARD.workers())
        self.assertIn(stranger, [i for bucket in buckets for i in bucket] + tail)

    def test_both_roots_contribute(self):
        # A root that stops being discovered — a renamed directory, a pattern
        # that stops matching — is otherwise an empty set nobody misses.
        for start, pattern in SHARD.ROOTS:
            with self.subTest(root=start):
                where = str(ROOT / start)
                suite = unittest.TestLoader().discover(where, pattern=pattern,
                                                       top_level_dir=where)
                ids = {test.id() for test in SHARD.walk(suite)}
                self.assertTrue(ids, f"{start} discovered nothing")
                self.assertEqual(set(), ids - set(found()))

    def test_every_name_the_tables_pin_still_exists(self):
        # A renamed class loses its pinning, and a pinning that has stopped
        # applying reads exactly like one that is working.
        classes = {SHARD.owner(test_id) for test_id in found()}
        tables = (("SERIAL", SHARD.SERIAL), ("TOGETHER", SHARD.TOGETHER),
                  ("WEIGHT", SHARD.WEIGHT))
        for name, table in tables:
            for pinned in table:
                with self.subTest(table=name, pinned=pinned):
                    self.assertIn(pinned, classes)

    def test_the_classes_that_share_state_share_a_worker(self):
        for count in COUNTS:
            buckets, _ = SHARD.plan(found(), count)
            for group in sorted(set(SHARD.TOGETHER.values())):
                names = {n for n, g in SHARD.TOGETHER.items() if g == group}
                landed = {n for n, bucket in enumerate(buckets)
                          for i in bucket if SHARD.owner(i) in names}
                with self.subTest(workers=count, group=group):
                    self.assertEqual(1, len(landed), landed)

    def test_the_serial_classes_reach_no_worker(self):
        for count in COUNTS:
            buckets, tail = SHARD.plan(found(), count)
            parallel = {SHARD.owner(i) for bucket in buckets for i in bucket}
            with self.subTest(workers=count):
                self.assertEqual(set(), parallel & set(SHARD.SERIAL))
                self.assertEqual(set(), set(SHARD.SERIAL) -
                                 {SHARD.owner(i) for i in tail})

    def test_the_worker_count_is_capped_and_never_zero(self):
        # Past the knee the packing stops improving and the interleavings
        # start, so the cap is the point rather than the core count.
        self.assertGreaterEqual(SHARD.workers(), 1)
        self.assertLessEqual(SHARD.workers(), SHARD.CAP)


class TheRunnerRefusesRatherThanReportingAPass(unittest.TestCase):

    def test_an_id_discovery_does_not_hold_stops_the_worker(self):
        # The runtime half of the coverage rule. A worker told to run a name
        # nothing discovers has nothing to run, and reporting that as a pass
        # is the one outcome this file exists to prevent.
        with tempfile.TemporaryDirectory() as tmp:
            listing = Path(tmp) / "ids"
            listing.write_text("test_nothing_at_all.AClass.test_it\n",
                               encoding="utf-8")
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--ids", str(listing)],
                cwd=str(ROOT), capture_output=True, text=True, timeout=600)
        self.assertEqual(2, out.returncode, out.stdout + out.stderr)
        self.assertIn("discovery does not hold", out.stderr)

    def test_it_takes_no_arguments_of_its_own(self):
        # The shard count is the host's, capped; a caller who could set it
        # would be a second opinion about what the suite is.
        out = subprocess.run([sys.executable, str(RUNNER), "--shards", "3"],
                             cwd=str(ROOT), capture_output=True, text=True,
                             timeout=120)
        self.assertEqual(2, out.returncode, out.stdout)
        self.assertIn("takes no arguments", out.stderr)


class TheCallersNameTheRunnerAndNoRootOfTheirOwn(unittest.TestCase):
    """Two copies of a discovery root is how a local check and CI come to run
    different suites. The roots have one owner now, and these are the two
    places that used to spell them out."""

    def test_harness_checks_runs_the_runner(self):
        code = uncommented(CHECKS)
        self.assertIn("shard-harness-suite.py", code)
        self.assertNotIn("unittest discover", code)

    def test_the_ci_harness_job_runs_the_runner(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("shard-harness-suite.py", text)
        self.assertNotIn("unittest discover", text)


if __name__ == "__main__":
    unittest.main()
