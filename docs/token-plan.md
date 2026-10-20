# Token usage — the plan

**Goal.** A Claude Code session here spends its tokens on the change, not on
re-reading text the change does not need. Every turn re-sends the whole
context, so a prompt that stays resident costs far more than its size.
This file records where the tokens go, sequences the work that cuts them
and names the test each step passes. It follows
`alexander-shamray/blueprint-backend`'s `docs/token-plan.md`, which owns the
method: measure first, one PR per step, an exit test per step.

The figures are not restated here. [`token-usage.md`](token-usage.md) owns
them, one dated section per measurement, and every number below cites it.

## 1. What the baseline says

From the 2026-10-15 baseline in [`token-usage.md`](token-usage.md):

- **Main sessions are the spend.** Subagents are a ninth of the input
  equivalent and `bug-auditor` most of that, so the backend's first target,
  the `general-purpose` agents, is not the one here.
- **A `/ship` main context is large.** Its mean is above 250k tokens a call
  and 45% of all history's input equivalent comes after a wake, when a
  session resumes into a context the cache may have dropped. #253 later
  found that share counts the calls after a wake, not cache lost to one
  (step 2).
- **What is resident is large and mostly unread.** `ship.md` alone is
  a third of the commands' total, and `CLAUDE.md` is in every turn; the
  sizes are in the baseline's table of what every turn carries.

## 2. The sequence

Each step names the issue that carries it. Steps that edit `.claude/**`
lift the edit deny for their own PR, per `harness-boundaries.md`, and land
one at a time.

### Step 1 — measure

- `tools/token-usage/` ported from the backend, with its suite. **Landed.**
- `token-usage.md` records an all-history total, a window and one named
  `/ship` run. **Landed**, with the gap in the window noted there.
- The quality sets are recorded in [`token-quality-sets.md`](token-quality-sets.md):
  a review set of deferred findings, and a sweep set, empty on 2026-10-15 and
  filled by the 2026-10-18 sweeps (that file's 2026-10-20 section). **Landed.**
  Steps 4–6 hold recall against them.
- The baseline run of `branch-reviewer` over the review set is recorded in
  the same file: 4 of 12 rows found. **Landed.** A step that shortens a
  prompt or changes a reviewer claims its exit by finding no fewer.
- **Owed:** the same run with `bug-auditor`, the reviewer's findings that
  matched no row verified, and a run against each PR's head, not `main`.

**Exit.** Attribution holds on a known session. **Passed** on the `/ship`
run `token-usage.md` names: its 88 calls are counted under `/ship` and its
three `bug-auditor` rounds under the same command.

### Step 2 — what fills a main session

Measure which tool results fill a `/ship` context (`--tools`) and cut the
largest: `Bash` output read whole where a count would do. Add one short
delegation rule and a rule to compact before a wait, since a wake into a
long context re-sends all of it. (It does, as every call does, and from a
warm cache: #253, below.)

**Exit.** A `/ship` main session's mean context under 200k a call, and
every first call after a wake writing under 5% of its context to the cache.

The exit's second half read "less than 35% of its input equivalent after a
wake" until #253 measured what that share counts: every call from a run's
first wake to its next prompt, which moves with where the first wait falls
rather than with what waking costs. The cost a wake can add is the cache
rewrite on the call after it, so that is what the exit now holds.

**Built** (#122): the rules. `CLAUDE.md`'s *Subagents, review rounds and
plans* held the delegation brief, the review loop (citing `/ship` step 5),
the planning rule, the compact-before-a-wait rule and one task per session;
it holds them still, but for the compact rule, retired below.
**Built** (#225): the `--tools` measurement, recorded in
[`token-usage.md`](token-usage.md), and the cut it names: step 6's `gh pr checks
--watch` takes `--interval 60`; its effect is read in the 2026-10-18 section
there. The owner's user-level copy of the #122 rules was retired outside this
repository. **Measured** (#252, #253): the shell reads by path and step and
the wakes by wait, in the second 2026-10-18 section there. The largest
`Bash` results are the reads between the branch and the first commit, about
half of them of the application and its tests, the work itself, and are not
cut. The compact-before-a-wait rule is **retired** from `CLAUDE.md`: no
session followed it in 81 wakes, none has a tool to, and the cost it was
written against is under 1%. **Exit met:** the 2026-10-18 Sonnet `/ship`
row is under 200k a call, and over the UTC day 2026-10-17, every model's
`/ship`, no first call after a wake wrote more than 1.2% of its context to
the cache.

### Step 3 — what every session loads

- `CLAUDE.md` cut to a locator and its hard rules; every rule it drops is
  cited from [`working-rules.md`](working-rules.md), its new owner.
- `docs/harness-boundaries.md` gains an index, so a session reads the one
  residual it needs.

**Exit.** `CLAUDE.md` at least 40% under its 2026-10-15 size, and the lower
per-turn resident size shown by the step 1 report. **Landed** with this
file. The first measurement, under 2026-10-15, *since #139*, could not
split the day #139 merged. The second, under 2026-10-16, *since the day
after #139*, shows the lower per-turn size of a `/ship` and does not
attribute it to this cut. **Exit met** (#123).

### Step 4 — local review

Replace the external review loops, switched off and still resident in
`ship.md`, with one local review. **Exit** on the review quality set.

**Built** (#124): `/ship` step 5 reviews in rounds of a read-only
`branch-reviewer`; the external reviewers' commands, agents, helpers,
hooks, suites and sandbox are at the tag `archive/external-reviewers`.
The exit is not measured: it holds the reviewer to the baseline recall
recorded in step 1.

### Step 5 — the sweeps

`bug-sweep.md` and `security-sweep.md` read in slices from an explicit file
list, follow leads in round two onwards, and keep the commit of the last
clean sweep. **Exit** on the sweep quality set.

**Built** (#125): `sweep-slices.sh` owns both sweeps' row partitions and
cuts each row into lists of at most 240,000 bytes; `sweep-mark.sh` keeps a
clean sweep's commit as the local `refs/sweeps/<kind>`; verification runs on
Sonnet, and the bug verifier weighs the covering tests. The argument is in
[`commands/bug-sweep.md`](commands/bug-sweep.md), *Slices, leads and the
mark*. The exit is not measured yet, but it now has a baseline: the
2026-10-20 section of [`token-quality-sets.md`](token-quality-sets.md)
records a round-1 auditor run over the sweep set (#168). A change to the
sweeps claims its exit by finding no fewer rows on the same slices.

### Step 6 — the commands

Each command splits into a runbook and its argument, the argument read only
when the command is disputed. `ship.md` is the target; its size
is in the baseline. **Exit.** A `/ship` run reaches the same end state with a
smaller resident prompt, measured by the step 1 report.

**Built** (#126): `ship`, `branch`, `review-branch`, `bug-sweep` and
`security-sweep` are runbooks in `.claude/commands/` with their argument
in [`commands/`](commands/); `test_command_runbooks.py` holds each
runbook under a ceiling and each citation to a heading that exists. The
exit is not measured: it waits on sessions run since (#123).

## 3. What each step may not do

A cut that loses a finding is not a saving. Steps 4–6 each hold recall
against the step 1 quality sets. Steps 4 and 6 do not claim their exit by
finding fewer rows than the `branch-reviewer` baseline recorded there; step 5
does not claim it by finding fewer than the round-1 auditor baseline over the
sweep set, recorded in the same file on 2026-10-20.
Steps 2 and 3 move no behaviour and need no set.
