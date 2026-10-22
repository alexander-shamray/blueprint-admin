# blueprint-admin

A local admin console for [`blueprint-backend`](https://github.com/alexander-shamray/blueprint-backend):
one page that runs, stops, observes and exercises the platform, doing verbatim
what the workspace's `run-locally.md` says a developer does by hand.

It runs on the developer's workstation, binds loopback only, and holds the
realm passwords, so it is never deployed and never shared. The design is
`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`.

## Prerequisites

Docker Desktop, the .NET SDK pinned in `global.json`, the Node version in
`.nvmrc`, and the two sibling clones beside this one:

    ../blueprint-backend    alexander-shamray/blueprint-backend
    ../blueprint-frontend   alexander-shamray/blueprint-frontend

Other locations: set `Admin:BackendDir` and `Admin:FrontendDir` (see
`src/Admin.Host/Config/AdminOptions.cs`) on the command line
(`--Admin:BackendDir=D:/work/backend`) or as `BLUEPRINT_Admin__BackendDir`.
The host refuses to start if either is wrong, naming the key.

## Run it

Build the console once — from `src/Admin.Web`:

    npm ci && npm run build

Then, from this directory:

    dotnet run --project src/Admin.Host

Open **http://127.0.0.1:5300**. The console is the built SPA served from
`src/Admin.Host/wwwroot`; without a build the host still starts and serves
only `/api`.

To work on the SPA, run the host as above and, in `src/Admin.Web`, `npm start`;
open **http://127.0.0.1:5301**, which proxies `/api` to the host (dev-server
port and proxy target are `src/Admin.Web/angular.json` and
`src/Admin.Web/proxy.conf.json`).

## No platform? Fake it

    dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true

Every process and every HTTP surface is replayed from recordings in
`src/Admin.Host/Fakes/`. This is how the SPA is developed and how CI runs the
Playwright smoke; it needs no Docker and no clones.

The fixture files are what the live platform answered, scrubbed of tokens and
passwords before they were written. To re-record them against a running stack:

    dotnet run --project src/Admin.Host -- --Admin:Record=true

then open the screens that read them and review the diff;
[`docs/testing.md`](docs/testing.md) has the walk.

## What it does today

