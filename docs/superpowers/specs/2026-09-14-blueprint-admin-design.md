# Blueprint admin — design

**A local admin console for `blueprint-backend`: one page that runs, stops,
observes and exercises the platform, doing verbatim what `run-locally.md` says
a developer does by hand.**

| | |
|---|---|
| **Status** | Approved design, 2026-09-14. Implementation plan follows. |
| **Backend** | `alexander-shamray/blueprint-backend`, checked out beside this repository at `../blueprint-backend` |
| **Frontend** | `alexander-shamray/blueprint-frontend`, checked out beside this repository at `../blueprint-frontend` |
| **Toolkit** | .NET 10 / C# 14 minimal API host; Angular 22 standalone SPA; xunit, Vitest, Playwright |
| **Runs on** | The developer's workstation, loopback only. Never deployed, never multi-user |
| **Prerequisites** | Docker Desktop, Node 22.22 or newer, .NET 10 SDK, both sibling clones |

## 1. Purpose

`run-locally.md` (kept at the workspace root beside the three clones) lists
what a developer does to bring the platform up and prove it works: start the
Compose stack, start the client, mint a token, call the APIs in a particular
order, inspect the broker, join logs and traces by correlation id, tear down.
Every step is a shell command or a browser tab, and the order matters because
Ordering prices from a projection that the broker fills asynchronously.

This repository is that document as a web console. Its one job is to make each
of those operations a click, and to show the developer what the platform did
in response, across services. It does not replace Grafana, Keycloak's console
or the reference client; it links to them where they are better.

Three properties follow, and each design choice below is argued against them:

1. **The console runs the platform's own commands.** Starting the backend is
   `docker compose -f deploy/compose/docker-compose.yml up -d --wait`, in the
   backend clone, with its output shown. Nothing is reimplemented that the
   Compose files or `package.json` already own.
2. **Nothing is invented that the backend owns.** Routes come from the
   gateway's configuration and the services' OpenAPI documents; permission
   names, status codes and error bodies pass through untouched. Where the
   console must hard-code a platform fact it cites the owner by file and
   symbol.
3. **Every operation leaves a trail that can be followed.** A call made from
   the console carries a correlation id, and the console can show what that id
   produced in logs, traces and the broker.

## 2. What the console operates

Owners, for the citation rule in §1: the Compose model is
`blueprint-backend/deploy/compose/` (index `docker-compose.yml`, baseline
`infrastructure.yml`, one file per unit under `services/`); the gateway's
routes are `Gateway.Api/appsettings.json`; health endpoints are mapped by
`Common.Web.HealthCheckExtensions`; the correlation header is
`Common.Web.CorrelationIdExtensions.Header`; the realm and its users are
`deploy/compose/keycloak/`; the client's port is `blueprint-frontend/angular.json`.

### 2.1 Processes

| Operation | Command, in which clone |
|---|---|
| Start backend | `docker compose -f deploy/compose/docker-compose.yml up -d --wait`, backend |
| Stop backend | `docker compose -f deploy/compose/docker-compose.yml down`, backend |
| Stop and wipe | `... down -v`, backend. Destroys databases and broker state |
| Backend status | `... ps --format json`, backend |
| Backend logs | `... logs -f [service...]`, backend |
| Broker inspection | `... exec rabbitmq rabbitmqctl list_queues name messages --formatter json` and the `list_exchanges name type`, `list_permissions` forms, backend |
| Start frontend | `npm start` (`ng serve`, port 5173), frontend |
| Stop frontend | kill the `npm start` process tree |

`npm ci` is not a console operation. It is a one-time prerequisite and the
console reports its absence (no `node_modules`) rather than running it.

### 2.2 HTTP surfaces on the workstation

