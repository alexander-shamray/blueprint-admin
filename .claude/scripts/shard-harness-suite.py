#!/usr/bin/env python3
"""Run the harness suite as parallel class shards, covering what the serial run covered.

**Every caller of this suite ran two `unittest discover` roots serially, and
paid about twenty minutes for it on Windows.** Half the runtime is in a
handful of classes and almost none of it is approximable: roughly seven tenths
of the suite is real `bash`, real `git` and real `gh` on purpose, because
re-implementing a `grep` pattern in Python's `re` would be a second
specification — `test_grok_helpers.py` says so in its own words. So the lever
is concurrency rather than rewriting, and the unit is the class, because a
class is what shares a fixture.

**What has to be true here is coverage, not speed.** A splitter that quietly
stops placing a class is this repository's most-repeated failure in its
cheapest form: every worker green and one class never run. So placement is
derived from discovery and never from a list written by hand — a class nobody
has heard of is placed on weight alone, `test_harness_shards.py` compares the
placement back against discovery at several shard counts, and a worker refuses
to start when it is handed an id discovery does not hold.

**Three tables, and every one of them is a schedule hint rather than a fact.**
`SERIAL` names the classes whose subject is elapsed time or a listening port,
so running them beside seven other workers would measure the load instead.
`TOGETHER` names the ones that share something outside their own fixture — a
`secsweep-*` namespace in the shared temp root, and `git worktree` against
this checkout's own `.git`. `WEIGHT` is a measurement that makes the packing
better and cannot make it wrong: a class missing from it is packed on its test
count, and a class whose entry has gone stale is packed badly and still runs.
A name in any table that no longer resolves to a discovered class fails a
test, so a renamed class loses its pinning loudly rather than silently.

**The floor is the longest class.** Class sharding cannot split one, so the
makespan never goes below the slowest class however many workers there are;
past eight the packing stops improving and the interleavings start. That is
why the count is capped rather than tuned, and why the remaining lever on the
long poles is inside those classes and not here (issue #43).
"""

import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve()
ROOT = HERE.parents[2]

# The two roots every caller used to name for itself. They live here now, so
# `harness-checks.sh` and the `harness` CI job cannot drift apart about what
# the suite is; `test_harness_shards.py` asserts each one still contributes.
ROOTS = ((".claude/scripts", "test_*.py"),
         (".github/locality-gate", "test*.py"))

# Whose subject is time or a port, and so cannot share a box with seven
# workers. `TheRefresh` asserts that a hook command returns inside two seconds
# — that is the claim, not an incidental bound — and `WhatTheProxyRefuses`
# dials an ephemeral port it has just released, which a neighbour can take.
SERIAL = frozenset({
    "test_index_refresh_hook.TheRefresh",
    "test_egress_proxy.WhatTheProxyRefuses",
})

# Who shares state outside their own fixture, and therefore shares a worker.
# The first three create and list fixed `secsweep-*` names in the shared bash
# temp root and drive `git worktree add`/`remove` against this checkout; the
# fourth reads the whole working tree and asserts a finding list is empty, so
# a concurrent worktree round trip is exactly what could disturb it.
TOGETHER = {
    "test_grok_helpers.SweepWorktreeShape": "the checkout",
    "test_grok_helpers.NoCommandHoldsAPrefixGrantThatAdmitsAForbiddenFlag": "the checkout",
    "test_grok_helpers.TheFourPortedResiduals": "the checkout",
    "test_root_ignore_coverage.ThisRepository": "the checkout",
}

# Seconds, as profiled on one Windows host on 2026-09-21 and recorded in
# issue #43. Nothing reads these as a fact about anything; they exist so the
# two longest classes land on different workers, which is the whole of the
# difference between a good packing and a random one.
WEIGHT = {
    "test_grok_helpers.TheGitArgvGuard": 225.6,
    "test_grok_helpers.CopilotFeedHelpersAreTheOnlyIntake": 222.9,
    "test_git_rebase_onto_main.TheHelperPublishesWhatItRebased": 131.9,
    "test_git_rebase_onto_main.AConflictIsTheCaseRebaseIsHereFor": 112.9,
    "test_grok_helpers.TheTriagerEditsNothingShipDenies": 80.0,
    "test_grok_helpers.AFeedHelperReturnsTheWholeAnswer": 47.2,
    "test_root_ignore_coverage.ThisRepository": 40.2,
    "test_grok_helpers.AnAuthorsFilenameDoesNotSteerTheTriage": 38.9,
}

# What an unmeasured class is assumed to cost per test. Deliberately coarse:
# the classes that decide the makespan are all in WEIGHT, and a wrong guess
# here costs a worse packing and nothing else.
UNMEASURED = 1.0

# Eight workers was the knee: sixteen bought twenty-five seconds and produced
# failures. The host's own core count caps it below that.
CAP = 8


def workers():
    """How many parallel workers this host gets."""
    return max(1, min(CAP, os.cpu_count() or 1))


def walk(suite):
    """Every test case under a suite, however deeply nested."""
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from walk(item)
        else:
            yield item


def discovered():
    """Every test the serial run ran, in the order it ran them.

    Import failures arrive as loader placeholders rather than as an exception,
    which is what keeps a broken module a red test in some worker instead of a
    module that quietly stops being discovered.
    """
    found = []
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        found.extend(test.id() for test in walk(suite))
    return found


