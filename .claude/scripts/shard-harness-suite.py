#!/usr/bin/env python3
"""Run the harness suite as parallel class shards, covering what the serial run covered.

**Every caller of this suite ran two `unittest discover` roots serially, and
paid about seventeen minutes for it on Windows.** Half the runtime is in a
handful of classes and almost none of it is approximable: roughly seven tenths
of the suite is real `bash`, real `git` and real `gh` on purpose, because
re-implementing a `grep` pattern in Python's `re` would be a second
specification — `test_harness_helpers.py` says so in its own words. So the lever
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
this checkout's own `.git`. `WEIGHT` is a measurement that makes the order
better and cannot make it wrong: a class missing from it is costed on its test
count, and a class whose entry has gone stale is dealt late and still runs.
A name in any table that no longer resolves to a discovered class fails a
test, so a renamed class loses its pinning loudly rather than silently.

**Classes are dealt from a queue rather than packed ahead of time.** Packing
had to guess every class's cost before the run and paid for each wrong guess
with a whole worker: the slowest shard took 1.43-1.52x the mean in every
green run measured, with unlisted classes costing ten to forty times what
their test count implied (#322). Now each worker takes the next unit when it
finishes the last, longest first, so a wrong guess costs one class. A unit is
claimed by renaming its file, which exactly one worker can do, and moves on
to `done/` only once it has run, so a unit a dead worker dropped is still
visible to the check that every unit finished.

**One run per host.** Each run sizes its pool for the whole host, so two at
once — two sessions, two worktrees — ran sixteen workers and each took about
three times as long (#323). A run takes a lock in the host's temp directory
first, and a second run waits for it, saying so, rather than running beside
it. The operating system releases the lock when its holder exits however it
exits, so there is no stale lock to recover.

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

if os.name == "nt":
    import msvcrt
else:
    import fcntl

HERE = Path(__file__).resolve()
ROOT = HERE.parents[2]

# The two roots every caller used to name for itself, and `tools/token-usage`,
# whose suite nothing ran until it joined them (#144). They live here, so
# `harness-checks.sh` and the `harness` CI job cannot drift apart about what
# the suite is; `test_harness_shards.py` asserts each one still contributes.
ROOTS = ((".claude/scripts", "test_*.py"),
         (".github/locality-gate", "test*.py"),
         (".github/commit-gate", "test*.py"),
         ("tools/token-usage", "test_*.py"))

# Whose subject is time or a port, and so cannot share a box with seven
# workers. `TheRefresh` asserts that a hook command returns inside two seconds
# — that is the claim, not an incidental bound.
SERIAL = frozenset({
    "test_index_refresh_hook.TheRefresh",
})

# Who shares state outside their own fixture, and therefore shares a unit.
# The first three create and list fixed `secsweep-*` names in the shared bash
# temp root and drive `git worktree add`/`remove` against this checkout; the
# fourth reads the whole working tree and asserts a finding list is empty, so
# a concurrent worktree round trip is exactly what could disturb it. The
# last makes a detached sweep worktree of this checkout, in the same
# `secsweep-*` namespace the first lists. `test_sweep_slices.ASmallRepository`
# makes its sweep worktrees of a synthetic repository under a temp root of its
# own, so it shares nothing and runs wherever it is dealt.
TOGETHER = {
    "test_harness_helpers.SweepWorktreeShape": "the checkout",
    "test_harness_helpers.NoCommandHoldsAPrefixGrantThatAdmitsAForbiddenFlag": "the checkout",
    "test_harness_helpers.TheFourPortedResiduals": "the checkout",
    "test_root_ignore_coverage.ThisRepository": "the checkout",
    "test_sweep_slices.TheRealTree": "the checkout",
}

# Seconds a class took, every one of them from the slowest-classes data one
# queued run of the whole suite printed, on one Windows host on 2026-10-20
# (#322), with #320 and #321 already in. Nothing reads these as a fact about
# anything. They set the order the queue deals units in, longest first, and
# under a queue the order is all a weight can buy: a class dealt late runs
# late and still runs.
#
# **Contended, and that is the right condition for them.** Each class was
# timed whole, `setUpClass` included, while seven other workers ran beside
# it, so these are the times a class takes inside the run it is ordered for.
# They are several times the uncontended floor. That does not matter, because
# they are only read against each other, and a host with less load scales
# them together.
#
# **Refreshed whole rather than entry by entry, and that is the point.** These
# numbers are read against each other, so a table half old and half new orders
# worse than either. The one this replaces was taken one class at a time on
# 2026-09-22, before the queue, and had gone stale in both directions: one
# class sped up ten times over in #320, and three it did not list cost 10-40x
# their test count.
#
# **A class is listed when it measured 5 s or more, OR when it holds ten tests
# or more.** Two arms because there are two ways to order the queue wrongly.
# Slower than its test count is the obvious one: an unlisted class costs
# `UNMEASURED` per test, and the slowest class here holds 18 tests. Faster
# than its test count is the other: `TheGitArgvGuard`'s many tests would be
# costed far above what it takes, and it would be dealt ahead of the classes
# that decide the run. A class under both arms is dealt on its test count, and
# is too small for the difference to reach the wall.
#
# The seconds are not restated here on purpose: the table owns them, and a
# comment that carries a second copy is the drift this repository keeps
# finding. Every run ends by printing its slowest classes, so the next
# refresh needs no profile.
#
# SERIAL classes are absent by construction: `plan()` sets them aside before
# it reads this, so an entry for one would never be consulted.
WEIGHT = {
    "test_git_pr_trailer_helpers.TrailerHelper": 458.5,
    "test_git_rebase_onto_main.TheHelperPublishesWhatItRebased": 450.2,
    "test_sweep_slices.ASmallRepository": 423.8,
    "test_git_rebase_onto_main.AConflictIsTheCaseRebaseIsHereFor": 262.6,
    "test_git_rebase_onto_main.TheApplyBackendIsDrivenToo": 231.7,
    "test_harness_helpers.TheLauncherProvesWhatItRemembers": 159.5,
    "test_git_worktree_fork.ForkShape": 150.0,
    "test_index_refresh_hook.TheSeed": 147.2,
    "test_git_rebase_onto_main.TheHelperRefusesBeforeItRewrites": 137.8,
    "test_git_pr_trailer_helpers.RevertHelper": 131.3,
    "test_harness_helpers.ARememberedGuardAnswersAsAProbedOne": 131.2,
    "test_index_refresh_hook.TheEvent": 122.9,
    "test_git_rebase_onto_main.AStoppedReplayIsNotAlwaysAConflict": 105.2,
    "test_sweep_slices.TheRealTree": 104.0,
    "test_harness_helpers.IssueHelperHasNoFreeParameter": 73.7,
    "test_harness_helpers.LandingByRebaseMovedTheReadsThatAssumedAMergeCommit": 72.0,
    "test_harness_helpers.LabelHelperBehaviour": 60.8,
    "test_git_rebase_onto_main.ALegacyMergeForwardIsNotSilentlyDropped": 58.7,
    "test_harness_helpers.TheGitArgvGuard": 52.5,
    "test_index_query_hint.TheHint": 37.8,
    "test_edit_target_guard.WhatThisGuardIsNotTheSubjectOf": 31.3,
    "test_index_search_hint.WhatItNeverTouches": 29.8,
    "test_harness_helpers.TheHookWiringRunsOnMoreThanOneOperatingSystem": 29.1,
    "test_root_ignore_coverage.TheScan": 25.0,
    "test_harness_helpers.OnlyThisCheckoutsPullRequestsSurvive": 23.7,
    "test_git_rebase_onto_main.TheFixtureCopyIsTheFixture": 21.3,
    "test_harness_helpers.WhatSuppressesIsDecidedByCodeNow": 18.8,
    "test_commit_gate.Reading": 18.6,
    "test_harness_helpers.NoCommandHoldsAPrefixGrantThatAdmitsAForbiddenFlag": 17.2,
    "test_index_search_hint.TheFirstSymbolSearchIsRefusedOnce": 14.0,
    "test_harness_helpers.SweepWorktreeShape": 12.7,
    "test_edit_target_guard.TheOrdinaryWriteIsNotDisturbed": 10.7,
    "test_edit_target_guard.ALinkIsNotTheFileItIsSpelledAs": 8.7,
    "test_git_rebase_onto_main.TheBranchNameGuardsEachHaveTheirOwnCase": 7.5,
    "test_root_ignore_coverage.ThisRepository": 5.8,
    "test_harness_shards.TheRunnerRefusesRatherThanReportingAPass": 4.4,
    "test_harness_helpers.CommandsEnforceTheEditingBoundariesTheyState": 4.0,
    "test_token_usage.ReportTests": 0.6,
    "test_harness_shards.ThePlacementIsDerivedFromDiscovery": 0.1,
    "test_token_usage.CommandLineTests": 0.1,
    "test_locality_gate.Verdicts": 0.0,
    "test_locality_gate.MapGrammar": 0.0,
    "test_locality_gate.TouchSetGrammar": 0.0,
    "test_locality_gate.Matching": 0.0,
    "test_commit_gate.Judgement": 0.0,
}

# What an unmeasured class is assumed to cost per test. Deliberately coarse:
# the classes that decide the makespan are all in WEIGHT, and a wrong guess
# here costs a later turn in the queue and nothing else.
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


def plan(found):
    """Cut every id into the units the queue deals, and the serial tail.

    A unit is one class, or every class one `TOGETHER` group names. The units
    come back longest first, which is the order the workers take them in and
    the only place WEIGHT is read: a long class dealt last is the one wrong
    guess a queue still pays for in full. Between them the units and the tail
    hold `found` exactly once — which is the property `test_harness_shards.py`
    checks rather than takes.
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

    order = sorted(members, key=lambda u: (-cost[u], u))
    return [members[unit] for unit in order], tail


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