| Piece | URL | Used for |
|---|---|---|
| Gateway | `http://localhost:5000` | every API call the console proxies; `/health/ready` |
| Catalog API | `http://localhost:5102` | `/openapi/v1.json` (token required); `/health/ready` |
| Ordering API | `http://localhost:5101` | `/openapi/v1.json` (token required); `/health/ready` |
| Web BFF | `http://localhost:5200` | `/health/ready` |
| Keycloak | `http://localhost:8080` | password grant on realm `commerce`, client `web-app` |
| Grafana | `http://localhost:3000` | `GET /api/datasources`, `POST /api/ds/query` for Tempo, Loki and Prometheus |
| Reference client | `http://localhost:5173` | a link, and the port the gateway's CORS admits |

The Grafana container is the `grafana/otel-lgtm` bundle, which enables
anonymous admin access and provisions Tempo, Loki and Prometheus datasources.
The console reaches all three through Grafana's query API, so no port is added
to the backend's Compose files. The RabbitMQ management UI on 15672 has no
login, which is why broker inspection goes through `rabbitmqctl`.

### 2.3 Routes the gateway exposes

| Method and path | Upstream | Edge policy |
|---|---|---|
| `GET /api/v1/catalog/{**}` | Catalog | anonymous, rate limit `anonymous` |
| `POST /api/v1/catalog/{**}` | Catalog | authenticated |
| `* /api/v1/orders/{**}` | Ordering | authenticated |
| `* /api/v1/inventory/{**}` | Inventory (absent; 502) | `inventory:admin` |
| `* /bff/{**}` | Web BFF | authenticated |

The gateway strips `/api` or `/bff` before forwarding. Catalog and Ordering
publish OpenAPI; the gateway and BFF do not. The console's operation tree is
therefore the two OpenAPI documents rebased onto the gateway prefix, plus a
curated list for the BFF's `POST /bff/v1/checkout/quote` and every host's
`/health/ready`, plus a free-form request form for anything else.

### 2.4 Identities

| User | Password | Holds |
|---|---|---|
| `demo` | `demo` | `catalog:write`, `orders:write`, `orders:cancel` |
| `browser` | `browser` | nothing |
| anonymous | — | the catalog GET route only |

Access tokens live 300 seconds (`accessTokenLifespan` in the realm export).
The console re-mints on expiry rather than refreshing.

## 3. Approach

**Chosen: a shell-out host.** A .NET process on the workstation runs the
commands in §2.1 as child processes with the two clones as working
directories, and speaks plain HTTP to the surfaces in §2.2. A browser SPA in
front of it is the console. This is the approach because it honours §1.1 by
construction: the console cannot drift from `run-locally.md` when it runs the
same lines.

**Rejected: the Docker Engine API.** Structured container events and log
streams without a CLI, but Compose `up`/`down` and `npm start` still need a
shell, so it is two mechanisms for one job, with Windows named-pipe handling
on top.

**Rejected: a Compose overlay that containerises the console.** It cannot run
the client's `npm start` on the host, it couples this repository to the
backend's Compose tree, and mounting the Docker socket into a container that
holds admin credentials is the wrong default for a tool that exists to be
safe to leave running.

**Why a host at all.** A browser page cannot spawn processes, and the
gateway's CORS admits only origin 5173, so even the API calls need a server
in the middle. Once one exists, everything routes through it and the SPA has
a single origin to talk to.

## 4. Repository shape

```
blueprint-admin/
  README.md                        what this is, how to run it, the ports
  CLAUDE.md                        primer: what the repo is, where things are, how to act here
  run-locally.md                   NOT here; the workspace copy is the source and §2 cites it
  global.json                      .NET SDK pin, same version as the backend
  Directory.Build.props            analyser policy and warnings-as-errors, copied from the backend
  Directory.Packages.props         central package versions
  .editorconfig, .gitattributes    CRLF for .cs, LF for everything else, as the siblings do
  BlueprintAdmin.slnx
  src/Admin.Host/                  the .NET 10 minimal API host (§5)
  src/Admin.Web/                   the Angular 22 SPA (§6); its build lands in Admin.Host/wwwroot
  tests/Admin.Host.Tests/          xunit
  .github/workflows/ci.yml         dotnet build and test; npm ci, lint, test, build
  docs/superpowers/specs/          this document
  docs/admin-architecture.md       written once the code exists; owns what moves
```

