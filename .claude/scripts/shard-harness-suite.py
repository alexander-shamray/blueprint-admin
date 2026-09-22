#!/usr/bin/env python3
"""Run the harness suite as parallel class shards, covering what the serial run covered.

**Every caller of this suite ran two `unittest discover` roots serially, and
paid about seventeen minutes for it on Windows.** Half the runtime is in a
handful of classes and almost none of it is approximable: roughly seven tenths
of the suite is real `bash`, real `git` and real `gh` on purpose, because
re-implementing a `grep` pattern in Python's `re` would be a second
specification — `test_grok_helpers.py` says so in its own words. So the lever
is concurrency rather than rewriting, and the unit is the class, because a
class is what shares a fixture.

**What has to be true here is coverage, not speed**, and the shape a failure
takes is always the same: every worker green, and some part of the suite never
run. So nothing here reports a pass it has not earned.

* Placement is derived from discovery, never from a list written by hand. A
  class nobody has heard of is placed on weight alone.
* `plan()` is checked against discovery by `test_harness_shards.py`, and the
  path from `plan()` to the workers is checked again at runtime, in `main()`,
  because a plan that covers everything and a run that covers everything are
  two different claims.
* A root that discovers nothing refuses the run. An empty suite is what a
  renamed directory or a pattern that stopped matching looks like, and the
  case that would have caught it is among the tests that stopped being found,
  so it cannot be left to report zero tests and exit 0.
* A worker refuses to start when handed an id discovery does not hold, or no
  ids at all.

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

import contextlib
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

# Seconds a class costs, every one of them measured on one Windows host on
# 2026-09-22 (#46). Nothing reads these as a fact about anything; they exist so
# the long poles land on different workers, which is the whole of the
# difference between a good packing and a random one.
#
# **The lowest of repeated serial passes, and the repeats are not ceremony.**
# #46 records that this host's variance is a third of a run, and it is right:
# two passes over identical code differed by 26% overall and by 2.8x on one
# class. A single pass measures the load as much as the class. The MINIMUM
# rather than the mean, because a class has a floor — the work it actually
# does — and everything above it is somebody else's process; the mean of a
# contended sample estimates the contention. It is also the direction this
# table wants to be wrong in least, since an inflated weight makes the packer
# reserve room nothing needs, which is the mistake it was already making.
#
# **Refreshed whole rather than entry by entry, and that is the point.** These
# numbers are read against each other, so a table half old and half new packs
# worse than either — and the eight this replaces were taken under a loaded
# sharded run while these were taken one class at a time.
#
# **A class is listed when it measured 5 s or more, OR when it holds ten tests
# or more.** Two arms because there are two ways to steer the packer wrongly
# and the table this replaces caught only one. Slower than its test count is
# the obvious one: `ThisRepository` is two tests and 54 s, and five classes
# above 19 s were not in the old table at all. FASTER is the one that was
# missed — an unlisted class costs `UNMEASURED` per test, so
# `TheGitArgvGuard`'s 168 tests would be costed at 168 s against a real 25 s
# and the packer would hand it a worker of its own. A class under both arms is
# packed on its test count, and is too small for the difference to reach the
# wall.
#
# SERIAL classes are absent by construction: `plan()` sets them aside before
# it reads this, so an entry for one would never be consulted.
#
# Summing the minima gives 584 s of serial work, against the 1029 s #46
# records before the spawn cuts. That sum is a floor rather than a wall: no
# run is ever this fast, because no run is ever uncontended.
WEIGHT = {
    "test_git_rebase_onto_main.TheHelperPublishesWhatItRebased": 73.9,
    "test_git_rebase_onto_main.AConflictIsTheCaseRebaseIsHereFor": 57.2,
    "test_grok_helpers.CopilotFeedHelpersAreTheOnlyIntake": 56.0,
    "test_git_rebase_onto_main.TheApplyBackendIsDrivenToo": 41.1,
    "test_root_ignore_coverage.ThisRepository": 33.3,
    "test_git_rebase_onto_main.AStoppedReplayIsNotAlwaysAConflict": 27.8,
    "test_git_rebase_onto_main.TheHelperRefusesBeforeItRewrites": 26.5,
    "test_grok_helpers.TheGitArgvGuard": 23.3,
    "test_grok_helpers.AnAuthorsFilenameDoesNotSteerTheTriage": 22.3,
    "test_grok_helpers.AFeedHelperReturnsTheWholeAnswer": 20.8,
    "test_grok_helpers.IssueHelperHasNoFreeParameter": 19.0,
    "test_grok_helpers.LandingByRebaseMovedTheReadsThatAssumedAMergeCommit": 17.5,
    "test_grok_helpers.CopilotFeedFilter": 14.8,
    "test_grok_helpers.TheCeilingBindsAndTheReadStaysWider": 14.0,
    "test_grok_helpers.LedgerDoesNotFailOpen": 12.6,
    "test_git_rebase_onto_main.ALegacyMergeForwardIsNotSilentlyDropped": 12.4,
    "test_grok_helpers.LabelHelperBehaviour": 11.0,
    "test_git_rebase_onto_main.TheFixtureCopyIsTheFixture": 7.2,
    "test_grok_helpers.TheTriagerDispatchesOnlyTheAdjudicator": 6.4,
    "test_grok_helpers.DidItRunAllowList": 6.4,
    "test_edit_target_guard.WhatThisGuardIsNotTheSubjectOf": 5.0,
    "test_grok_helpers.TheFourPortedResiduals": 3.5,
    "test_grok_helpers.TheTriagerEditsNothingShipDenies": 1.9,
    "test_grok_helpers.UsageLimitPattern": 1.9,
    "test_grok_helpers.WhatSuppressesIsDecidedByCodeNow": 1.8,
    "test_edit_target_guard.TheOrdinaryWriteIsNotDisturbed": 1.2,
    "test_grok_helpers.CommandsEnforceTheEditingBoundariesTheyState": 0.9,
    "test_locality_gate.Verdicts": 0.2,
    "test_locality_gate.Matching": 0.2,
    "test_locality_gate.TouchSetGrammar": 0.1,
    "test_locality_gate.MapGrammar": 0.1,
}

# What an unmeasured class is assumed to cost per test. Deliberately coarse:
# the classes that decide the makespan are all in WEIGHT, and a wrong guess
# here costs a worse packing and nothing else.
UNMEASURED = 1.0

# Eight workers was the knee: sixteen bought twenty-five seconds and produced
# failures. The host's own core count caps it below that.
CAP = 8


class NothingDiscovered(RuntimeError):
    """A root that matches nothing is not an empty suite to report green."""


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
    module that quietly stops being discovered. A root that yields nothing at
    all is different in kind and refuses the run: a renamed directory or a
    pattern that stopped matching looks exactly like an empty suite, and the
    case that would have caught it is among the tests that stopped being
    found.
    """
    found = []
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        ids = [test.id() for test in walk(suite)]
        if not ids:
            raise NothingDiscovered(
                f"{start} matching {pattern!r} discovered no tests at all")
        found.extend(ids)
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