def unfinished(work, units):
    """The units of a queued run that never reached `done/`.

    `unplaced` checks what was queued; this checks what came back. A worker
    that died mid-unit leaves its unit in `taken/`, one that refused an id
    leaves it there too, and a unit no worker reached is still in `queue/` —
    all three are tests that did not run, however every worker exited.
    """
    done = {path.name for path in (work / "done").glob("*.ids")}
    return [name for name in units if name not in done]


def settle_streams():
    """Write UTF-8 to a log file, whatever the code page says.

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


def loaded():
    """Every discovered test case, by id, in discovery order."""
    tests = {}
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        for test in walk(suite):
            tests[test.id()] = test
    return tests


def run_listed(wanted):
    """Run exactly these ids, and refuse the run if discovery lost one.

    The refusal is the runtime half of the coverage rule: a worker handed a
    name nothing discovers has been told to run something that does not exist,
    and reporting that as a pass is the failure this file exists to avoid. A
    worker handed nothing at all is the same failure with nothing to name.
    The serial tail runs this way, alone, after the queue has drained.
    """
    settle_streams()
    if not wanted:
        print("shard-harness-suite.py: this worker was given no ids to run",
              file=sys.stderr)
        return 2
    tests = loaded()
    outstanding = sorted(set(wanted) - set(tests))
    if outstanding:
        print("shard-harness-suite.py: discovery does not hold "
              f"{len(outstanding)} of the ids this worker was given: "
              f"{outstanding[:5]}", file=sys.stderr)
        return 2
    wanted = set(wanted)
    chosen = [test for test_id, test in tests.items() if test_id in wanted]
    result = unittest.TextTestRunner(verbosity=2).run(unittest.TestSuite(chosen))
    return 0 if result.wasSuccessful() else 1


def claim(work):
    """Take the next unit off the queue: its name, or None when it is empty.

    The rename is the claim. It succeeds for one worker and fails for every
    other — with a missing file, or on Windows a sharing violation while the
    winner's rename is in flight — so a failure only means "not this one".
    A worker stops when the queue reads empty, never on a failed rename, so a
    unit is never left behind by a race.
    """
    queue, taken = work / "queue", work / "taken"
    while True:
        names = sorted(path.name for path in queue.glob("*.ids"))
        if not names:
            return None
        for name in names:
            try:
                os.rename(queue / name, taken / name)
            except OSError:
                continue
            return name
        time.sleep(0.05)


def run_queue(work):
    """Run units off the queue in `work` until it is empty.

    Discovery happens once per worker, not once per unit: it imports every
    module in every root. Each class in a unit runs as its own suite, in
    discovery order, so its time is measured whole — `setUpClass` included —
    and written beside the unit as it moves to `done/`, which is where the
    parent reads the slowest classes from. A unit holding an id discovery does
    not hold is refused and left in `taken/`, so the parent's check that every
    unit finished fails on it by name.
    """
    settle_streams()
    tests = loaded()
    status = 0
    while (name := claim(work)) is not None:
        ids = (work / "taken" / name).read_text(encoding="utf-8").split()
        outstanding = sorted(set(ids) - set(tests))
        if not ids or outstanding:
            print(f"shard-harness-suite.py: unit {name}: discovery does not "
                  f"hold {len(outstanding)} of its ids: {outstanding[:5]}",
                  file=sys.stderr)
            status = 2
            continue
        times = []
        for cls in dict.fromkeys(owner(test_id) for test_id in ids):
            began = time.monotonic()
            result = unittest.TextTestRunner(verbosity=2).run(
                unittest.TestSuite(tests[i] for i in ids if owner(i) == cls))
            times.append(f"{time.monotonic() - began:.1f}\t{cls}\n")
            if not result.wasSuccessful():
                status = max(status, 1)
        (work / "done" / f"{name}.times").write_text("".join(times),
                                                     encoding="utf-8")
        os.rename(work / "taken" / name, work / "done" / name)
    return status


def spawn(args, log):
    """One worker process, writing everything to one log.

    The environment is this process's, unchanged: a worker settles its own two
    streams in `settle_streams`, and anything set here would reach every
    process the tests under it spawn.
    """
    return subprocess.Popen([sys.executable, str(HERE), *args],
                            cwd=str(ROOT), stdout=log,
                            stderr=subprocess.STDOUT)


def report(logs):
    """Print what each worker wrote, whatever ended the run.

    Called from a `finally`, and before the working directory goes: a run
    interrupted at minute four otherwise showed nothing at all, where the
    serial command it replaced had been streaming test names the whole time.
    Bytes straight through — decoding a worker's output here only to encode
    it again is what once killed the parent printing a run it had finished.
    """
    for title, log in logs:
        if not log.exists():
            continue
        print(f"\n===== {title} =====", flush=True)
        sys.stdout.buffer.write(log.read_bytes())
        sys.stdout.buffer.flush()


def slowest(work, count=15):
    """The classes that took longest, from every unit that finished.

    Printed at the end of every run so `WEIGHT` can be refreshed from data
    rather than from a profile somebody has to remember to take.
    """
    rows = []
    for path in (work / "done").glob("*.times"):
        for line in path.read_text(encoding="utf-8").splitlines():
            seconds, cls = line.split("\t")
            rows.append((float(seconds), cls))
    return sorted(rows, reverse=True)[:count]


# One run per host, in the host's temp directory rather than the checkout's,
# because what two runs contend for is the host: two worktrees of this
# repository, or two clones, are two checkouts and one set of cores.
LOCK = Path(tempfile.gettempdir()) / "blueprint-admin-harness.lock"


def try_lock(handle):
    """Take the lock on <handle> if it is free; whether it was taken.

    Both are locks the operating system drops when the process holding them
    exits, killed or not, which is why there is no stale-lock recovery here.
    """
    try:
        if os.name == "nt":
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return False
    return True


def unlock(handle):
    if os.name == "nt":
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
    else:
        fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


@contextlib.contextmanager
def host_lock(path=LOCK, poll=5.0):
    """Hold the host's harness lock, waiting for another run to finish first."""
    with open(path, "a+b") as handle:
        if not try_lock(handle):
            print(f"shard-harness-suite.py: another harness run on this host "
                  f"holds {path}; waiting for it rather than running beside "
                  f"it", flush=True)
            while not try_lock(handle):
                time.sleep(poll)
        try:
            yield
        finally:
            unlock(handle)