Ports: the host listens on `http://127.0.0.1:5300`. In development the SPA's
dev server listens on `http://127.0.0.1:5301` and proxies `/api` to 5300. A
published build serves the SPA from the host's `wwwroot`, so the console is
one URL. Neither port is used by the platform.

The host is a single project. Its modules (§5) are namespaces with one
public service each and an interface where a test needs a fake; they are not
separate assemblies until one of them has a second consumer.

## 5. The host

### 5.1 Config

`AdminOptions`, bound from `appsettings.json` and environment variables with
the `BLUEPRINT_` prefix:

| Key | Default | Meaning |
|---|---|---|
| `BackendDir` | `../blueprint-backend` | the backend clone; resolved against the repo root |
| `FrontendDir` | `../blueprint-frontend` | the client clone |
| `ComposeFile` | `deploy/compose/docker-compose.yml` | relative to `BackendDir` |
| `GatewayUrl`, `CatalogUrl`, `OrderingUrl`, `BffUrl`, `KeycloakUrl`, `GrafanaUrl`, `ClientUrl` | the §2.2 values | the workstation surfaces |
| `Realm`, `ClientId` | `commerce`, `web-app` | the password-grant target |
| `Users` | `demo`/`demo`, `browser`/`browser` | the realm user table shown in the identity picker |
| `FakePlatform` | `false` | swap every process and HTTP dependency for recorded fakes (§9) |

Startup validates that both directories exist and contain what they should
(the Compose file; `package.json`) and refuses to start otherwise, naming the
key to fix.

### 5.2 ProcessRunner and Jobs

`ProcessRunner.Start(command, args, cwd)` launches a child process, reads
stdout and stderr line by line into a `Job`, and returns it. A `Job` has an
id, the command line, `Running`/`Exited` state, the exit code, a bounded ring
buffer of the last 2000 lines with a monotonic sequence number, and a
broadcast channel that live subscribers read from. `Stop(jobId)` kills the
process tree: on Windows each started process is assigned to its own Job
Object (kill-on-close), and Stop terminates the job, reaching orphaned
descendants that a parent-link walk would miss; elsewhere, and as the Windows
fallback when assignment fails, it is `Process.Kill(entireProcessTree: true)`.
Stop waits at most 10 s after the kill, then marks the job exited -1 and logs,
so a stop never hangs. Bare command names resolve through PATH/PATHEXT on
Windows (`npm` is `npm.cmd`). The operating system is known only inside
`src/Admin.Host/Jobs/` (`ProcessRunner.cs`, `WindowsJobObject.cs`,
`ExecutableResolver.cs`), which is the reason `ng serve` can be stopped at all
on Windows.

Jobs are kept in memory for the life of the host, `Exited` jobs are trimmed
past the last 50. One-shot commands (`up`, `down`, `ps`, `exec`) are jobs too,
so that their output is visible the same way.

### 5.3 Compose

`ComposeService` builds every command from `AdminOptions` and runs it through
`ProcessRunner` in `BackendDir`:

- `Up()`: `up -d --wait`. Returns the job.
- `Down(wipeVolumes)`: `down` or `down -v`. The endpoint requires
  `confirm: "down -v"` in the body when `wipeVolumes` is true.
- `Ps()`: `ps --format json`, parsed into `ServiceStatus { Service, State,
  Health, PublishedPorts }`. A `docker` that is not on `PATH` or a daemon that
  is not running yields an `Unreachable` result carrying the last stderr line,
  not an exception.
- `FollowLogs(services)`: `logs -f --tail 200 [services]`, a long-running job.
- `Exec(service, args)`: `exec -T service args`, a one-shot job whose stdout
  the caller parses.

### 5.4 FrontendSupervisor

Holds at most one `npm start` job in `FrontendDir`. `Start()` refuses if one
is running or if `node_modules` is absent. `Stop()` kills the tree. `Status()`
reports the job and whether port 5173 answers.

### 5.5 Broker

