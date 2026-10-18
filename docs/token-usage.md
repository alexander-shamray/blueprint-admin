# Token usage — the measurements

What `tools/token-usage/` reported, and the other measurements
[`token-plan.md`](token-plan.md)'s steps rest on, one dated section per
measurement. Every figure is a record of its day and none is kept current;
a later measurement is a new section, never an edit of an old one.

## Running the report

`tools/token-usage/` is ported unchanged from
`alexander-shamray/blueprint-backend`'s, which owns it; its
[`README`](../tools/token-usage/README.md) says what each column and view
means. It is stdlib only and reads Claude Code's local transcripts, so a
baseline is measured on the machine that ran the sessions.

```bash
py -3.12 tools/token-usage/token_usage.py                       # all history
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01    # a window
py -3.12 tools/token-usage/token_usage.py --session <id>        # one session
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --spawns 40
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --tools 30
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --docs
py -3.12 tools/token-usage/token_usage.py --since 2026-10-01 --models
cd tools/token-usage && py -3.12 -m unittest                    # its suite
```

## 2026-10-15 — the baseline, the owner's machine

Measured by the ported report at `main` 854ee92, over every transcript
under the main checkout's directory and the 37 worktree directories beside
it. Attribution is the backend's: a command owns everything until the next
prompt a person types.

### All history, 2026-09-15 to 2026-10-15

No `--since`, so this is a total and not a rate. 10,633 responses in 587
contexts re-sent 2.10B tokens from the cache and wrote 37.3M to it, an input
equivalent of 270M; output was 6.2M. 121.5M of that input equivalent, 45%,
was spent after a wake.

| Who | Input equivalent | Contexts | Calls | Mean context per call |
|---|---|---|---|---|
| `main`, no command | 110.5M | 394 | 4,040 | 216k |
| `/ship`, `main` | 77.6M | 30 | 2,468 | 259k |
| `skill:ship`, `main` | 21.7M | 16 | 1,001 | 179k |
| `skill:commit`, `main` | 16.0M | 7 | 488 | 302k |
| `skill:branch`, `main` | 13.1M | 4 | 291 | 418k |
| `bug-auditor`, every command | 20.7M | 138 | 1,742 | 56k |

Main sessions are 89% of the spend and subagents 11%: the largest cost here
is the context a long session re-sends, not the agents it starts.
`bug-auditor` is 70% of the subagent share.

### Since 2026-10-01

The transcripts hold responses only from 2026-10-03 to 2026-10-15; the span
2026-10-08 to 2026-10-14 is absent, so `--since 2026-10-08` reports one day
and is not a window. 5,098 calls in 289 contexts, an input equivalent of
126.7M, 33.4M of it after a wake (26%). The same view with `--models`: the
`/ship` main row ran on `claude-opus-5-5` for 20 of 26 contexts and
`claude-sonnet-5-5` for the rest, and its mean context was 278k and 183k a
call respectively.

### One named `/ship` run

Session `698dc312-2b5e-4080-a6d9-6e17f0d6d782`, the run that took
`fix/drift-gates-catch-up-with-backend` to PR #132, merged as 7a2060c.

| Who | Contexts | Calls | Mean context | Input equivalent | After a wake |
|---|---|---|---|---|---|
| `/ship`, `main` | 1 | 88 | 228k | 2.37M | 0.74M |
| `/ship`, `bug-auditor` | 3 | 18 | 44k | 0.27M | 0.07M |
| no command, `main` | 1 | 7 | 80k | 0.23M | 0 |
| **Total** | 5 | 113 | 189k | **2.87M** | 0.81M |

Attribution holds on it: the command's one prompt owns 88 calls and the three
`bug-auditor` rounds the run dispatched sit under `/ship`, not under a
command of their own. For 2026-10-15 as a whole, `--tools` puts `/ship`'s
`Bash` results at 73k tokens across 210 results and its `Read` results at
22k: what a long main session's context is made of.

### What every turn and every `/ship` carries

Bytes over four, which is close for prose; none of this is a transcript
figure.

