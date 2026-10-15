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