`BrokerService` runs the three `rabbitmqctl` forms from §2.1 with
`--formatter json` through `ComposeService.Exec` and returns queues,
exchanges and permissions. It marks queues whose name ends in `_error`, and
answers `IsDrained(queues, "ordering-catalog-events")` — declared and holding
no messages — which `GET /broker/queues` carries as `projection` for the
Broker screen and the API screen's "wait for the projection" affordance. A
broker that does not answer, or output that is not the formatter's JSON
array, is `reachable: false` with the error, and each `rabbitmqctl` is
bounded to 30 s like `ps`.

### 5.6 Identity

`TokenService.Get(identity)` performs the password grant against
`{KeycloakUrl}/realms/{Realm}/protocol/openid-connect/token` for a named
realm user or a custom username and password, caches the token until 30
seconds before expiry, and returns the access token with its decoded claims
for display. `Anonymous` is an identity that yields no token. Keycloak's error
body on a failed grant is returned as-is. The wire identity is
`{ username, password }`: no username is anonymous, a username alone is
looked up in `Users`, both is a custom identity; `GET /identity/users`
returns usernames only.

### 5.7 ApiCatalog and RequestProxy

`ApiCatalog.Load()` fetches `/openapi/v1.json` from Catalog and Ordering with
a `demo` token, rewrites each path onto the gateway (`/v1/catalog/...` becomes
`/api/v1/catalog/...`), and merges the curated operations from §2.3. Each
operation carries method, gateway path, path parameters, an example body (the
one `run-locally.md` sends for that operation where there is one, otherwise
placeholders built from the request schema, since the documents carry no
examples), and the edge policy from a cited copy of the gateway's route table
(`Api/GatewayRoutes.cs`). The catalog is cached and reloaded on demand; when a
service is down its operations are listed as unavailable rather than dropped.

`RequestProxy.Send(request)` takes method, an absolute URL restricted to the
configured surfaces (the gateway, Catalog, Ordering and BFF origins), headers,
body, an identity and an optional correlation id. It attaches the token, sets
`X-Correlation-Id` (generated if absent; a supplied id that breaks those rules
is refused, because the backend would silently replace it; the header's rules
are letters, digits, `-`, `_`, up to 128 characters), sends,
and returns status, headers, body, elapsed time and the correlation id used.
Status and body are never rewritten: a 401, a 403 and the three different 409s
must read exactly as the reference client sees them.

### 5.8 Telemetry

`GrafanaClient` resolves the Tempo, Loki and Prometheus datasource uids from
`GET /api/datasources` at first use and reads through Grafana's **datasource
proxy**, not `POST /api/ds/query`. Measured 2026-09-16: `/api/ds/query` answers
Grafana's internal data frames, while the proxy returns Loki's and Tempo's own
documented shapes, and for a trace-by-id fetch there is no frame-free
alternative at all. The three calls are:

- Loki: `GET …/proxy/uid/{loki}/loki/api/v1/query_range` with
  `{service_name=~".+"} | CorrelationId = "<id>"` over a window (`start`/`end`
  in nanoseconds), returning each line with its service, level, message and
  `trace_id`. There is **no `| json` stage**: the backend exports logs over
  OTLP only, so the line body is the formatted message and `CorrelationId` is
  structured metadata. Measured 2026-09-16, a `| json` stage stamps
  `__error__: "JSONParserErr"` on every record.
- Tempo: `GET …/proxy/uid/{tempo}/api/traces/{traceIdHex}`. Ids in the answer
  are base64 and are decoded to hex before they are joined against Loki's
  `trace_id`.
- Prometheus: the golden-signal queries the backend's
  `deploy/observability/dashboards/golden-signals.json` uses, for the Stack
  screen's health strip.

How the correlation id reaches telemetry is a backend fact, and it decides
the join: `Common.Web.CorrelationIdExtensions` puts the id in a logging scope
named `CorrelationId` on every record of the request, and when the caller
sent none it uses the current trace id as the correlation id. No span carries
the id as an attribute. So the console finds spans through logs: the Loki
lines for a correlation id carry OpenTelemetry's `trace_id`, and those trace
ids are what Tempo is asked for — but that reaches the request's **own** trace
only, because the outbox severs the context (§5.9). The `service_name` label is the
`service.name` resource attribute that `Common.Web`'s telemetry registration
sets from each host's service name.