def owner(test_id):
    """The class an id belongs to — the unit everything here schedules."""
    return test_id.rsplit(".", 1)[0]


def plan(found, count):
    """Place every id on a worker or in the serial tail.

    Longest unit first into the emptiest worker: the ordinary packing, and the
    only place WEIGHT is read. Returns the workers' id lists and the tail, and
    between them they hold `found` exactly once — which is the property
    `test_harness_shards.py` checks rather than takes.
    """
    tail = [test_id for test_id in found if owner(test_id) in SERIAL]

    members, cost = {}, {}
    for name in dict.fromkeys(owner(test_id) for test_id in found):
        if name in SERIAL:
            continue
        group = [test_id for test_id in found if owner(test_id) == name]
        unit = TOGETHER.get(name, name)
        members.setdefault(unit, []).extend(group)
        cost[unit] = cost.get(unit, 0.0) + WEIGHT.get(name,
                                                      UNMEASURED * len(group))

    buckets = [[] for _ in range(max(1, count))]
    loaded = [0.0] * len(buckets)
    for unit in sorted(members, key=lambda u: (-cost[u], u)):
        at = loaded.index(min(loaded))
        buckets[at].extend(members[unit])
        loaded[at] += cost[unit]
    return buckets, tail


def run_listed(wanted):
    """Run exactly these ids, and refuse the run if discovery lost one.

    The refusal is the runtime half of the coverage rule: a worker handed a
    name nothing discovers has been told to run something that does not exist,
    and reporting that as a pass is the failure this file exists to avoid.

    **The two streams are reconfigured because they are a file rather than a
    console**, which on Windows makes them the ANSI code page, and unittest
    writes test names and failure messages carrying literal dashes; the parent
    then reads back bytes that are not UTF-8. It is done here rather than in
    the worker's environment on purpose: an inherited `PYTHONIOENCODING`
    reaches every `bash`, `git` and hook the tests spawn, and what those
    behave like unaided is the whole subject of this suite.
    """
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    outstanding = set(wanted)
    chosen = []
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        for test in walk(suite):
            if test.id() in outstanding:
                outstanding.discard(test.id())
                chosen.append(test)
    if outstanding:
        print("shard-harness-suite.py: discovery does not hold "
              f"{len(outstanding)} of the ids this worker was given: "
              f"{sorted(outstanding)[:5]}", file=sys.stderr)
        return 2
    result = unittest.TextTestRunner(verbosity=2).run(unittest.TestSuite(chosen))
    return 0 if result.wasSuccessful() else 1


def spawn(listing, log):
    """One worker, reading its ids from a file and writing everything to one.

    The environment is this process's, unchanged: a worker settles its own two
    streams in `run_listed`, and anything set here would reach every process
    the tests under it spawn.
    """
    return subprocess.Popen([sys.executable, str(HERE), "--ids", str(listing)],
                            cwd=str(ROOT), stdout=log,
                            stderr=subprocess.STDOUT)


def main(argv):
    if len(argv) == 3 and argv[1] == "--ids":
        return run_listed(Path(argv[2]).read_text(encoding="utf-8").split())
    if len(argv) != 1:
        print("usage: shard-harness-suite.py        (it takes no arguments)",
              file=sys.stderr)
        return 2

    found = discovered()
    buckets, tail = plan(found, workers())
    phases = [("shard", bucket) for bucket in buckets if bucket]
    if tail:
        phases.append(("serial", tail))
    print(f"shard-harness-suite.py: {len(found)} tests over "
          f"{len(phases)} runs ({len(buckets)} parallel, "
          f"{1 if tail else 0} serial)", flush=True)

    work = Path(tempfile.mkdtemp(prefix="harness-shards-"))
    began = time.monotonic()
    codes = []
    try:
        # The tail goes last and alone, which is what "serial" means here.
        running = []
        for n, (kind, ids) in enumerate(phases):
            if kind == "serial":
                continue
            listing = work / f"{n}.ids"
            listing.write_text("\n".join(ids), encoding="utf-8")
            handle = (work / f"{n}.log").open("wb")
            running.append((n, handle, spawn(listing, handle)))
        for n, handle, proc in running:
            codes.append(proc.wait())
            handle.close()
        for n, (kind, ids) in enumerate(phases):
            if kind != "serial":
                continue
            listing = work / f"{n}.ids"
            listing.write_text("\n".join(ids), encoding="utf-8")
            with (work / f"{n}.log").open("wb") as handle:
                codes.append(spawn(listing, handle).wait())

        for n, (_, ids) in enumerate(phases):
            log = work / f"{n}.log"
            print(f"\n===== run {n}: {len(ids)} tests =====", flush=True)
            if log.exists():
                # Straight through as bytes. Decoding a worker's output here
                # only to encode it again is what turned one dash into a
                # parent that died printing a run it had already finished,
                # with four runs' results and the roll-up still unsaid.
                sys.stdout.buffer.write(log.read_bytes())
                sys.stdout.buffer.flush()
    finally:
        shutil.rmtree(str(work), ignore_errors=True)

    bad = [n for n, code in enumerate(codes) if code != 0]
    print(f"\nshard-harness-suite.py: {len(codes)} runs in "
          f"{time.monotonic() - began:.0f}s, "
          f"{'runs ' + str(bad) + ' failed' if bad else 'all green'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
