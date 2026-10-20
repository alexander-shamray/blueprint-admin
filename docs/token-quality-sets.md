# Token usage — the quality sets

What [`token-plan.md`](token-plan.md)'s steps 4–6 hold recall against: the
defects that audits of this repository actually found. A step that shortens a
prompt or changes a reviewer re-runs its reviewer or sweep over the same
subject and may claim its exit only if every row below it should have found is
still found. This file is a record of 2026-10-15 and is extended by a new
section, never edited.

## How the sets were built

From `gh issue list --state all`, read in full on 2026-10-15 (91 issues).
An issue joins a set only when its own body names the audit that raised it.

- **Sweep set.** Issues filed by `/bug-sweep` or `/security-sweep`, which end
  their body with the line *Filed by an authorised sweep and verified at
  filing by a second read-only auditor.* **No issue in this repository carries
  it**, so the set is empty: the sweeps have never filed here. Step 5 cannot
  claim its exit against recall until a sweep has run and been checked
  against a seeded or known defect; the empty set is the finding.
- **Review set.** Findings an audit round raised on a pull request and that
  were *deferred* into an issue. A finding fixed inside its round is in the
  commit history and in no issue, so this set under-counts: it holds what
  the loop left behind, which is the part a weaker reviewer would also leave.

## The review set

| Issue | Severity | Found by | On | What |
|---|---|---|---|---|
| #11 | low | Copilot, round 2, suppressed comment | PR #10 | the drained indicator claims the projection caught up when only the queue did |
| #40 | low | prose-versus-code auditor | PR #39 | `ship.md` restates a helper's exit count, and the number is wrong |
| #41 | low | test-rigour auditor | PR #39 | nine refusals of the rebase helper have no test case |
| #116 | low | `bug-auditor`, round 11 (implementation correctness) | PR #115 | the fake gateway answers a path it does not model before the route policy |
| #118 | low | `bug-auditor`, round 13 (README claims) | PR #115 | README omits a surface the proxy forwards |
| #141 | low | `bug-auditor`, round 2 | PR #140 | the Trace screen reports a message parked on `_error` and not on `_skipped` |
| #144 | low | test-rigour audit round | PR #139 | the token tool's suite is not run in CI |
| #145 | low | audit | PR #142 | `/branch`'s type table has no `tools/**` row |
| #147 | low | read-only review | PR #146 | the scrubber leaves part of a secret when the shape around it is unexpected (quoted or `;`-holding passwords, `Pwd=`) |
| #151 | low | `bug-auditor`, round | PR #149 | a numeric `password` JSON value is not scrubbed |
| #152 | low | `bug-auditor`, round | PR #149 | only the first element of a multi-value `authorization` array is scrubbed |
| #153 | low | `bug-auditor`, round | PR #149 | the scrubber's patterns still leave part of a secret in a handful of shapes |

A related third kind, found by reading rather than by a loop: #127 (`high`,
the drift gates read against backend `8c04435`) and #42 and #44 (profiling the
harness suite). They test a reader's recall on a known diff and are kept
beside the set, not in it.

## How to use it

1. Check out the PR's merge base, not `main`, so the defect is present.
2. Run the reviewer or sweep under test with the candidate prompt.
3. A row is *found* if a finding names its file and the same fault. Count
   found over rows; the step's exit is no fewer found than the baseline run.
4. The baseline run is owed: **no reviewer has yet been run over these rows**,
   so no recall figure exists. That, not the rows, is the remaining work
   before step 4's or step 6's exit can be claimed (#121 closes with the sets;
   the baseline run is filed separately).

## 2026-10-15 — the baseline run of `branch-reviewer`

One full pass of `branch-reviewer` per source PR, eight in all, each given
only the PR's diff (`gh pr diff <n>`) and no hint of what the set expects.
**Four of twelve rows were found, a recall of 33%.** This supersedes item 4
of *How to use it* above, which was true on the morning it was written.