### 5.9 EventTrace

`EventTraceService.Build(correlationId, window)` runs in three steps:

1. Loki: every line scoped to the correlation id, and the set of distinct
   `trace_id` values on those lines.
2. Tempo: each of those traces. **The broker hop is not inside them.** Measured
   2026-09-16 against the backend's source: `OutboxMessage` has no
   `traceparent` column, and `OutboxDispatcher` is a `BackgroundService` that
   publishes without starting an `Activity`, so `Activity.Current` is null at
   publish time and MassTransit's send span is the root of a **new** trace.
   (The message-level `Guid CorrelationId` is not the HTTP correlation id
   either; Catalog sets it to the product id.) The join therefore reaches the
   request's own spans — inbound HTTP, handlers, the database write that
   stages the outbox row — and stops at that row.
3. Broker: the current queue snapshot from §5.5. Because step 2 stops at the
   outbox, this is not a nicety but the **only bridge across the hop**, and it
   is rendered as the timeline's terminal event: the projection queue, its
   depth, and a sentence saying that the publish runs in a trace this
   correlation id cannot reach. A message still waiting in
   `ordering-catalog-events` or parked in an `_error` queue shows there rather
   than as silence.

Restoring the trace across the hop is a `blueprint-backend` change — persist
`traceparent` on the outbox row when it is staged and restore it into an
`Activity` at dispatch — and is raised there, not worked around here.

The result is one ordered timeline of `TraceEvent { At, Source, Service,
Kind, Summary, TraceId, Link }`, where `Kind` is one of `HttpIn`, `Log`,
`Span`, `Outbox`, `Publish`, `Consume`, `Queued`, `Error`. `Outbox`,
`Publish` and `Consume` are recognised from span names and attributes that
MassTransit's instrumentation and `Common.Infrastructure`'s
`OutboxDispatcher` own; the recogniser is a table of patterns, so a renamed
span is a one-line change and an unrecognised span is still shown as `Span`.
Each row deep-links to Grafana Explore with the datasource and query
pre-filled.

### 5.10 Endpoints

All under `/api`, JSON, loopback only.

| Method and path | Does |
|---|---|
| `GET /stack` | `ServiceStatus[]`, the frontend job summary, and reachability of gateway, Keycloak, Grafana and the client |
| `POST /stack/backend/up` | Compose up; returns the job |
| `POST /stack/backend/down` | body `{ wipeVolumes, confirm }`; returns the job |
| `POST /stack/frontend/start`, `POST /stack/frontend/stop` | supervisor |
| `GET /jobs`, `GET /jobs/{id}` | summaries; one job with its last lines |
| `GET /jobs/{id}/stream` | Server-Sent Events, one event per line, `id:` is the sequence number so `Last-Event-ID` resumes from the ring buffer |
| `POST /logs/follow` | body `{ services }`; starts a follow job and returns it |
| `GET /broker/queues`, `/broker/exchanges`, `/broker/permissions` | §5.5; `queues` also carries `projection`, the drain state of `ordering-catalog-events` |
| `GET /identity/users`, `POST /identity/token` | §5.6 |
| `GET /catalog/operations`, `POST /catalog/reload` | §5.7 |
| `POST /proxy` | §5.7 |
| `GET /trace/{correlationId}?window=15m` | §5.9. `window` is `90s`/`15m`/`2h` bounded to `[1m, 24h]`, default `15m`; a window outside it, and an id outside the backend's adoptable alphabet (the id reaches a LogQL string, so this is the injection boundary), are 400 problem details. |
| `GET /telemetry/health` | the golden-signal strip |

Streams use Server-Sent Events rather than WebSockets because every stream is
server-to-client and line-oriented, and SSE reconnects for free.

## 6. The SPA

