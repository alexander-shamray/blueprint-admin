---
name: codebase-index
description: Use before answering repository questions about architecture, implementation, symbols, references, dependencies, refactoring impact, data flow, or bugs. Query the local hybrid index first so the agent reads only evidence-bearing file:line ranges instead of scanning the repository, and verify evidence gathered earlier before relying on it.
allowed-tools: Bash(codebase-index search:*), Bash(codebase-index explain:*), Bash(codebase-index architecture:*), Bash(codebase-index symbol:*), Bash(codebase-index refs:*), Bash(codebase-index impact:*), Bash(codebase-index diff-impact:*), Bash(codebase-index path:*), Bash(codebase-index describe:*), Bash(codebase-index verify:*), Bash(codebase-index stats:*), Bash(codebase-index doctor:*), Bash(codebase-index update:*), Bash(codebase-index index:*), Read, Grep, Glob
---

# Codebase Index

Use the local index before reading repository files.

The operating principle is **Find → Trace → Verify → Predict**:

- **Find** the implementation with ranked retrieval.
- **Trace** behavior through definitions, callers, dependencies, and paths.
- **Verify** that evidence you already hold is still true before relying on it.
- **Predict** change impact while preserving an explicit evidence trail.

## Route the question

| Intent | Command |
|---|---|
| Where is X implemented? | `codebase-index search "X" --limit 5 --session <tag> --json` |
| How does X work? | `codebase-index explain "X" --session <tag> --json` |
| What is this codebase? | `codebase-index architecture --json` |
| Find a named symbol | `codebase-index symbol "X" --json` |
| Who calls or references X? | `codebase-index refs "X" --json` |
| What changes if X changes? | `codebase-index impact "X" --json` |
| What does my current diff affect? | `codebase-index diff-impact --json` |
| How are X and Y connected? | `codebase-index path "X" "Y" --json` |
| Describe X and its neighborhood | `codebase-index describe "X" --json` |
| Is what I read earlier still true? | `codebase-index verify --session <tag> --json` |
| Produce a human graph | `codebase-index graph "X" --output <path>` — **not auto-approved**; take the prompt |

**Pass `--limit 5` to `search`**, which is the only subcommand that takes
the option at all. The default is ten, so the results past the fifth arrive
whether or not they are read. On this corpus five is where the payload halves
at no cost: over eight "where is X" questions, ten ranks cost roughly 18,300
tokens and five roughly 9,700, and both find the implementation 8/8 at a mean
rank of 2.50. Three does not — it loses two of the eight, which sit at ranks
four and five.

**That number is this corpus's and not a law.** `blueprint-backend` measured
the same thing on its own tree and settled on three. **Ask again with a
larger `--limit` whenever the five do not answer the question** — not only
when they come back empty, which is the rarer case and not the one this
number risks: an answer ranked sixth arrives behind five results that are
neither empty nor useful, so an emptiness test could never fire for it. A
short result set is never an absence.

Use `search --mode symbol` for exact symbol work, `--mode fts` for text and
error messages, and the default `hybrid` mode for mixed questions. Use pure
`vector` mode only when embeddings are enabled and exact vocabulary is unknown.

Read [references/commands.md](references/commands.md) only when command options
or routing remain unclear.

## Evidence protocol

1. Pick one session tag for this conversation (for example `auth-fix-1`) and
   pass `--session <tag>` to every `search` and `explain`.
2. Run the best-matching command with `--json`.
3. Check `index` before trusting the payload:
   - missing → run `codebase-index index`, then repeat;
   - stale with fewer than 20 changed files → run `codebase-index update`;
   - stale with 20 or more changed files → run `codebase-index index`;
   - fresh → continue.
4. Start with ranks 1–3, and read on to the fifth before asking again: a
   quarter of the answers measured on this corpus sat at rank four or five.
   Read only `recommended_reads` line ranges.
5. Trace one additional hop only when the question requires behavior,
   ownership, or impact.
6. Before answering or editing from evidence gathered earlier in the task, run
   `codebase-index verify --session <tag> --json` and reread anything whose
   state is not `valid` or `relocated`.
7. Answer with `file:line` evidence and state uncertainty explicitly.

Do not open whole files when a line range is available. A snippet may already
be sufficient. `skeletonized: true` means the response intentionally folded
unrelated body lines; read the supplied range when the missing body matters.

## Evidence memory

- `reused: true` with `snippet: null` — this session already received that
  exact text and its source is unchanged. Use your earlier copy; if you can no
  longer see it, Read the range.
- `memory.invalidated` — evidence this session received has changed since.
  Treat your earlier copy as wrong and reread before relying on it.
- `stale: true` — the index is older than the file. Run `codebase-index update`
  or Read the range.
- A tag belongs to one context. Never give it to a subagent or another
  conversation. Start a new tag after the context is cleared or compacted, or
  whenever earlier snippets are no longer visible to you.

Verdict states and citing evidence in notes: [references/memory.md](references/memory.md).

## Confidence contract

- **high** — answer from the indexed evidence.
- **medium** — read the recommended ranges and confirm the key claim with one
  targeted lookup if necessary.
- **low** or no results — follow `fallback_suggestions`, then use a narrow
  Grep/Glob fallback.

On `refs` and `impact`, an empty result is inconclusive whatever `coverage`
reports. The C# graph carries call edges and not every use: a registration
like `builder.Services.AddSingleton<JobRegistry>()` is recorded against
neither name, so `impact "JobRegistry" --direction up` answers with no
dependents and `coverage.partial: false` while twelve other files name the
class. Confirm with a targeted Grep before saying that nothing references the
target, and never report an empty graph result as an absence.

Edges carry `confidence`:

- `extracted` — exact parser evidence;
- `inferred` — heuristic resolution;
- `ambiguous` — unresolved or non-unique.

Never present an inferred or ambiguous chain as certain.

## Answer contract

Structure repository answers around:

1. **Answer** — the direct conclusion.
2. **Evidence** — the minimum supporting `file:line` references.
3. **Confidence** — only when evidence is partial, inferred, stale, or missing.
4. **Next check** — only when another check would materially reduce uncertainty.

Do not narrate every search step. Do not claim absence from the graph.
Do not replace evidence with a generated HTML graph.

For payload fields and failure handling, read
[references/response-contract.md](references/response-contract.md).
