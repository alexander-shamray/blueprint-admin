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

The plans are independent and may land in any order, with one overlap. The
frontend and golden-signals plans both change the Stack screen
(`stack-page.*`, `e2e/stack.spec.ts`), README's Stack description and spec
§5.10. Whichever lands second rebases onto the first: the golden-signals
plan renames the Compose table's selector, and the frontend plan changes
the e2e reachability count, so each must re-read the other's test edits
rather than replay its own.

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