Angular 22 standalone components, signals, the router, no Ionic. The layout
mirrors the reference client's so a reader of one repo can read the other:
`src/app/core/` (config, the host client, the SSE client, identity state),
`src/app/features/` one directory per screen, `src/app/shared/` for the
output pane, status pill and JSON viewer.

| Screen | Shows | Does |
|---|---|---|
| **Stack** | one row per Compose service with state, health and port; the frontend job; a reachability strip; quick links to the client, Grafana, Keycloak | Up, Down, Down and wipe (typed confirmation), Start and Stop frontend; opens the job's output pane |
| **Logs** | a follow stream with service filter, text search and correlation-id highlight | start, stop, clear |
| **Broker** | queues with depth, `_error` queues in red, exchanges, permissions; a drained indicator for `ordering-catalog-events` | refresh, auto-refresh |
| **API** | operation tree on the left; request editor (path params, headers, body pre-filled from the schema example, `commandId` generated per send) and identity picker; response pane with status, timing, headers, body; a history list | Send; "Trace this call" opens the Trace screen with the response's correlation id |
| **Trace** | the §5.9 timeline for a correlation id, grouped by service, with Grafana deep links | enter an id or arrive from the API screen |
| **Scenario** | `run-locally.md`'s calls as five steps — publish, wait for the drain, quote, order, cancel — each with its status and body, and each HTTP step with its own correlation id | Run as a realm user; each step that sent links to its trace; the first step that does not succeed ends the run |

The API screen keeps one behaviour from `run-locally.md` explicit: after a
successful publish it shows the drained indicator and says why an order for
that product should wait.

## 7. Data flow

```
browser (SPA, origin 5300)
   │  JSON over /api, SSE over /api/jobs/{id}/stream
   ▼
Admin.Host (127.0.0.1:5300)
   ├─ ProcessRunner ──▶ docker compose … (cwd backend)  ──▶ Docker Desktop
   │                ──▶ npm start          (cwd frontend) ──▶ ng serve :5173
   ├─ HttpClient ────▶ gateway :5000 ─▶ catalog, ordering, bff
   │             ────▶ keycloak :8080
   │             ────▶ grafana :3000 ─▶ tempo, loki, prometheus
   └─ ComposeService.Exec ─▶ rabbitmqctl inside the rabbitmq container
```

The SPA never calls the platform directly; §3 says why.

## 8. Security posture

The console holds realm passwords and can wipe volumes, so it is a loopback
tool and nothing else. The host binds `127.0.0.1` only, refuses to start on
any other address, and sets no CORS headers, so a page on another origin
cannot drive it. There is no login of its own. `down -v` requires the typed
confirmation. The proxy accepts only URLs on the configured surfaces, so it
cannot be used as an open relay from a tab the developer left open.

## 9. Error handling

- A platform that is down is a state, not a failure: `ServiceStatus` shows
  `Unreachable`, the operation tree shows a service as unavailable, and the
  screens keep working for what is up.
- A command that fails surfaces its exit code and the last lines of its
  output on the job, where the Stack screen shows them.
- A failed password grant returns Keycloak's body and status.
- The proxy returns upstream status, headers and body untouched, and adds its
  own error only when the request never reached the upstream (connection
  refused, timeout), with a distinct shape the SPA renders differently:
  `outcome` is `responded`, `unreached` (no answer, including Keycloak down)
  or `tokenRejected` (Keycloak refused the identity; nothing was sent).
- The host's own errors are RFC 9457 problem details, matching the backend's
  `Common.Web.ResultExtensions` convention.

## 10. Testing

**Host** (`tests/Admin.Host.Tests`, xunit):
- `FakeProcessRunner` replays recorded `docker compose ps --format json` and
  `rabbitmqctl --formatter json` output from fixture files, and `npm start`
  output from an in-code recording in `src/Admin.Host/Fakes/FakePlatformScripts.cs`;
  `ComposeService`, `BrokerService` and `FrontendSupervisor` are tested
  against it, including the failure shapes (`docker` absent, daemon down,
  `node_modules` absent).