| Row | PR | Found | The reviewer's finding |
|---|---|---|---|
| #11 | #10 | no | one unrelated finding about a scenario step losing its trace link |
| #40 | #39 | no | none |
| #41 | #39 | no | none |
| #116 | #115 | no | none |
| #118 | #115 | no | none |
| #141 | #140 | **yes** | a message parked in `_skipped` is not reported on the Trace screen |
| #144 | #139 | **yes** | the token tool's suite is run by no workflow or check script |
| #145 | #142 | no | none |
| #147 | #146 | **yes** | a password holding `;` is scrubbed only up to the semicolon |
| #151 | #149 | no | none |
| #152 | #149 | no | none |
| #153 | #149 | **yes** | a quoted secret followed by `, ` or `: ` leaks past the closing quote |

Read it with these limits, which the next run should remove:

- **The tree was `main`, not the PR's head.** The reviewer read the diff and
  then the files it touches as they are today, so a row whose code has since
  changed was judged from the diff alone. The reviewer said so on #140.
- **One run per PR, no repeat**, so the 33% carries no spread.
- **#141 may be contaminated.** The reviewer named #141 in its finding, so it
  had seen the later record in the tree. The row is counted found because it
  also located the fault in the diff.
- **PR #115 was read in part.** Its 2,907-line diff was read as lines 1–1063
  and 1450–2908, so #116 and #118 may lie in the gap; both rows are counted
  missed all the same, because a gap that size is what a real review of it
  would have had.