Phases 0 to 6 of the spec: the Stack screen (a workstation doctor read before
Up: Docker, the published ports and what holds them, the gateway's CORS
origins, Node against the client's `.nvmrc`, `py -3.12` for the dead-letter
workbench, k6 for the load run, both clones against their last
fetch, image age, and whether the image store was provisioned (`object-store`
and `image-edge` healthy, `object-store-init` exited 0); then Compose services, reachability,
up, down, down with a typed `down -v` confirmation, Reset behind the same
confirmation, which runs `down -v`, up and the readiness wait as one job, live
output; the reference
client's `npm start` with start, stop and its output; a table of each
service's golden signals from Prometheus, one column per panel of the
backend golden-signals dashboard: request rate, 5xx share, p99 latency, the
422 and 401 refusals, and §13.7's command and query p95; beside it, the alert rules Prometheus
loaded, firing first, and the backend's `awaiting-signal.yaml` rules listed apart
as waiting, each with the metric it lacks; and the load run, the backend's
§13.7 SLO run as one job, block by block as `run-locally.md`'s "Load run
(k6)" runs it, with Stop, so the strip is read under load), the Logs screen
(follow, service filter, text filter, correlation-id highlight) and the API
screen (Catalog's, Ordering's, Inventory's, Payments' and Privacy's OpenAPI operations
through the gateway, the BFF quote, its product-image upload and saved address,
a stored image read back through the image edge, every host's readiness and the payment
and carrier simulators' request logs; send as anonymous, a realm user, with
the held token's time left and grants beside the picker, or a custom username
and password, with a correlation id; status, timing, headers
and body as the platform returned them; a history of this visit's calls).
The Broker screen lists queues (`_error` and `_skipped` queues marked, each named
with what consumes it, and a non-empty one linking the backend runbook that owns
it), exchanges, bindings (what each exchange feeds, a queue or another
exchange, with its routing key) and permissions through `rabbitmqctl` in the
`rabbitmq` container; below the queues
a BFF projection panel says what `tools/bff-replay` cannot restore and runs it
as a repair, or as a rebuild behind a typed confirmation that stops the BFF,
deletes its order projection, replays the outbox window and starts it again
(the commands are `run-locally.md`'s); and the API
screen shows whether `ordering-catalog-events` has drained after a publish.
The Broker screen's dead-letter workbench opens on a parked queue that holds a
message and lists each message with its fault, type, body and a link on its
correlation id. Replay or Discard runs dry first, its output in the job pane
below the messages, and the real run is offered only for the message and verb
that dry run was started for, behind the typed `--execute`. Each run mints a
password for the broker's dead-letter account, uses it and clears it, so the
account has none between runs; the record and audit files land under
`Admin:DataDir`'s `dead-letters/` (the temp directory against FakePlatform).
The tool is the backend's; spec §5.5 and `run-locally.md`'s "Replay or discard
a dead letter" say how it is run.
The Erasure screen raises an erasure request for a subject id as the realm's
`privacy` login (the one that holds `privacy:erase`; `demo` does not, and a stack
whose realm was imported before that login needs `docker compose down -v`), then
shows one row per holder Privacy asked, answered, waiting or overdue, with the
depth of its `*-privacy` queue and of `privacy-completions` beside it; an overdue
request is reissued from the screen and links the backend's `erasure-overdue`
runbook. Against FakePlatform the subject ending `dead` is the one that goes
overdue, so the reissue has something to do.
The Map screen shows each service of the platform with the gateway routes that reach it and the queues it consumes,
each queue's depth, its change per second between two reads (Refresh, or every 5 s) and the exchanges bound to it. It
is built from the host's own tables and the broker's `rabbitmqctl` reads at view time; where they differ it lists the
disagreement and resolves nothing.
The Runbooks screen lists the backend's `docs/runbooks/` as its `tools/game-day`
lists them (`py -3.12 tools/game-day/game_day.py --list --json`, read on every
request, so a new runbook appears without a console change). For one with a
script it shows each alert's state beside the runbook's text, read from the
clone and never copied, with what a cause costs and the tool's deadlines for the
alert to fire and clear. Cause runs `RUNBOOK --cause`, and Undo runs `RUNBOOK
--restore`; both are `run-locally.md`'s "Game day" lines, as jobs under the same
gate as Up and Down. A cause the tool lists as `confirm` takes a second press of
the button, and one it lists as `typed` takes the runbook's name typed, as the
wipe takes `down -v`. Against FakePlatform four runbooks are listed and a cause
fires its alert in the alert read until Undo. Spec §5.14 has the rest.
The Mail screen lists what Notifications sent, newest first, as the mail sink
(Mailpit) holds it: when, to whom, the subject, and the message's
`X-Correlation-Id` as a link into its Trace timeline; a correlation id typed
into its filter leaves one order's messages alone. Opening a message shows
its body: the HTML part in a frame sandboxed with no scripts, or the plain
text. It only reads: clearing the sink is done in the sink's own UI, which it
links, and the sink's chaos API is never called.
The Trace screen joins a correlation id's Loki lines and Tempo spans into one
timeline that follows the request across the broker into each consume, naming
the fulfilment saga's steps, Inventory's reservation, Payments' authorisation
and Shipping's despatch; each row links into Grafana Explore, closing with a
snapshot of the projection queue and every other queue the spans crossed; the
API screen's "Trace this call" opens it on the
response's own correlation id. The Scenario screen runs `run-locally.md`'s
calls end to end as a realm user, in one of a library of scripts: publish, wait for
the projection to drain, set its stock on hand, quote, order, and then either read the order back
through the BFF until it is confirmed, despatched and delivered and then count the mail it sent
(one message for each event it raised, read from the mail sink by the order's id; a second message
for one event fails the step; the cancel, declined, out-of-stock, carrier and timeout scripts count theirs too), cancel it
and read it back until it is cancelled, or apply a cause `run-locally.md` names
(a price ending `.01`, stock set to 0) and read it back until the saga has declined
it or found it out of stock, or order to the delivery postal code `SIM-TRANSIT`, which the carrier
simulator collects and never delivers, read the order back despatched, and see a cancel
of it refused with 422 `order.already_shipped`. The `stock-timeout` and `payment-timeout` scripts hold a
lease on the stack (stop Inventory, shorten the saga's waits), read the order end `out_of_stock` or
`declined`, and restore the stack whatever happens, including on Cancel. The `despatch-timeout` script shortens
the saga's waits, orders to `SIM-STRANGE`, and reads from Loki the line Ordering logs when it flags the order for review
(`not_despatched`) while the order read stays confirmed. The `release-timeout` script orders a `.09` total, waits for its
stock to reserve, stops Inventory on the lease it already holds, cancels the order, and reads the `stock_not_released`
line the unanswered release raises while the order read says cancelled. The `carrier-refused`, `carrier-down`,
`carrier-slow` and `carrier-strange` scripts order to the postal code that scripts the carrier simulator, read the
simulator's own request log for what the carrier saw, and hold the order read at confirmed. Each watch says what it is waiting
for and what it last read; the run stops at the first step that does not
succeed, and each sent step links to its own trace. A step for which the host
mints a new token, because the one it held was due to renew, says so. The
`image` script walks `run-locally.md`'s "Attach a product image by hand": the BFF
signs an upload, the host PUTs a sample PNG to the object store, the product is
published with the key it returned, and the thumbnail is fetched from the image
edge and must be an image served with `nosniff`. Beside the steps a saga strip shows the order's
state as the BFF last reported it beside the saga's own (Ordering's `/saga` read, which `demo` may make),
when each was read, how long each transition took, and whether the pair agrees, has the buyer running ahead
of the saga, or is a disagreement; it says it is the
last state read and the only stuck-saga detector a workstation has (the backend's `StuckSaga` alert cannot
fire locally); **Export Markdown** writes a finished run, with its transcript, steps, correlation ids, trace
timelines and queue depths, as one document scrubbed by the host, to paste into an issue.

The Transcript screen shows what the operator did this session as a person
would type it: each command the host ran, with its directory and exit code, and
each request the proxy sent, as the `curl` that sends it with the identity it
went as and the status that came back. The `ps` and `exec` reads the screens
poll are left out. "Copy as shell script" copies it as one bash script. Tokens,
passwords and Authorization values pass through the scrubber the fixtures are
held to and read `<scrubbed>`.

The API screen also says what each service's OpenAPI document changed since
the console last kept it: operations, response codes and schema fields added
or removed, and since when. The first document seen for a service is kept as
its baseline under `Admin:DataDir`, and "Accept as baseline" keeps the current
one. It reads only the documents the screen already fetches. The Trace screen
keeps the last few timelines per correlation id for the browser session, and a
reload says what arrived or left since the previous fetch, or that nothing
changed between the two.

Ctrl+K (Cmd+K) opens a command palette from any screen: go to a screen, run
the Scenario, or paste a correlation id to land on its Trace timeline. The
reference client's error banner prints the id of a failed call (`Correlation
id: …`), so a failure in the client is one paste from its timeline. Job output
is a log a screen reader can read on demand; only a job's start and its exit
are announced.

The top bar's language picker switches the console between English and
Russian, and reloads the page to do it; it waits while a layout change is
still being saved. The first visit follows the browser's language when the
console has it. So far the shell, the palette and the shared components are
translated; each screen follows when its workspace phase replaces it. Platform
responses, logs, command output, typed confirmations and service and queue
names stay as written.

## Known limits

- A game-day cause stays until Undo is pressed, and a host that is stopped while
  one is in force does not undo it: the stack stays as the cause left it, and the
  backend's own `--restore` (run-locally.md, "Game day") puts it back. The alert
  column needs the backend's alert rules loaded by Grafana and reads `not loaded`
  for a rule Prometheus does not list. A restore does not take back the orders a
  cause placed or `contact-refused`'s deleted contacts.

- The host keeps at most one `logs -f` job, so a Follow in a second browser
  tab ends the first tab's. A follow whose answer never reached the page,
  or whose stop failed as the Logs screen was left, runs until the next
  Follow.
- The console runs `npm start` but never `npm ci`: with no `node_modules` in
  the frontend clone, or one whose installed versions differ from its
  `package-lock.json`, Start is refused and the screen says which (the doctor's
  "Frontend install" row reads the same). A lock file it cannot read does not
  block Start.
- The device lane builds the client for the emulator (`npm run emulator:android`)
  and reads whether an emulator is attached, whether the gateway admits
  `https://localhost` and what the running realm's `mobile-app` redirects to.
  It does not start the AVD, install or start the app, or sign in on the
  device, and a mismatch it reports is raised in the backend, not fixed here.
  Its build and reads answer from recordings under FakePlatform, written
  from `run-locally.md` without an emulator to record.
- A Scenario lease is the console's one change to a service beyond Up, Down and the BFF
  rebuild: stopping `inventory-api`, or recreating `ordering-api` with the backend's
  short-timeouts override. It is journaled under `Admin:DataDir` before it is made and
  undone by Restore, at its deadline (10 minutes at most), and at the next host start. The Stack
  screen shows a lease in force and refuses Up, Down and the reset while it is held. A host
  killed outright cannot undo it: the service stays as the run left it until the next
  start, or until Restore is pressed on a host that is running.
- The release and despatch scripts read the line Ordering logs when it flags an order for review. The review line is a
  trace and not the queue: it repeats on a redelivery and ages out with Loki's retention, so a flag proves the row was
  raised and its absence proves nothing. The confirmation timeout is not reachable by hand.
- The carrier-log scripts assert what the carrier saw and not the shipment's own state (`Unfulfillable`,
  `Pending`, `Booked`), which is in a table the console does not query. The log carries no order id
  (blueprint-backend#1074), so the step reads it from when the order was placed and groups retries by the
  booking's `shipmentId`; an order to the same code at the same time by another run is not told apart.
- The console does not track a client started by hand: Start still runs a
  second `npm start`, which competes for port 5173, and its output shows
  what `ng serve` did.
- A client the console started may be in use by the client's own Playwright
  run, which reuses a dev server it finds outside CI; Stop ends it under that
  run. Nothing detects the run, because the host cannot observe one.
- Stop gives up waiting after 10 s and says so in the output; the process
  may still be running.
- The proxy sends only to the `Admin:*Url` surfaces that
  `RequestProxy.Surfaces()` lists, and refuses a correlation id the platform
  would replace (anything but 1 to 128 ASCII letters, digits, `-` and `_`).
- Operation examples are the bodies `run-locally.md` sends where there is one,
  otherwise the document's own `example`; only a body the document declares
  without one falls back to placeholders built from the schema.
- API history is kept only while the screen is open.
- OpenAPI baselines live under `Admin:DataDir`, which defaults to a directory
  inside `/artifacts/`; deleting it starts every service's baseline again.
- Trace history lasts as long as the browser tab, and a refetch over a
  different window is not compared.
- The workstation doctor makes no network call, so a clone is judged against
  its last fetch, and it reads listeners, the client's `.nvmrc` and k6
  through PowerShell, so off Windows the Ports, Node and k6 rows cannot tell. It judges the web client's origin against the
  gateway's CORS list; which native origins a device build needs is #82's.
- The token clock reads only what the host holds: a custom identity's clock
  shows once that identity has been used, and a token never minted has none.
- Shipping has no API. What it did is read from the carrier simulator's request
  log, and an order's despatch and delivery from the BFF's buyer order read.
  That read is the buyer's view: the Scenario watches the steps the BFF
  projected. The saga's own state is read beside it for the strip, but the
  backend stores no time of entering a state (blueprint-backend#1093), so
  the strip's "last changed" for the saga is when the console first saw it
  so. Every read a watch makes is a request the Transcript keeps.
- The identity picker offers demo/demo and browser/browser; configuring any user
  replaces both: `--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`.
- The transcript lives in the host's memory and ends with it, keeping the
  newest `OperatorTranscript.Capacity` entries. It holds what the host ran and
  the proxy sent, so a Keycloak token grant is not in it: its curl lines carry
  `<scrubbed>` where a token goes, to be minted as `run-locally.md` does.
- The golden-signal strip sums the 422 panel's routes into one rate per
  service; Grafana's panel shows the split. Its command and query p95 are
  quantiles over the backend's `request.duration` histogram, so they are only
  as fine as that histogram's bucket boundaries.
- The load run's Stop ends the step going by killing its process tree, not by
  k6's own Ctrl+C: a Stop before k6 drives no load, and a Stop during k6 skips
  its `teardown()`, so that run has no verdict. A run leaves its
  orders, the `slo-run` client and the restocked count as they are, and a
  local run proves nothing about the SLO (§13.7).
- The Mail screen lists the sink's newest `MailSinkClient.Limit` messages and
  says how many it holds in all. Opening a message marks it read in the sink,
  as opening it in the sink's own UI does. A message with no
  `X-Correlation-Id`, or one the backend would not adopt, has no Trace link.
- The mail count reads only the newest `MailSinkClient.Limit` messages, so a run's mail is counted
  only while it is among them. The deliver, cancel, declined, out-of-stock, carrier and timeout scripts
  count mail; the in-transit, already-shipped and image scripts do not (§5.13).

## Tests

    dotnet test                                # host, xunit
    (cd src/Admin.Web && npm test)             # SPA, Vitest
    (cd src/Admin.Web && npx playwright install chromium)   # once per workstation
    dotnet build && (cd src/Admin.Web && npm run build && npm run e2e)   # Playwright against the fake host

`.github/workflows/ci.yml` runs the same three on every push and pull request.