| | Tokens |
|---|---|
| `CLAUDE.md` | 3.1k, resident in every turn |
| `docs/harness-boundaries.md` | 5.8k, read whole before touching `.claude/` |
| `ship.md` | 33.7k, resident for the rest of a `/ship` run |
| `bug-sweep.md`, `security-sweep.md` | 16.6k, 12.8k |
| `branch.md`, `review-branch.md`, `review-copilot.md` | 6.9k, 5.9k, 5.6k |
| `review-grok.md`, `pr.md`, `style-pass.md`, `commit.md` | 4.7k, 3.1k, 2.8k, 1.1k |
| All ten commands | 93.0k |
| Four agent profiles and two skills | 10.7k |

A `/ship` run loads `ship`, `branch`, `commit`, `pr` and `review-copilot`
through the `Skill` tool, with `CLAUDE.md`: about 54k tokens of this
repository's text, re-sent on every later turn.

## 2026-10-15 — since #139, the owner's machine

Measured by the same report at `main` e0af55b, `--since 2026-10-15`.
#139 merged at 06:43 UTC that day, and `--since` takes a date, so the window
holds the sessions before the merge as well as after: it is the day, not the
effect of the cut.

The three largest rows of the report, and its total over every row (the
rest are `/ship` forks and `branch-reviewer`, `skill:pr` and other small
rows):

| Who | Contexts | Calls | Mean context | Input equivalent | After a wake |
|---|---|---|---|---|---|
| `/ship`, `main` | 20 | 1,599 | 254k | 47.6M | 15.3M |
| no command, `main` | 87 | 323 | 231k | 9.9M | 1.4M |
| `/ship`, `bug-auditor` | 51 | 296 | 38k | 3.4M | 1.3M |
| **Total, every row** | 178 | 2,388 | 217k | 64.3M | 18.1M |

Against the baseline above, `/ship`'s mean context is 254k a call, to be
read against 259k over all history, and its wake share 32% (15.3M of 47.6M)
against 45%. Neither difference can be attributed to the cut:

- `CLAUDE.md` fell from about 12.4 KB to 7.3 KB, some 1.3k tokens a turn out
  of 254k, which is half a percent and below the spread between a Sonnet
  and an Opus `/ship` context in the October window above.
