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
