# blueprint-admin

A local admin console for [`dotnet-ddd-blueprint`](https://github.com/alexander-shamray/dotnet-ddd-blueprint):
one page that runs, stops, observes and exercises the platform, doing verbatim
what the workspace's `run-locally.md` says a developer does by hand.

It runs on the developer's workstation, binds loopback only, and holds the
realm passwords, so it is never deployed and never shared. The design is
`docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`.

## Prerequisites

Docker Desktop, the .NET SDK pinned in `global.json`, the Node version in
`.nvmrc`, and the two sibling clones beside this one:

    ../blueprint-backend    alexander-shamray/dotnet-ddd-blueprint
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

Phases 0 to 4 of the spec: the Stack screen (Compose services, reachability,
up, down, down with a typed `down -v` confirmation, live output; the reference
client's `npm start` with start, stop and its output), the Logs screen
(follow, service filter, text filter, correlation-id highlight) and the API
screen (Catalog's and Ordering's OpenAPI operations through the gateway, the
BFF quote and every host's readiness; send as anonymous, a realm user or a
custom username and password, with a correlation id; status, timing, headers
and body as the platform returned them; a history of this visit's calls).
The Broker screen lists queues (error queues marked), exchanges and
permissions through `rabbitmqctl` in the `rabbitmq` container, and the API
screen shows whether `ordering-catalog-events` has drained after a publish.
The event trace is the spec's next phase.

## Known limits

- The host keeps at most one `logs -f` job: Follow stops the previous one
  before starting the next. Stop on the Logs screen only closes the browser's
  stream; the job keeps running until the next Follow or until the host exits.
- The console runs `npm start` but never `npm ci`: with no `node_modules` in
  the frontend clone, Start is refused and the screen says so.
- The console does not track a client started by hand: Start still runs a
  second `npm start`, which competes for port 5173, and its output shows
  what `ng serve` did.
- Stop gives up waiting after 10 s and says so in the output; the process
  may still be running.
- The proxy sends only to the gateway, Catalog, Ordering and BFF URLs in
  `Admin:*Url`, and refuses a correlation id the platform would replace
  (anything but 1 to 128 ASCII letters, digits, `-` and `_`).
- Operation examples are the bodies `run-locally.md` sends where there is one,
  otherwise placeholders built from the schema; the documents carry none.
- API history is kept only while the screen is open.
- The identity picker offers demo/demo and browser/browser; configuring any user
  replaces both: `--Admin:Users:0:Username=ops --Admin:Users:0:Password=…`.
- No event trace yet.
- Broker reads are `docker compose exec` jobs, so they appear in
  `GET /api/jobs`; auto-refresh re-reads queues only.

## Tests

    dotnet test                                # host, xunit
    (cd src/Admin.Web && npm test)             # SPA, Vitest
    (cd src/Admin.Web && npx playwright install chromium)   # once per workstation
    dotnet build && (cd src/Admin.Web && npm run build && npm run e2e)   # Playwright against the fake host

`.github/workflows/ci.yml` runs the same three on every push and pull request.