def unplaced(found, phases):
    """What discovery holds that the runs do not, and the reverse.

    `plan()` covering everything and the runs covering everything are two
    claims, and only the first has a test that can see it. This is the second,
    checked where the plan becomes subprocesses.
    """
    placed = [test_id for _, ids in phases for test_id in ids]
    missing = sorted(set(found) - set(placed))
    extra = sorted(set(placed) - set(found))
    if not missing and not extra and len(placed) != len(found):
        # Same set, different multiset: something is placed twice.
        extra = sorted({i for i in placed if placed.count(i) > 1})
    return missing, extra


def run_listed(wanted):
    """Run exactly these ids, and refuse the run if discovery lost one.

    The refusal is the runtime half of the coverage rule: a worker handed a
    name nothing discovers has been told to run something that does not exist,
    and reporting that as a pass is the failure this file exists to avoid. A
    worker handed nothing at all is the same failure with nothing to name.

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
    if not wanted:
        print("shard-harness-suite.py: this worker was given no ids to run",
              file=sys.stderr)
        return 2
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


def report(phases, work):
    """Print what each run wrote, whatever ended the run.

    Called from a `finally`, and before the working directory goes: a run
    interrupted at minute four otherwise showed nothing at all, where the
    serial command it replaced had been streaming test names the whole time.
    Bytes straight through — decoding a worker's output here only to encode
    it again is what once killed the parent printing a run it had finished.
    """
    for n, (_, ids) in enumerate(phases):
        log = work / f"{n}.log"
        if not log.exists():
            continue
        print(f"\n===== run {n}: {len(ids)} tests =====", flush=True)
        sys.stdout.buffer.write(log.read_bytes())
        sys.stdout.buffer.flush()


def main(argv):
    if len(argv) == 3 and argv[1] == "--ids":
        return run_listed(Path(argv[2]).read_text(encoding="utf-8").split())
    if len(argv) != 1:
        print("usage: shard-harness-suite.py        (it takes no arguments)",
              file=sys.stderr)
        return 2

    try:
        found = discovered()
    except NothingDiscovered as empty:
        print(f"shard-harness-suite.py: {empty}. Refusing the run rather than "
              f"reporting a pass over nothing.", file=sys.stderr)
        return 2

    buckets, tail = plan(found, workers())
    phases = [("shard", bucket) for bucket in buckets if bucket]
    parallel = len(phases)
    if tail:
        phases.append(("serial", tail))

    missing, extra = unplaced(found, phases)
    if missing or extra:
        print("shard-harness-suite.py: the runs do not cover discovery — "
              f"{len(missing)} test(s) would never run {missing[:5]}, "
              f"{len(extra)} unaccounted for {extra[:5]}", file=sys.stderr)
        return 2

    print(f"shard-harness-suite.py: {len(found)} tests over "
          f"{len(phases)} runs ({parallel} parallel, "
          f"{1 if tail else 0} serial)", flush=True)

    work = Path(tempfile.mkdtemp(prefix="harness-shards-"))
    began = time.monotonic()
    codes, running = [], []
    try:
        with contextlib.ExitStack() as open_logs:
            for n, (kind, ids) in enumerate(phases):
                if kind == "serial":
                    continue
                listing = work / f"{n}.ids"
                listing.write_text("\n".join(ids), encoding="utf-8")
                log = open_logs.enter_context((work / f"{n}.log").open("wb"))
                running.append(spawn(listing, log))
            for proc in running:
                codes.append(proc.wait())

        # The tail goes last and alone, which is what "serial" means here.
        for n, (kind, ids) in enumerate(phases):
            if kind != "serial":
                continue
            listing = work / f"{n}.ids"
            listing.write_text("\n".join(ids), encoding="utf-8")
            with (work / f"{n}.log").open("wb") as log:
                codes.append(spawn(listing, log).wait())
    finally:
        # A worker still running here is one this process is abandoning, and
        # several of them drive `git worktree` against this checkout.
        for proc in running:
            if proc.poll() is None:
                proc.terminate()
        report(phases, work)
        shutil.rmtree(str(work), ignore_errors=True)

    bad = [n for n, code in enumerate(codes) if code != 0]
    if len(codes) != len(phases):
        print(f"\nshard-harness-suite.py: {len(codes)} of {len(phases)} runs "
              f"reported at all", file=sys.stderr)
        return 1
    print(f"\nshard-harness-suite.py: {len(codes)} runs in "
          f"{time.monotonic() - began:.0f}s, "
          f"{'runs ' + str(bad) + ' failed' if bad else 'all green'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
