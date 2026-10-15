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
**Four of twelve rows were found, a recall of 33%.**

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
- **Three of the four found rows are `low` in a single file**, the scrubber
  or a harness helper; the misses include one-line prose and README claims
  (#40, #118, #145), which a diff-only reader has no reason to check.
- **`bug-auditor` was not run**, and the sweep set stays empty.

The reviewer also raised findings that match no row (PR #10 one, #140 one
nit, #146 four, #149 one more, #139 two). Nothing here verifies them.