- **Every row is `low`**, so the figure says nothing about a reviewer's recall
  of a graver defect. Two of the four found rows are in the scrubber
  (`FixtureScrubber.cs`: #147, #153), one on the Trace screen (#141) and one
  a CI gap (#144); the misses include one-line prose and README claims
  (#40, #118, #145), which a diff-only reader has no reason to check.
- **`bug-auditor` was not run**, and the sweep set stays empty.

The reviewer also raised findings that match no row (PR #10 one, #140 one
nit, #146 four, #149 one more, #139 two). Nothing here verifies them; the
next section does.

## 2026-10-15 — the nine unmatched findings, checked (#168)

Each of the nine findings above was traced to its file on `main` at 8ae20e9.
**Eight hold and one does not.** That is nine findings read by one checker,
all `low`: it is a first read of the reviewer's precision and not a figure
for it.

| PR | Finding | Verdict | Where it went |
|---|---|---|---|
| #10 | a scenario step clears its correlation id when the proxy call errors | **false positive**: `exchange` in `scenario-page.ts` clears it only when the call to the host itself throws, and the host answers a platform failure as an outcome that says whether it sent (`unreached` carries `sent`), so a throw is a request the host never made | none |
| #140 | `BrokerQueue` carries both `IsErrorQueue` and `Parked` | holds: the first is `Parked == "error"` | #173 |
| #146 | the `/broker/projection` row omits the 409 | holds: `BrokerEndpoints.Answer` returns it | fixed with this section, in the design's endpoint table |
| #146 | a rebuild can run during a Stack reset, down or up job | holds: the gates are separate fields, and Compose jobs have none | #171 |
| #146 | the depth line is not refreshed when the job ends | holds: `projectionStarted` refreshes once, at the start | #172 |
| #146 | a misplaced header comment in `broker.spec.ts` | holds | #176 |
| #149 | bare `token` / `api_key` JSON keys are not named by the scrubber | holds: `TokenField` and `SecretField` name neither, and `SecretForm` covers them only as `key=value` | #174 |
| #139 | no test pins the `tools` redirection refusal in `guard-git-argv.py` | holds, and `tests` is unpinned too | #175 |
| #139 | "ported unchanged" is uncited | holds as a citation, not as a claim: `token_usage.py` and its README are identical to `blueprint-backend`'s today, modulo line endings, but `token-usage.md` names no source commit, and a worktree session cannot read the sibling's history | none; a nit |

None of the eight real findings is one of the twelve rows, so the set is not
the reviewer's whole yield, and a miss on a row does not mean the reviewer
found nothing in that PR.

**Still owed under #168**, which stays open for it: `bug-auditor` over the
set (the sweep set is still empty), and a repeated run in a detached worktree
at each PR's head, which removes the *main, not the PR's head*, *one run, no
spread* and *PR #115 read in part* limits above.

## 2026-10-15 — the rerun at each PR's head, and `bug-auditor` (#168)

Each of the eight source PRs was checked out detached at its head
(`refs/pull/<n>/head`) outside the repository, its diff written beside it, and
reviewed **twice** by `branch-reviewer` (a full pass, the diff read whole in
consecutive chunks, no impact or locality file) and **once** by `bug-auditor`
(scoped to the files the diff touches, told of no known defect). Twenty-four
reports (sixteen reviewer, eight auditor), none shown a row. This removes the three limits above and leaves two
of its own, below.

| Row | PR | Baseline (on `main`) | Reviewer, run 1 | Reviewer, run 2 | `bug-auditor` |
|---|---|---|---|---|---|
| #11 | #10 | no | no | no | no |
| #40 | #39 | no | no (near) | no | no |
| #41 | #39 | no | no | no | no |
| #116 | #115 | no | no | no | no |
| #118 | #115 | no | no | no | no |
| #141 | #140 | **yes** | no | no | no |
| #144 | #139 | **yes** | no | **yes** | no |
| #145 | #142 | no | no | no | no |
| #147 | #146 | **yes** | no | no | **yes** |
| #151 | #149 | no | no | no | no |
| #152 | #149 | no | no | no | no |
| #153 | #149 | **yes** | **yes** | **yes** | no |
| **Found** | | **4 of 12** | **1** | **2** | **1** |

**Three rows found by some run at the head (#144, #147, #153), four by some run
anywhere (#141 as well, in the baseline), eight by none of the three head columns or the baseline.** Read it
with the spread in view, which is the new fact:

- **The baseline's 33% does not repeat.** The same reviewer at the PR's head
  found one row, then two. Only #153 was found by both reviewer runs; #144
  flipped between them; #147 and #141, found in the baseline, were found by
  neither, and #147 was found by `bug-auditor` instead. A recall figure for
  this reviewer is somewhere between 8% and 33% on twelve `low` rows, and a
  step that claims "no fewer found than the baseline" must say which run it
  means.
- **#40 is a near miss, not counted.** Run 1 flagged `ship.md` claiming the
  script owns the exit counts when the script states none, which is the same
  family as #40 (a restated exit count) and not the same fault (the number
  being wrong).
- **`bug-auditor` found 1 of 12**, on a scope of the touched files with no
  hint. It is the only run at the head to find #147, and the only one to miss
  #153, so its rows and the reviewer's differ. One auditor run per PR is the
  same one-run-no-spread as before.
- **The sweep set is still empty.** `bug-auditor` here ran as a read-only
  auditor on a pinned tree, not under `/bug-sweep`, so nothing was filed by a
  sweep and step 5 still has no sweep recall to hold anything against.

Limits left: the heads include the fixes the PRs' own review rounds made, so a
row's defect may not be present at the head the way it was when the round found
it (#141 is the likely case: the PR's own change marks `_skipped`); and the
reviewer was given no impact or locality file, as in the baseline, so it read
less than the live loop gives it.

### What the twenty-four reports raised that matches no row

Traced to `main` at 059b8c4, nits not filed:

| Source | Finding | Verdict | Where it went |
|---|---|---|---|
| #142, both reviewer runs | class D of the locality map admits `tools/**` while `change-locality.md` names only documents and harness | **stale**: the doc's D row names `tools/**` today | none |
| #140, both reviewer runs | spec §5.5 and the Broker row mention only `_error` queues | **stale**: the spec covers `_skipped` and the runbook today | none |
| #149, run 1 | bare `token` / `api_key` JSON keys are not named | holds | #174, already filed |
| #149, run 1 and 2 | a quoted password that opens with `, `, `: ` or `} ` leaks | the row #153 | #153 |
| #149, run 2 | the class summary under-states what the scrubber covers | nit | none |
| #146, run 2 | the Broker e2e header comment sits above the wrong test | holds | #176, already filed |
| #146, `bug-auditor` | a password holding `;` or `@` is scrubbed in part | the row #147 | #147 |
| #146, `bug-auditor` | a rebuild test registers the repair script ahead of the reset script | holds | #185 |
| #146, `bug-auditor` | the rebuild confirmation is cleared even when the host refuses | holds, perhaps deliberate | #186 |
| #139, run 1 | `style-pass.md` sends the command to a *Style* section `CLAUDE.md` no longer has | holds | #182 |
| #139, `bug-auditor` | `default_projects()` reads any directory sharing the checkout's name prefix | holds | #183 |
| #39, runs 1 and 2 | the rebase helper's test restates "five" exit-2 conditions | holds | #184 |
| #39, run 1 | the helper may skip a commit that carries content when the apply backend stops for another reason | **unverified**: another run traced the reads and found nothing; needs a reproduction | #187 |

Of the thirteen lines, seven are findings that hold on `main` (two already
filed, five filed now), one is unverified, two are stale, two are rows, and one
is a nit; none of the new ones is one of the twelve rows.

**Still owed under #168**: the sweep set. `bug-auditor` has now been run, but no
`/bug-sweep` has filed here, and a sweep checked against a seeded defect is
what gives step 5 a recall to hold. #168 stays open for that alone.

## 2026-10-20 — the sweep set, and round-1 auditor recall over it (#168)

**The sweep set is no longer empty.** On 2026-10-18 `/bug-sweep` and
`/security-sweep` both ran over `main` at `e9731c2`, and sixteen issues carry
the sweep's filing line. That supersedes "the sweep set is empty" above. All
sixteen were confirmed and fixed (PRs #286–#299), so each is a **known defect at
`e9731c2`**. That stands in for the seeded defect #168 asked for. The bug
sweep's pinned commit is `e9731c2c184e994de8816ef8012b3815e19d6859`: the main
checkout's local `refs/sweeps/bug` pointed there when it was read on
2026-10-20. That ref moves with the next clean sweep and exists on no other
clone, so the sha is the record. The security sweep left no mark, but
`e9731c2` was `main`'s head from before the first filing to after the last.

| Issue | Sweep | Severity | Row | Slice holding it | What |
|---|---|---|---|---|---|
| #270 | bug | critical | `suites` | 12 | the runbook-name test cannot fail |
| #271 | bug | high | `host` | 4 | the realm read reports a misconfiguration when Keycloak is down |
| #272 | bug | critical | `suites` | 13 | the lease-deadline test cannot reach its guard |
| #273 | bug | critical | `suites` | 11 | the unparseable-Content-Type test passes without its guard |
| #274 | bug | critical | `suites` | 12 | the token-clock test cannot fail if `Held` ignores the password |
| #275 | security | high | `tooling` | 14 | a push to `main` spelt `heads/main` is admitted |
| #276 | security | high | `tooling` | 14 | an escaped `\<<` is read as a heredoc |
| #277 | security | high | `tooling` | 14 | an arithmetic `<<` is read as a heredoc |
| #278 | security | high | `tooling` | 14 | `rg --pre` and `git rm` reach protected trees unjudged |
| #279 | security | high | `tooling` | 14 | the substitution allow-list admits `rg` and `git` on their first word |
| #280 | security | medium | `tooling` | 13 | the edit guard admits writes to `~/.claude` memory and output styles |
| #281 | security | medium | `tooling` | 14 | `GIT_*` spellings of `-c` and `--ext-diff` |
| #282 | security | medium | `source` | 5 | the console sets no framing protection |
| #283 | security | medium | `tooling` | 15 | the lockfile check matches npm's exact indentation |
| #284 | security | medium | `tooling` | 14 | the protected-branch check covers only `push` (`send-pack`) |
| #285 | security | high | `tooling` | 14 | a `#` inside `${...}` is read as a comment |

### How it was measured

Two detached worktrees were made at `e9731c2` with `git-worktree-detach.sh`,
and each sweep's plan was cut with `sweep-slices.sh <kind> <worktree> full`:
20 bug slices and 19 security slices. A sweep's **round 1** is one auditor
per slice. These are the eight slices that hold a row: bug 4, 11, 12 and 13,
and security 5, 13, 14 and 15. One `bug-auditor` or `security-auditor` ran
on each with the sweep's own contract (root, row, list, report fields), and
nothing about the rows. A row is *found* when a finding names its file and
the same fault.

| Issue | Found | Note |
|---|---|---|
| #270 | no | `GameDayServiceTests` listed among the clean files |
| #271 | **yes** | rated `medium`, filed `high` |
| #272 | no | the auditor traced the deadline test and judged that it can fail |
| #273 | no | `RequestProxyTests` listed among the clean files |
| #274 | no | `TokenServiceTests` listed among the clean files |
| #275 | **yes** | found by slice 14's auditor, and again by slice 13's from a cross-read |
| #276 | no | |
| #277 | no | |
| #278 | partial | `git` on `READING_COMMANDS` with its writing subcommands unjudged; `rg --pre` missed |
| #279 | no | |
| #280 | **yes** | |
| #281 | no | |
| #282 | no | four other findings on the slice, none about framing |
| #283 | no | |
| #284 | no | |
| #285 | no | |
| **Found** | **3 of 16, 1 partial** | bug 1 of 5; security 2 of 11 and 1 partial |

**What this says, and what it does not:**

- **Round 1 finds about a fifth of what the full sweep filed.** The sweeps ran
  up to seven rounds, and every round after the first sends an auditor after
  the leads earlier candidates opened. That, not verification, is where the
  rest came from. The verify step judges only candidates an auditor has
  already raised, and can only drop them. So this is a lower bound on a
  sweep's recall and not its value. A step that changes the auditors' prompt can hold this
  table as its baseline: the same eight slices and round 1, with no fewer
  rows found.
- **The bug auditors miss the cannot-fail class.** All four bug rows they
  missed are tests that pass without the guard they are named for. In three
  cases the auditor named the very test file as clean. In the fourth it
  traced the test and concluded the opposite of the confirmed defect. Reading
  a test does not show that it can fail. The sweep's own round summaries were
  not kept, so which round raised each of these is not known. The round 1
  measured here is a separate run, so it cannot say whether the sweep's own
  round 1 raised them or a later round did.
- **One run per slice, no spread.** The 2026-10-15 reruns above showed the same
  reviewer finding one row and then two, so a single run is a sample, not a
  rate.
- **Scope.** Only the eight slices holding a row were run (the sweeps'
  scope hint). The other 31 were not, so this measures recall over the set and
  says nothing about precision across the tree. The auditors were not given
  the tracked-issue list the sweeps hand them. That can only add duplicate
  reports, never hide a row.
- **Cost.** Eight auditor runs, 1,292,110 subagent tokens in all, the
  largest run 203,499.

### What the eight reports raised that matches no row

Forty findings matched no row. None was checked against the code, and
several are rated at or above the sweeps' bar. They are listed for checking in
#306, with each one's file, line and claim, and are not restated here. By
report:

| Slice | Kind | Unmatched findings | At or above the sweep's bar |
|---|---|---|---|
| bug 4 (`host`) | bug | 6 | 0 |
| bug 11 (`suites`) | bug | 4 | 0 |
| bug 12 (`suites`) | bug | 6 | 0 |
| bug 13 (`suites`) | bug | 1 | 0 |
| security 5 (`source`) | security | 4 | 1 |
| security 13 (`tooling`) | security | 9 | 6 |
| security 14 (`tooling`) | security | 2 | 0 |
| security 15 (`tooling`) | security | 8 | 5 |

**#168 closes with this section.** The sweep set exists, and step 5 has a
round-1 recall over it to hold. A full seven-round sweep measured against the
same sixteen rows would give the rate rather than a bound. That run is the
owner's.