def main(argv):
    if len(argv) == 3 and argv[1] == "--ids":
        return run_listed(Path(argv[2]).read_text(encoding="utf-8").split())
    if len(argv) == 3 and argv[1] == "--queue":
        return run_queue(Path(argv[2]))
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

    units, tail = plan(found)
    phases = [("unit", ids) for ids in units]
    if tail:
        phases.append(("serial", tail))

    missing, extra = unplaced(found, phases)
    if missing or extra:
        print("shard-harness-suite.py: the runs do not cover discovery — "
              f"{len(missing)} test(s) would never run {missing[:5]}, "
              f"{len(extra)} unaccounted for {extra[:5]}", file=sys.stderr)
        return 2

    count = max(1, min(workers(), len(units)))
    print(f"shard-harness-suite.py: {len(found)} tests in {len(units)} units "
          f"over {count} parallel workers, then {len(tail)} serial",
          flush=True)

    with host_lock():
        return run_all(units, tail, count)


def run_all(units, tail, count):
    """Queue the units, drain the queue with `count` workers, then the tail."""
    work = Path(tempfile.mkdtemp(prefix="harness-shards-"))
    for part in ("queue", "taken", "done"):
        (work / part).mkdir()
    names = [f"{n:04d}.ids" for n in range(len(units))]
    for name, ids in zip(names, units):
        (work / "queue" / name).write_text("\n".join(ids), encoding="utf-8")

    logs = [(f"worker {n}", work / f"worker-{n}.log") for n in range(count)]
    if tail:
        logs.append((f"serial: {len(tail)} tests", work / "serial.log"))
    began = time.monotonic()
    codes, running, left, times = [], [], [], []
    try:
        with contextlib.ExitStack() as open_logs:
            for _, path in logs[:count]:
                log = open_logs.enter_context(path.open("wb"))
                running.append(spawn(["--queue", str(work)], log))
            for proc in running:
                codes.append(proc.wait())

        # The tail goes last and alone, which is what "serial" means here.
        if tail:
            listing = work / "serial.ids"
            listing.write_text("\n".join(tail), encoding="utf-8")
            with logs[-1][1].open("wb") as log:
                codes.append(spawn(["--ids", str(listing)], log).wait())
        left = unfinished(work, names)
        times = slowest(work)
    finally:
        # A worker still running here is one this process is abandoning, and
        # several of them drive `git worktree` against this checkout.
        for proc in running:
            if proc.poll() is None:
                proc.terminate()
        report(logs)
        shutil.rmtree(str(work), ignore_errors=True)

    print("\nshard-harness-suite.py: slowest classes, in seconds:")
    for seconds, cls in times:
        print(f"  {seconds:7.1f}  {cls}")
    bad = [title for (title, _), code in zip(logs, codes) if code != 0]
    if len(codes) != len(logs):
        print(f"\nshard-harness-suite.py: {len(codes)} of {len(logs)} runs "
              f"reported at all", file=sys.stderr)
        return 1
    if left:
        print(f"\nshard-harness-suite.py: {len(left)} of {len(names)} units "
              f"never finished, so their tests did not run: {left[:5]}",
              file=sys.stderr)
        return 1
    print(f"\nshard-harness-suite.py: {len(units)} units and "
          f"{1 if tail else 0} serial run in "
          f"{time.monotonic() - began:.0f}s, "
          f"{', '.join(bad) + ' failed' if bad else 'all green'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
