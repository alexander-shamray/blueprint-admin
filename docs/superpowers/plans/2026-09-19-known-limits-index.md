# Known limits — which are lifted, and by which plan

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`.
**Source of the list:** `README.md`, "Known limits", as of `e8e4f57`.

README's list mixes two kinds of line: a gap that this repository can close,
and a boundary that is the design working as intended or a fact another
repository owns. Only the first kind gets a plan. Each plan below is one
subsystem, produces working, tested software on its own, and amends the spec
and README in the same change, because a known-limits line that moves is a
README fact and a rule that moves is a spec fact.

## Lifted

| Plan | Limits it lifts |
|---|---|
| [Jobs: stoppable follow and quiet reads](2026-09-19-known-limits-jobs.md) | Stop on the Logs screen leaves `logs -f` running until the next Follow; broker reads appear in `GET /api/jobs`, and auto-refresh re-reads queues only |
| [Frontend: install and a client started by hand](2026-09-19-known-limits-frontend.md) | The console never runs `npm ci`; a client started by hand is not noticed, and Start runs a second `npm start` against port 5173 |
| [API screen: lasting history and additive users](2026-09-19-known-limits-api.md) | History is kept only while the screen is open; configuring any user replaces demo/demo and browser/browser |
| [Golden signals: the rest of the row](2026-09-19-known-limits-golden-signals.md) | The strip shows rate, errors and duration only; a service with no 5xx shows a dash instead of a zero |

**One order is required: jobs before frontend.** The frontend plan's "Show
npm ci output" reads the job registry by id, and until the jobs plan stops
recording the Stack poll's `ps` reads, an exited install is evicted from it
within minutes. The other plans may land in any order, but not in
isolation: each was written against `e8e4f57`, and these files are named by
more than one of them.

| File | Plans |
|---|---|
| `README.md` | all four |
| the design spec | all four; jobs and frontend both amend §5.10 and §6 |
| `src/Admin.Web/src/app/core/host/host-client.ts` and its spec | jobs, frontend |
| `src/Admin.Web/src/app/core/host/host-types.ts` | frontend, golden-signals |
| `src/Admin.Web/src/app/features/stack/stack-page.*` | frontend, golden-signals |
| `src/Admin.Web/e2e/stack.spec.ts` | frontend, golden-signals |
| `src/Admin.Host/Fakes/FakeProcessRunner.cs` | jobs (`LastStarted`), frontend (`IsRunning`) |
| `tests/Admin.Host.Tests/Fakes/FakePlatformTests.cs` | jobs, frontend |

The table is a warning, not the rule: it was read off the plans by hand and
has already been found short twice. **A plan executed after another has
landed re-reads every file it edits on `main` before its first edit to that
file**, listed here or not, and applies its change to what is there rather
than replaying its own code blocks, which quote `e8e4f57`. Two cases make
replaying lose work rather than merely conflict: the golden-signals plan
renames the Compose table's selector that the frontend plan's Stack tests
use, and the frontend plan rewrites the reachability assertion that a
golden-signals rewrite of `stack.spec.ts` would restore. The code blocks are
the intent; the file on `main` is the base.

## Kept, and why

- **Stop gives up waiting after 10 s.** Spec §5.2: "a stop never hangs" is
  the rule, and the line in README is its honest cost. The frontend plan
  narrows the practical effect: after a Stop that gave up, the Stack screen
  still says whether port 5173 answers.
- **The proxy sends only to the configured URLs, and refuses a correlation id
  the platform would replace.** Spec §8 and §5.9: the URL list is what keeps a
  loopback console from being a request forwarder, and the id's alphabet is
  the backend's adoptable set (`CorrelationIdExtensions.cs`) and the LogQL
  injection boundary. Neither is a gap.
- **Operation examples are placeholders where `run-locally.md` has none.**
  The OpenAPI documents carry no examples, and spec §1.2 says nothing the
  backend owns is invented here. The fix starts in `blueprint-backend`'s
  documents. Only once they carry an `example` is reading it here worth a
  plan: `ExampleBuilder` reads schemas only today, and a reader for a keyword
  no document uses would be tested against nothing real.
- **A timeline ends at the outbox.** The outbox row carries no trace context,
  already raised for `blueprint-backend`, which owns the fix (phase 5 plan,
  M2).