- The weight of a `/ship` is its command text, and `ship.md` is 30.6 KB (about
  7.6k tokens) at this commit against the baseline's 33.7k: that cut came from the
  later command split (#126), not from #139.

So **the step 3 exit's second half is not shown, and not refuted**. The
measurement owed is `--since` the first day after the last prompt-shortening
merge, against this section's `/ship` row.

## 2026-10-16 — since the day after #139, the owner's machine

Measured by the same report at `main` 3df4e83, `--since 2026-10-16 --models`.
The window starts the day after #139 merged, so every session in it ran with
the cut `CLAUDE.md`; it covers 161 contexts and 2,048 calls, 55.9M input
equivalent in all.

The `/ship` rows, split by model because a Sonnet and an Opus context differ
by more than `CLAUDE.md`'s saving:

| Who | Contexts | Calls | Mean context | Input equivalent | After a wake |
|---|---|---|---|---|---|
| `/ship`, `main`, Sonnet | 13 | 716 | 192k | 16.9M | 4.7M (28%) |
| `/ship`, `main`, Opus | 2 | 143 | 148k | 2.8M | 0.2M (7%) |
| `skill:ship`, `main`, Sonnet | 1 | 119 | 211k | 2.9M | none |

Against the baselines above, `/ship`'s Sonnet mean context is 192k a call
where the 2026-10-15 day read 254k and all history 259k, and its wake share
28% where they read 32% and 45%.

- **The lower per-turn size is shown.** Every `/ship` row in the window sits
  under the earlier means, and the Sonnet row, the closest to the earlier
  mix, is 62k under the day it is compared with.
- **It is not `CLAUDE.md`'s doing.** The cut is some 1.3k tokens of that 62k.
  The rest is what else changed before the window, the command split (#126)
  and the local review (#124) among it. The report cannot apportion it, and
  this section does not.
- **Thin.** Fifteen `/ship` contexts in one day; the Opus row is two.

So **the step 3 exit is met on both halves**: `CLAUDE.md` is more than 40%
under its 2026-10-15 size, and the per-turn size of a `/ship` is lower in the
window the cut ran in. What the measurement does not show is that the cut
alone lowered it. Step 2's exit, a `/ship` main context under 200k and under
35% after a wake, reads met by the Sonnet row on this day's sessions; that is
recorded here, not claimed for step 2's rules, which land in the same change.

## 2026-10-16 — which tool results fill a `/ship` context, the owner's machine

Measured with `--tools 15 --since 2026-10-15` at `main` a8d9471, and the
`Bash` results split by the command that produced them by a scratch script
over the same transcripts (not part of the report; the report keys on tool
name only). Characters, not tokens; four to a token.

| Rank | Result | Characters | Calls |
|---|---|---|---|
| 1 | `/ship` `Bash`, all commands | 4.45M | 2,789 |
| 2 | no command, `Read` | 1.07M | 188 |
| 3 | no command, `Bash` | 0.67M | 609 |
| 4 | `/ship` `Read` | 0.59M | 131 |

Within `Bash`, the commands whose output was largest (the scratch script
sees fewer characters than the report, so only the order is read):

| Command | Characters | Calls |
|---|---|---|
| `gh pr checks` | 205k | 75 |
| a command run in the sibling backend checkout | 158k | 56 |
| `gh issue view` | 133k | 57 |
| `host-checks.sh all` | 72k | 88 |
| `codebase-index diff-impact` | 59k | 23 |
| `git diff origin/main...HEAD` | 49k | 13 |

**The cut made (#225):** `gh pr checks`, the largest, is `--watch` in
`/ship` step 6, which reprints its table every ten seconds. The step now
passes `--interval 60`. The others are reads the step needs whole or are
spread over unrelated calls; none is cut here. The effect is not yet
measured: read it on `/ship` sessions run after the change lands.

## 2026-10-18 — the effect of `--interval 60`, the owner's machine

#226 merged at 16:56 UTC on 2026-10-16, so every `/ship` from 2026-10-17 ran
with the cut. Measured at `main` a07e43b with `--since 2026-10-17` (`--tools
15` and `--models`), and the `/ship` `Bash` results split by command by a
scratch script over the same transcripts. Its *before* is 2026-10-15 up to
#226's merge and its *after* is the UTC day 2026-10-17, so the evening between
is in neither and *after* closes before the session that measured it began;
an entry with no timestamp is in neither, since the transcripts of sessions
still running would otherwise add to both. It classifies a call by the first
named command anywhere in it and the shell readers (`sed`, `cat`, `grep`) by
their first word; characters, not tokens.

| `/ship` `Bash` | Before | After |
|---|---|---|
| All results | 4.41M, 2,674 calls | 0.91M, 665 calls |
| `gh pr checks` | 236k, 89 calls, 2,651 a call | 39k, 19 calls, 2,034 a call |
| its share of all | 5.4% | 4.2% |
| `gh issue view` | 285k, 112 calls | 64k, 36 calls |
| shell file reads, `sed` + `cat` + `grep` | 1.76M (40%) | 0.34M (37%) |

- **The cut did what it says.** A `gh pr checks` call, with `--watch` or
  without, returns about a quarter less text than before. Its share of
  `/ship`'s `Bash` moved by about a point, because the call is a small part
  of the whole.
- **It was never the largest.** Classified this way, `gh pr checks` is
  seventh in both windows; the 2026-10-16 table ranked it first on a
  script that saw fewer characters. In both windows the largest `Bash`
  results are file reads through the shell, ahead of `gh issue view`.
- **Step 2's exit, half met.** The Sonnet `/ship` row (15 contexts, 869
  calls) has a mean context of 138k a call, under 200k, against 192k on
  2026-10-16. Its after-a-wake share is 6.0M of 15.4M, 39%, over 35% and up
  from 28%. The report's `--since` takes a date, so this row runs through
  2026-10-18; the measuring session is the one Opus `/ship` context, which
  is not read.

## 2026-10-18 — `/ship`'s shell reads by path and step, and its wakes by wait, the owner's machine

Measured at `main` fd46ad7 by a scratch script over the same transcripts and
the same two windows as the section above, reusing the report's own prompt,
wake and command classifiers so that a run and a wake mean what the report
means by them. Characters, not tokens, for results; input equivalent for
usage. Two classifications differ from the section above and are stated here:

- **A call is a shell file read when its first command, after any `cd`, is
  `sed`, `cat`, `grep`, `head`, `tail`, `rg` or `awk`.** The section above
  classified a call by the first *named* command anywhere in it, so a `sed`
  followed by `gh issue view` counted as `gh`. Neither is wrong; this one is
  what "a file read through the shell" means here, and it counts more.
- **A step is read off the run's own milestones**: start until the fork or
  branch (step 0), then until the first `git commit` (steps 1–2, the work and
  its checks), then until `gh-pr-create.sh` (3–4), then until
  `gh-pr-merge.sh` (5), then the teardown (6). A resumed run that starts in
  its worktree has no fork, so its reads count as step 0.

### Shell reads (#252)

| `/ship` `Bash` | Before | After |
|---|---|---|
| All results | 4.41M, 2,673 | 0.91M, 665 |
| Shell file reads | 2.50M (57%), 773 calls | 0.49M (54%), 169 calls |
| … a `sed -n a,bp` range | 1.76M, 406 calls | 353k, 97 calls, 3.6k a call |
| … `grep` | 370k, 240 calls | 69k, 51 calls |
| … a whole file (`cat`, or a reader with no range) | 369k, 127 calls | 64k, 21 calls |
| … a path the same run had already read | 208k | 41k |

| Step | Before | After |
|---|---|---|
| 0, start | 265k | 78k |
| 1–2, branch to first commit | 1.89M | 386k |
| 3–4, commit to PR | 43k | 2k |
| 5, review | 73k | 14k |
| 6, merge and teardown | 226k | 6k |

After, by what was read: the SPA 111k, the host 57k and its tests 37k, so
the application and its tests are 205k of the 486k; a pipe's own input
63k; command files 42k in four calls, from one session editing those
commands; `TODO.md` 25k in nine; docs 23k; workflows 22k; helpers 17k.

- **The largest is the work.** Three in four of these characters before,
  and four in five after, are read between the branch and the first commit,
  most of them ranges of the application and its tests, about ninety lines
  a call. `/ship` step 1 already routes that reading through the index's
  ranges; a `Read` of the same range returns the same text with line
  numbers added, so moving it off the shell saves nothing.
- **What a rule could cut is small.** Whole-file reads are 8% of `/ship`'s
  `Bash` before and 7% after, and re-reads of a path already read in the
  run 4.7% and 4.5%; a re-read after an edit is often the check of that
  edit. Step 2's premise, `Bash` output read whole where a count would do,
  describes neither.

### Wakes (#253)

| `/ship` main | Before | After |
|---|---|---|
| Input equivalent after a wake | 23.9M of 85.6M (28%) | 6.04M of 15.4M (39%) |
| Wakes | 69 | 12 |
| Context of the first call after a wake, median | 181k | 114k |
| Cache it wrote, of the context it sent | 69k of 12.0M (0.6%) | 5k of 1.94M (0.3%) |
| Wakes with a compaction since the run's prompt or last wake | 0 | 0 |

After, the twelve wakes: six are the `token-weather` plugin's prompt for a
reply that did not end with its apply-status line, three are MCP tasks
completing, two are background `harness-checks.sh` runs and one a background
`spa-ci.sh`. Counted by the wake that first put a run into the share: the
`spa-ci.sh` run 2.23M, the MCP tasks 1.57M, the plugin 1.41M and
`harness-checks.sh` 0.83M. Before, the largest first wakes were
`bug-auditor` agents (8.67M) and `harness-checks.sh` (5.54M).

- **A wake costs about what any call costs.** The call after one sends its
  whole context, and so does every call; what a wake could add is the cache
  rewrite when the wait outlasted the cache, and that is under 1% of the
  context in both windows: the waits measured all ended inside the cache's
  lifetime.
- **So the share counts work, not waking.** A call is "after a wake" from a
  run's first wake to its next prompt, so the share moves with how early in
  a run the first background wait falls. Foregrounding the waits would lower
  it and leave the same calls, billed the same, from the same warm cache.
- **No session compacted before a wait, in 81 wakes**, and none could: a
  session has no tool that compacts its own context, and a compaction
  rewrites the context it keeps into the cache, which a warm wake does not.
