"""The splitter's subject is what it is looking at, not what it found.

**A shard runner that quietly stops placing a class is this repository's
most-repeated failure in its cheapest form**: every worker green, and one
class never run. Nothing here compares the placement against a list written
here — a list is the thing that goes stale. It compares the placement back
against discovery, and it does so at counts derived from the host and from the
suite rather than from a literal, because a literal boundary stops being the
boundary without saying so.

**The three tables are the second subject.** They are schedule hints, so a
wrong entry costs a slower run; but a name in one that no longer resolves to a
discovered class is a pinning that has silently stopped applying, which is the
same failure in a smaller place. Each of them is checked against discovery
too.

**And the refusals are the third.** A run that discovers nothing, a plan that
loses a worker, a worker handed a name nothing discovers or handed nothing at
all — each of those is a way to report a pass over tests that never ran, and
each has a case here.
"""

import importlib.util
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parent.parent
RUNNER = SCRIPTS / "shard-harness-suite.py"
CHECKS = SCRIPTS / "harness-checks.sh"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"


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


def counts():
    """The worker counts every placement case is checked at.

    One worker; a couple; this host's own, which is what it will actually run
    at and which a literal tuple missed on every four-core box, including both
    GitHub runners; the cap; and one more than there are classes, which is
    where an off-by-one in the packing shows. The last is derived, because a
    literal that was once larger than the class count stops being that
    boundary the moment somebody adds classes, and says nothing when it does.
    """
    classes = len({SHARD.owner(test_id) for test_id in found()})
    return (1, 2, 3, SHARD.workers(), SHARD.CAP, classes + 1)


def uncommented(path):
    return "\n".join(line for line in path.read_text(encoding="utf-8")
                     .splitlines() if not line.lstrip().startswith("#"))


class ThePlacementIsDerivedFromDiscovery(unittest.TestCase):

    def test_every_discovered_test_is_placed_exactly_once(self):
        # The whole claim. Placed twice is a test run twice, which is slow;
        # placed never is a test that stopped being a gate, which is worse.
        self.assertTrue(found(), "discovery returned nothing to place")
        for count in counts():
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
        classes = {SHARD.owner(i) for i in found()}
        tables = (("SERIAL", SHARD.SERIAL), ("TOGETHER", SHARD.TOGETHER),
                  ("WEIGHT", SHARD.WEIGHT))
        for name, table in tables:
            for pinned in table:
                with self.subTest(table=name, pinned=pinned):
                    self.assertIn(pinned, classes)

    def test_the_classes_that_share_state_share_a_worker(self):
        self.assertTrue(SHARD.TOGETHER, "nothing is pinned together any more")
        for count in counts():
            buckets, _ = SHARD.plan(found(), count)
            for group in sorted(set(SHARD.TOGETHER.values())):
                names = {n for n, g in SHARD.TOGETHER.items() if g == group}
                landed = {n for n, bucket in enumerate(buckets)
                          for i in bucket if SHARD.owner(i) in names}
                with self.subTest(workers=count, group=group):
                    self.assertEqual(1, len(landed), landed)

    def test_the_serial_classes_reach_no_worker(self):
        for count in counts():
            buckets, tail = SHARD.plan(found(), count)
            parallel = {SHARD.owner(i) for bucket in buckets for i in bucket}
            with self.subTest(workers=count):
                self.assertEqual(set(), parallel & set(SHARD.SERIAL))
                self.assertEqual(set(), set(SHARD.SERIAL) -
                                 {SHARD.owner(i) for i in tail})

    def test_the_worker_count_follows_the_host_up_to_the_cap(self):
        # Asserting only that the answer sits between one and the cap is an
        # assertion the expression's own shape already guarantees: a
        # `workers()` that ignored the host and always returned 1 would pass
        # it, turning the whole design back into one serial worker while
        # everything stayed green.
        for cores, expected in ((1, 1), (2, 2), (4, 4),
                                (SHARD.CAP, SHARD.CAP),
                                (SHARD.CAP + 1, SHARD.CAP), (64, SHARD.CAP),
                                (None, 1), (0, 1)):
            with self.subTest(cores=cores):
                with mock.patch.object(SHARD.os, "cpu_count",
                                       return_value=cores):
                    self.assertEqual(expected, SHARD.workers())


class TheRunnerRefusesRatherThanReportingAPass(unittest.TestCase):

    def test_a_root_that_discovers_nothing_refuses_the_run(self):
        # A renamed directory or a pattern that stopped matching looks exactly
        # like an empty suite, and `test_both_roots_contribute` cannot catch
        # it — that case is among the tests that stopped being discovered. So
        # the runner has to refuse rather than report zero tests and exit 0.
        with mock.patch.object(SHARD, "ROOTS", (("docs", "test_*.py"),)):
            with self.assertRaises(SHARD.NothingDiscovered):
                SHARD.discovered()

    def test_a_run_that_loses_a_worker_is_caught_before_it_starts(self):
        # `plan()` covering everything and the runs covering everything are
        # two claims, and the gate above can only see the first. This is the
        # slip it cannot see: a bucket that never becomes a phase.
        buckets, tail = SHARD.plan(found(), 8)
        phases = [("shard", b) for b in buckets[:-1] if b]
        if tail:
            phases.append(("serial", tail))
        missing, extra = SHARD.unplaced(found(), phases)
        self.assertTrue(missing, "a dropped worker went unnoticed")
        self.assertEqual([], extra)

    def test_a_run_that_covers_discovery_is_not_flagged(self):
        # The control: the check must be quiet on the plan the runner makes,
        # or it would refuse every run and teach everyone to ignore it.
        buckets, tail = SHARD.plan(found(), SHARD.workers())
        phases = [("shard", b) for b in buckets if b]
        if tail:
            phases.append(("serial", tail))
        self.assertEqual(([], []), SHARD.unplaced(found(), phases))

    def test_a_test_placed_twice_is_caught(self):
        buckets, tail = SHARD.plan(found(), 4)
        phases = [("shard", b) for b in buckets if b]
        phases.append(("shard", list(buckets[0])))
        if tail:
            phases.append(("serial", tail))
        missing, extra = SHARD.unplaced(found(), phases)
        self.assertEqual([], missing)
        self.assertTrue(extra, "a bucket run twice went unnoticed")

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

    def test_a_worker_given_no_ids_at_all_refuses(self):
        with tempfile.TemporaryDirectory() as tmp:
            listing = Path(tmp) / "ids"
            listing.write_text("", encoding="utf-8")
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--ids", str(listing)],
                cwd=str(ROOT), capture_output=True, text=True, timeout=600)
        self.assertEqual(2, out.returncode, out.stdout + out.stderr)
        self.assertIn("no ids to run", out.stderr)

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
        # The `run:` values, not the file's text: an `assertIn` over the raw
        # workflow is satisfied by the comment block sitting directly above
        # the step, which is the one distinction this case exists to make.
        runs = [line.split("run:", 1)[1].strip()
                for line in WORKFLOW.read_text(encoding="utf-8").splitlines()
                if re.match(r"\s*-?\s*run:", line)]
        self.assertTrue(any("shard-harness-suite.py" in one for one in runs),
                        runs)
        self.assertFalse(any("unittest discover" in one for one in runs), runs)


if __name__ == "__main__":
    unittest.main()
