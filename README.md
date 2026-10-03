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

## What it does today

Phases 0 to 6 of the spec: the Stack screen (Compose services, reachability,
up, down, down with a typed `down -v` confirmation, Reset behind the same
confirmation, which runs `down -v`, up and the readiness wait as one job, live
output; the reference
client's `npm start` with start, stop and its output; a table of each
service's golden signals from Prometheus, one column per panel of the
backend golden-signals dashboard: request rate, 5xx share, p99 latency, the
422 and 401 refusals, and §13.7's command and query p95), the Logs screen
(follow, service filter, text filter, correlation-id highlight) and the API
screen (Catalog's, Ordering's, Inventory's and Payments' OpenAPI operations
through the gateway, the BFF quote, every host's readiness and the payment
simulator's request log; send as anonymous, a realm user or a
custom username and password, with a correlation id; status, timing, headers
and body as the platform returned them; a history of this visit's calls).
The Broker screen lists queues (error queues marked, each named with what
consumes it), exchanges and
permissions through `rabbitmqctl` in the `rabbitmq` container, and the API
screen shows whether `ordering-catalog-events` has drained after a publish.
The Trace screen joins a correlation id's Loki lines and Tempo spans into one
timeline, each of those rows linking into Grafana Explore, closing with the
projection queue's snapshot; the API screen's "Trace this call" opens it on the
response's own correlation id. The Scenario screen runs `run-locally.md`'s
calls end to end as a realm user — publish, wait for the projection to drain,
quote, order, cancel — stopping at the first step that does not succeed, each
sent step linking to its own trace.

The API screen also says what each service's OpenAPI document changed since
the console last kept it: operations, response codes and schema fields added
or removed, and since when. The first document seen for a service is kept as
its baseline under `Admin:DataDir`, and "Accept as baseline" keeps the current
one. It reads only the documents the screen already fetches. The Trace screen
keeps the last few timelines per correlation id for the browser session, and a
reload says what arrived or left since the previous fetch, or that nothing
changed between the two.

## Known limits

- The host keeps at most one `logs -f` job, so a Follow in a second browser
  tab ends the first tab's. A follow whose answer never reached the page,
  or whose stop failed as the Logs screen was left, runs until the next
  Follow.
- The console runs `npm start` but never `npm ci`: with no `node_modules` in
  the frontend clone, Start is refused and the screen says so.
- The console does not track a client started by hand: Start still runs a
  second `npm start`, which competes for port 5173, and its output shows
  what `ng serve` did.
- A client the console started may be in use by the client's own Playwright
  run, which reuses a dev server it finds outside CI; Stop ends it under that
  run. Nothing detects the run, because the host cannot observe one.
- Stop gives up waiting after 10 s and says so in the output; the process
  may still be running.
- The proxy sends only to the gateway, Catalog, Ordering, BFF, Inventory,
  Payments and payment simulator URLs in
  `Admin:*Url`, and refuses a correlation id the platform would replace
  (anything but 1 to 128 ASCII letters, digits, `-` and `_`).
- Operation examples are the bodies `run-locally.md` sends where there is one,
  otherwise placeholders built from the schema; the documents carry none.
- API history is kept only while the screen is open.
- OpenAPI baselines live under `Admin:DataDir`, which defaults to a directory
  inside `/artifacts/`; deleting it starts every service's baseline again.
- Trace history lasts as long as the browser tab, and a refetch over a
  different window is not compared.
- The identity picker offers demo/demo and browser/browser; configuring any user
  replaces both: `--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`.
- A timeline ends at the outbox. The outbox row carries no trace context, so
  the dispatcher's publish, the consume on the other service and the projection
  write run in a trace the correlation id cannot reach; the Trace screen ends
  with the projection queue's depth and says why it stops there. Raised for
  `blueprint-backend`, which owns the fix.
- The golden-signal strip sums the 422 panel's routes into one rate per
  service; Grafana's panel shows the split. Its command and query p95 are
  quantiles over the backend's `request.duration` histogram, so they are only
  as fine as that histogram's bucket boundaries.

## Tests

    dotnet test                                # host, xunit
    (cd src/Admin.Web && npm test)             # SPA, Vitest
    (cd src/Admin.Web && npx playwright install chromium)   # once per workstation
    dotnet build && (cd src/Admin.Web && npm run build && npm run e2e)   # Playwright against the fake host

`.github/workflows/ci.yml` runs the same three on every push and pull request.
