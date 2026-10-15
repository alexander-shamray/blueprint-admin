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