- Fake `HttpMessageHandler`s for Keycloak, the two OpenAPI documents, Grafana
  and the gateway; `TokenService`, `ApiCatalog`, `RequestProxy`,
  `GrafanaClient` and `EventTraceService` are tested against them, including
  status passthrough and the correlation-id rules.
- Endpoint tests with `WebApplicationFactory` and the fakes, including SSE
  resume with `Last-Event-ID` and the `down -v` confirmation.
- One integration class, skipped unless Docker answers, that runs `ps`
  against the real CLI. No test starts the platform.

**SPA** (Vitest, Playwright):
- Vitest for the host client, the SSE client, the identity state and each
  screen's component against a mocked host client.
- Playwright smoke against the host started with `FakePlatform=true`, which
  swaps in the fakes above: every screen renders, Up produces a job with
  output, a proxied call shows a response, a trace renders a timeline. CI
  runs this without Docker.

**FakePlatform** is a first-class mode, not a test hook: it is how the SPA is
developed without the platform running, and how a reader of this repo sees
the console in a minute.

## 11. CI and conventions

`ci.yml` on push and pull request: `dotnet build` and `dotnet test`; `npm ci`,
`ng lint`, `vitest run`, `ng build`, then the Playwright smoke against the
FakePlatform host. Matrix on `ubuntu-latest` and `windows-latest`, because
process-tree handling is the one platform-specific piece and Windows is the
workstation.

Conventions copied from the siblings, not re-decided: the SDK pin and analyser
policy (IDE0055 as a build error, no column alignment), `.gitattributes` with
CRLF for `.cs` and LF elsewhere, the prose style of `docs/style-guide.md`,
`CLAUDE.md` as a primer that cites owners rather than restating facts.
Harness commands, hooks, review loops and the change-locality contract are
not adopted at the start; they come when there is a PR flow to govern.

## 12. Phases

| Phase | Delivers |
|---|---|
| 0 Bootstrap | repo, solution, host skeleton with Config and loopback binding, Angular app with routing and the shell, CI, README, CLAUDE.md, `.gitattributes` |
| 1 Stack and Logs | ProcessRunner, Jobs, SSE, ComposeService, Stack and Logs screens, FakePlatform with the process fakes |
| 2 Frontend | FrontendSupervisor and its controls on the Stack screen |
| 3 API | TokenService, ApiCatalog, RequestProxy, the API screen with identity picker and history |
| 4 Broker | BrokerService and screen; the drained indicator on the API screen |
| 5 Trace | GrafanaClient, EventTraceService, the Trace screen and "Trace this call" |
| 6 Scenario | the Scenario screen: a scripted publish → wait for drain → quote → order → cancel run with a trace per HTTP step (below) |

**Phase 6 is the SPA alone.** Every step is a call the API screen can
already make — `POST /proxy` with the catalog's own operation and its
`run-locally.md` example body, and `GET /broker/queues` for the drain — so no
endpoint is added and FakePlatform answers it as it stands. Each step carries
the id the previous one produced (the product into the quote and the order,
the order into the cancel), and each HTTP step its own correlation id, so each
links to its own trace; the drain asks the broker and has none. The drain is
the API screen's watch, with its interval and cap, and a projection that is
not drained ends the run rather than ordering against a price that may not be
there. Drained is `run-locally.md`'s wait and no more: the outbox publishes
after the request, so an empty queue can precede the event, and no surface
says one product has been projected (§5.9 is the same hop). A signal that does
is a `blueprint-backend` change.

Each phase is one or more PRs; each ends with the CI green on both runners
and the Playwright smoke covering the new screen.

## 13. Non-goals

- Deployment, containers, multi-user, any listener that is not loopback.
- Keycloak realm administration (users, clients, roles). The Keycloak console
  is linked for that.
- Replacing Grafana. The Trace screen joins; Explore is one click away for
  anything deeper.
- Editing the sibling repositories. If a backend fact needed for §5.8 or §5.9
  is not observable, the change is raised there, not worked around here.
- Mobile, Ionic, Capacitor. This is a desktop tool.
