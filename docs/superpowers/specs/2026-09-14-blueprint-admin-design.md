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
| Stop and start the BFF | `... stop web-bff` and `... start web-bff`, backend; only around a projection rebuild |
| Repair the BFF projection | `dotnet run --project tools/bff-replay`, backend, with the six connections `run-locally.md` sets as `$env:` lines |
| Rebuild the BFF projection | `dotnet run --project tools/bff-replay -- --reset`, backend, between the stop and the start above. Deletes the projection |
| Give the dead-letter operator a password, and take it away | `... exec rabbitmq rabbitmqctl change_password dead-letter-operator <password>` and `... clear_password dead-letter-operator`, backend; around every dead-letter run, with a password minted for that run |
| Read a parked queue | `py -3.12 tools/dead-letters/dead_letters.py inspect <queue> --limit 20 --json`, backend, with the password in `DEAD_LETTERS_PASSWORD` |
| Replay or discard a dead letter | `py -3.12 tools/dead-letters/dead_letters.py replay\|discard <queue> --message-id <id>`, backend; a dry run, and with `--execute --record <file> --audit-log <file>` the real run, which takes each message off the queue |
| Start frontend | `npm start` (`ng serve`, port 5173), frontend |
| Stop frontend | kill the `npm start` process tree |
| Build the client for the emulator | `npm run emulator:android` (`ng build --configuration android && cap sync android`), frontend; `run-locally.md` step 4 |
| Read the device lane | three reads from `run-locally.md` step 4, each run as written through PowerShell: `adb.exe devices`, a `curl.exe` CORS preflight to the gateway, and the running realm's `mobile-app` redirects through Keycloak's admin API. None changes anything |
| Stop and restore Inventory for a Scenario | `... stop inventory-api`, and `... up -d --no-deps inventory-api` to restore it, backend; only for a script whose cause `run-locally.md` gives as stopping it (§5.3, *Scenario service control*) |
| Shorten the saga's waits for a Scenario | `docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.short-timeouts.yml up -d --no-deps ordering-api`, backend (ADR-128), and the same without the second file to restore it; only for a script whose cause is one of those waits (§5.3) |

`npm ci` is not a console operation. It is a one-time prerequisite and the
console reports its absence (no `node_modules`) rather than running it.

### 2.2 HTTP surfaces on the workstation

| Piece | URL | Used for |
|---|---|---|
| Gateway | `http://localhost:5000` | every API call the console proxies; `/health/ready` |
| Catalog API | `http://localhost:5102` | `/openapi/v1.json` (token required); `/health/ready` |
| Ordering API | `http://localhost:5101` | `/openapi/v1.json` (token required); `/health/ready` |
| Web BFF | `http://localhost:5200` | `/health/ready` |
| Inventory API | `http://localhost:5103` | `/openapi/v1.json` (token required); `/health/ready` |
| Payments API | `http://localhost:5104` | `/openapi/v1.json` (token required); `/health/ready` |
| Privacy API | `http://localhost:5105` | `/openapi/v1.json` (token required); `/health/ready`; its erasure requests are reached through the gateway (§2.3) |
| Payment simulator | `http://localhost:5190` | `/__admin/requests`, what Payments asked the provider; a decline is caused by the order's amount |
| Carrier simulator | `http://localhost:5191` | `/__admin/requests`, what Shipping asked the carrier; Shipping is a worker with no API, so this is its only by-hand view |
| Mail sink | `http://localhost:8025` | Mailpit's API, `GET` alone: the message list, one message's headers and the message itself (§5.12); not a surface of the proxy (§8) |
| Object store | `http://127.0.0.1:8333` | `GET /status`, the readiness the Compose healthcheck asks; the one pre-signed `PUT` of a product image, which the host sends (§5.7) and the proxy cannot (ADR-106) |
| Image edge | `http://127.0.0.1:9000` | `GET /healthz`; a published image read back at `/products/{seller}/{file}`, anonymous, `GET` and `HEAD` only, served with `nosniff` (§10.8) |
| Keycloak | `http://localhost:8080` | password grant on realm `commerce`, client `web-app` |
| Grafana | `http://localhost:3000` | `GET /api/datasources`, then the datasource proxy for Tempo, Loki and Prometheus (§5.8) |
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
| `* /api/v1/inventory/{**}` | Inventory | `inventory:admin` |
| `* /api/v1/payments/{**}` | Payments | `payments:admin` |
| `* /api/v1/privacy/{**}` | Privacy | `privacy:erase` |
| `* /bff/{**}` | Web BFF | authenticated |

The gateway strips `/api` or `/bff` before forwarding. Catalog, Ordering,
Inventory, Payments and Privacy publish OpenAPI; the gateway and BFF do not. The
console's operation tree is therefore the five OpenAPI documents rebased onto
the gateway prefix, plus a curated list for the BFF's
`POST /bff/v1/checkout/quote` and its buyer order reads
(`GET /bff/v1/orders`, `GET /bff/v1/orders/{id}`), its product-image upload
(`POST /bff/v1/product-images`) and saved address (`GET` and `PUT
/bff/v1/saved-address`), a stored image read through the edge, every host's
`/health/ready` and the payment and carrier simulators' request logs, plus a
free-form request form for anything else.

### 2.4 Identities

| User | Password | Holds |
|---|---|---|
| `demo` | `demo` | `catalog:write`, `orders:write`, `orders:cancel`, `inventory:admin`, `payments:admin` |
| `browser` | `browser` | nothing |
| `privacy` | `privacy` | `privacy:erase` and nothing else; a login of its own because the role destroys a customer's records and `demo` stays without it. The Erasure screen sends as it |
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
| `Record`, `RecordDir` | `false`, `src/Admin.Host/Fakes/fixtures` | write the fixtures from the live platform's answers, scrubbed (§10); the directory resolves against this checkout |

Startup validates that both directories exist and contain what they should
(the Compose file; `package.json`) and refuses to start otherwise, naming the
key to fix. It refuses `Record` together with `FakePlatform`, whose answers
are the fixtures, and a `RecordDir` that does not exist.

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
past the last 50. `up` and `down` are one-shot jobs kept the same way, so that
their output is visible the same way. `ps` and the `exec` reads are jobs too —
bounded, stoppable and killed at shutdown like any other — but the registry
does not keep them (`ProcessSpec.Listed`): their output reaches a screen only
as the view the caller parses, and kept, the Stack screen's `ps` poll alone
would push an exited `npm start`, and the output that says why it died, past
`JobRegistry.KeepExited` within minutes. An `exec` the operator caused rather
than a screen polled, the dead-letter operator's password set and clear (§5.5),
is kept (`ComposeService.ExecListedAsync`).

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
  the caller parses; like `Ps()`, a job the registry does not keep (§5.2).
- `Config()` and `Images()`: `config --format json` and
  `images --format json`, read the same way, for the workstation doctor.

`WorkstationDoctor` reads the workstation before Up, so a failure Compose
would report late is named first. Every read is a line of `run-locally.md`'s
step 0, run as written and bounded to 30 s like `ps`; none changes anything,
none leaves the machine, and none fetches, so a clone is judged against its
last fetch. Each row is ok, a problem, or could-not-tell where the row cannot
be judged — a read that did not answer, or nothing yet to compare — never a
guess. Docker, Node or Python not answering is a problem rather than
could-not-tell, because that silence is itself what Up (or the dead-letter
workbench, for Python) will meet:

- Docker: `docker info` answers.
- Ports: the published ports of the resolved Compose model, against every
  listener and its process (a PowerShell line). A port held by anything but
  Docker's own process is one Up cannot publish.
- Gateway CORS: the gateway's resolved `Cors__Origins__*` admit the client
  origin `ClientUrl` names. Read and reported, never fixed from here.
- Node: `node --version` against the frontend clone's `.nvmrc`.
- Python: `py -3.12 --version` answers `Python 3.12.x`. The stack runs without
  it; the Broker screen's dead-letter workbench (§5.5) runs its tool under
  that same `py -3.12`, so a `py` that does not start, or answers another
  version, is a problem before the first Open on a parked queue meets it.
- Each clone: on `main`, and not behind its upstream as last fetched.
- Images: no image Compose built predates the backend's last commit, since Up
  does not rebuild an image that exists.
- Object store: `object-store` and `image-edge` are running and healthy and
  `object-store-init`, the one-shot that makes the bucket and its policies,
  exited 0. An Up that leaves the store unprovisioned reads green on every
  other row, and the failure arrives later as a publish with an image. A
  service still starting, or a stack with none of the three, is could-not-tell.

A Compose port override has no row: the Compose files declare no port
variable, and `-f` keeps Compose from reading an override file. The
listener and `.nvmrc` reads are PowerShell's, so off Windows the Ports and
Node rows cannot tell.

`ResetService` is "back to a known state" as one listed job: `Down(true)`,
then `Up()`, then the reachability probe of §5.10's `GET /stack` until every
platform surface answers (the reference client is not waited for). It polls
at `ResetService.ReadinessInterval` up to `ResetService.ReadinessCap`, saying
on each poll what it is still waiting for, and a step that fails ends it
there. The reset job owns no process: it carries its steps' output, in order,
under one id. It wipes volumes, so its endpoint takes the same typed
confirmation as `down -v`. There is no seed step: the backend has no seed to
run yet.

`ComposeGate` lets one job at a time stop or start the stack's services. Six
kinds claim it: Up, Down, Down and wipe, the reset, §5.5's projection rebuild
and a Scenario's lease (below). The lease is a claim that outlives any one
command rather than a running job, and every "another `ComposeGate` job" in §5.10
includes it; a lease request is itself refused with 409, naming the kind and the
job, while any of the other five runs. A Compose read claims nothing. While one of them runs, a request of
the same kind is answered with the running job and any other kind is refused
with 409 naming the kind and the job id, so a rebuild's `stop web-bff` and
`start web-bff` never land inside a reset's `down -v` and `up`. Down and Down
and wipe are two kinds because a plain Down answered with a running wipe would
show a volume wipe under a request that did not ask for one. A repair stops and
starts nothing and does not claim it.

**Scenario service control.** A Scenario script whose cause `run-locally.md`
gives as changing a service may change it, and nothing else may. Two changes
exist: stopping `inventory-api`, and recreating `ordering-api` with the
backend's `docker-compose.short-timeouts.yml` added to the `-f` list, so the
saga's waits run in seconds instead of minutes. The values are that file's
(ADR-128); the console sets none of its own. They are
a code-owned table (`ScenarioControl.Table`, which cites the document and the
backend's override file) and the host runs no other service change for any
screen. The rules are these, and a change to any of them is a change to this
paragraph first:

- **A lease, not a switch.** The change is held as one lease under the sixth
  `ComposeGate` kind. At most one lease exists. While it is held, Up, Down, Down
  and wipe, the reset and a projection rebuild are refused with 409 naming the
  lease (the §5.10 rows that refuse "another `ComposeGate` job" mean this too),
  so the stack is never taken apart under a run that depends on it.
- **Written before it is made.** The lease is journaled to a file under
  `Admin:DataDir` (service, change, the run, when) before the first command of
  the change runs, and removed only after the restore has been verified.
- **Always restored, by one line.** The restore of either change is
  `up -d --no-deps <service>` on the base compose file alone. Compose starts a
  stopped container and recreates one whose environment differs, so one line
  undoes both, and it also works on a service whose container a Down removed. It
  adds `--wait`, and the lease is dropped only once `ps` shows the service
  running. The host runs it when a change fails, when the request that asked for
  one goes away mid-change, on request (a Scenario's own call at its end, and on
  its cancellation or the page being left), and at the lease's deadline. It runs
  again from the journal when the host starts, so a host that was killed leaves
  nothing changed past its next start. A restore that fails keeps the lease, the
  journal and the gate, and says so; it is never dropped silently, and Restore
  tries again.
- **A deadline.** Every lease has one. The Scenario asks to hold a change for
  1 to 600 seconds, no change may be held longer, and the host restores at the
  deadline by itself, so a page that closed without asking still leaves nothing
  changed for as long as the host runs. The deadline is armed when the lease is
  taken, from the hold asked for, so a change that takes long does not extend it.
  A command still running when it is abandoned (a change that did not finish
  within three minutes, or whose request was cancelled) is ended before its
  restore starts, so the two never touch the service together. A restore that
  failed is tried again by the host every 30 seconds, as well as by Restore.
- **The limit, stated.** A host killed outright cannot run its own `finally`.
  Until the next start the stack stays as the run left it. The Stack screen
  shows the lease the host holds, what it changed and since when, with a Restore
  button. The journal is read once, in the background, at host start: until
  that restore sets the lease the screen shows none, and a journal the host
  cannot read, or that names a change it does not know, is logged and left in
  place for a person.
- **Visible and stoppable.** Before Run the Scenario page names the services the
  script will change and for how long it expects to hold them. A script that
  holds a lease shows progress as every watch does (what it waits for, how long
  it has, what it last read) and can be cancelled from the console, which
  restores. Run is the confirmation; there is no typed text, since nothing is
  destroyed.
- **FakePlatform is first-class.** The fakes take the same changes and show the
  same lease, so the smoke covers the control without a stack.

### 5.4 FrontendSupervisor

Holds at most one `npm start` job in `FrontendDir`. `Start()` refuses if one
is running or if `node_modules` is absent. `Stop()` kills the tree. `Status()`
reports the job and whether port 5173 answers.

The device lane sits beside it on the Stack screen. `StartDeviceBuild()` runs
`npm run emulator:android` in `FrontendDir` under the same gate, as a second
job that ends by itself (no Stop), refused while one is running or if
`node_modules` is absent, and not waiting on the dev server. `DeviceReads` makes
the three reads of §2.1 before it is pressed and reports each as the doctor's
rows are (`Ok`, `Problem`, `Unknown`): an emulator is attached (one line ending
`device`, none `offline` or `unauthorized`), the gateway answers a preflight from
`https://localhost` with that origin, and the running realm's `mobile-app` client
redirects to exactly the client's `nativeRedirectUri`. A mismatch is reported and
raised in the repository that owns it (the gateway's CORS list and the realm are
the backend's), never fixed from here. Starting the AVD, installing and starting
the app and the sign-in on the device stay by hand, as `run-locally.md` has them;
the adb `reverse` it also lists changes the emulator's forwarding and is not run.

### 5.5 Broker

`BrokerService` runs the three `rabbitmqctl` forms from §2.1 with
`--formatter json` through `ComposeService.Exec` and returns queues,
exchanges and permissions. It marks queues whose name ends in `_error` or
`_skipped` (`ParkedQueues`, a cited copy of the dead-letter tool's suffixes
and the runbook that owns each kind), links the runbook of one that holds a
message, names each queue's consumer from `PlatformQueues.Table` (a cited
copy of the backend's queue constants, which the drift gate reads both ways;
a parked queue is named by its endpoint's consumer), and answers
`IsDrained(queues, PlatformQueues.Projection)` — declared and holding no
messages — which `GET /broker/queues` carries as `projection` for the Broker
screen and the API screen's "wait for the projection" affordance. The drain
names the one queue it read and claims nothing about the others. A
broker that does not answer, or output that is not the formatter's JSON
array, is `reachable: false` with the error, and each `rabbitmqctl` is
bounded to 30 s like `ps`.

`ProjectionRebuildService` puts the BFF's order projection right as one job,
the way `ResetService` resets the stack: a repair is the backend's
`tools/bff-replay` with no argument, and a rebuild stops `web-bff`, runs the
tool with `--reset` and starts `web-bff` again, because the tool's README says
a reset needs the BFF's consumption stopped. The BFF is started again even
when the tool fails, since leaving it down would turn a partial projection
into an outage. The six connections the tool reads are handed to the child as
environment variables (`ProcessSpec.Environment`, never part of the command
line), built from `AdminOptions` and held by the drift gate to the Compose
model and to `run-locally.md`; the transcript prints them scrubbed. A run ends
by watching `bff-order-events` until it is empty or two minutes pass, and says
which; neither fails a run whose tool succeeded. The screen states, before
either button, what the tool cannot restore, in sentences the drift gate holds
to the tool's README. A rebuild deletes the projection, so it takes a typed
confirmation (`bff-replay --reset`) as a wipe does (§8); a repair deletes
nothing and takes none.

`DeadLetterService` reads, replays and discards a parked message with the
backend's `tools/dead-letters`, as `run-locally.md`'s "Replay or discard a dead
letter" does it by hand; the console parses no queue and moves no message
itself. The tool runs as the broker account `dead-letter-operator`, which ships
with no password. Each run mints a random one, sets it with `rabbitmqctl
change_password`, hands it to the tool as `DEAD_LETTERS_PASSWORD`
(`ProcessSpec.Environment`, never argv), and clears it when the run ends,
whether the tool succeeded or not, and whenever the set was attempted, since a
set that timed out may still have landed; so between runs the account has none, and
runs are one at a time, since one run's clear would end another's login. A
read is `inspect --json`, the contract the tool's README fixes for this
console (schema 1), and puts every message back, flagged redelivered, so it
takes no confirmation. A replay or discard names its messages by id and is a
listed job. The real run with `--execute` takes the typed confirmation
(`--execute`) as a wipe does (§8), and always passes `--record` and
`--audit-log` under `Admin:DataDir`'s `dead-letters/`; FakePlatform, whose
records no incident could need, writes them under the temp directory instead.
The host takes the real run on its own; it is the screen that offers it only
for the message and verb a dry run was last started for, and the dry run's
output, in the job pane below the messages, says what the real run would do. A stderr line the tool
prefixes `unrecorded` is the last copy of a message it took and could not
record, so the job keeps it in a file beside the record and says where. The
drift gate holds the tool's path, the account, the variable, the prefix and the
contract's fields to the backend.

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

The token clock (`POST /identity/clock`) reads the token the host already
holds for an identity without minting one, and never returns the token: its
expiry, `RenewsAt` (expiry less the reuse margin, after which the next call
mints a new one), and the permissions in its `permission` claim. The API
screen's identity picker shows it, so a 403 is explained by the grant it
lacks. The margin has one owner, `TokenService`, and the page holds no
threshold of its own. The Scenario reads the same clock before each call it
sends: past `RenewsAt`, or with no token held once the run has sent, that
call mints a new one, and the step says so. Nothing is retried on the page's
behalf; a 401 ends the step as the gateway returned it.

### 5.7 ApiCatalog and RequestProxy

`ApiCatalog.Load()` fetches `/openapi/v1.json` from Catalog, Ordering, Inventory, Payments and Privacy with
a `demo` token, rewrites each path onto the gateway (`/v1/catalog/...` becomes
`/api/v1/catalog/...`), and merges the curated operations from §2.3. Each
operation carries method, gateway path, path parameters, an example body (the
one `run-locally.md` sends for that operation where there is one, otherwise
placeholders built from the request schema, since the documents carry no
examples), and the edge policy from a cited copy of the gateway's route table
(`Api/GatewayRoutes.cs`). The catalog is cached and reloaded on demand; when a
service is down its operations are listed as unavailable rather than dropped.

Each document read is also compared with the one kept for its service
(`OpenApiBaselines`, under `Admin:DataDir`), and the source carries the
difference: operations, response codes and schema fields added or removed.
The first document seen for a service becomes its baseline. After that the
baseline stays until it is accepted again, so a change keeps showing until
someone has read it. This is a read of documents the catalog already fetches,
with no new upstream call. FakePlatform keeps its baselines in a directory of
its own per process.

`RequestProxy.Send(request)` takes method, an absolute URL restricted to the
configured surfaces (the gateway, Catalog, Ordering, BFF, Inventory, Payments, Privacy,
the payment and carrier simulator origins and the image edge), headers,
body, an identity and an optional correlation id. It attaches the token, sets
`X-Correlation-Id` (generated if absent; a supplied id that breaks those rules
is refused, because the backend would silently replace it; the header's rules
are letters, digits, `-`, `_`, up to 128 characters), sends,
and returns status, headers, body, elapsed time and the correlation id used.
Status and body are never rewritten: a 401, a 403 and the three different 409s
must read exactly as the reference client sees them.

The proxy's body is text, so it cannot carry an image. `ProductImageUploader`
is the one other outbound call: it `PUT`s the console's sample image (a real
one-pixel PNG, `SampleImage`, because the store's consumer sniffs the bytes and
refuses anything else) to a pre-signed URL the BFF returned, and only where
that URL names the configured object store. It signs nothing and holds no
credential; the signature is in the URL and the store judges it. Its answer is
returned in the proxy's shape and the request is kept in the transcript (§5.11)
as the curl that would send it, naming the file as `run-locally.md` does.

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
- Prometheus: `GET …/proxy/uid/{prometheus}/api/v1/query`, an instant query,
  with the golden-signal queries the backend's
  `deploy/observability/dashboards/golden-signals.json` uses, for the Stack
  screen's health strip. The strip is every query panel of that dashboard,
  one table row per `service_name`; `GoldenSignals` owns the copied queries
  and the drift gate checks them both ways. The query text stays the
  dashboard's, so the join (`TelemetryHealthService`) decides what an
  absence means: a status code with no series is a zero rate for a service
  the request rate names; a 5xx share with no series is zero for a service
  with traffic, because the division has no numerator to match; a share over
  no requests, a quantile with no series and any value that is not finite
  stay absent. The 422 panel's per-route series are summed, because rates
  add. One failed query makes the whole strip unavailable, never a partial
  one. The targets in the §13.7 panel titles are the backend's, and the
  strip prints measured values only.
- Prometheus, alert rules: `GET …/proxy/uid/{prometheus}/api/v1/rules?type=alert`,
  Prometheus's own rules read, for the alert strip beside the golden signals.
  The rules are Prometheus's and not Grafana-managed (the local `grafana`
  service mounts `platform-alerts.yaml` into the Prometheus its image bundles,
  backend#502), so Grafana's own alerting API would list none of them; the
  read goes through `GrafanaClient` to the configured Grafana URL and adds no
  origin. Each loaded rule shows its state (`inactive`, `pending`, `firing`),
  severity label and the instances alight, firing first. An empty list is
  said to be empty and is never shown as a quiet platform. **The rules in
  `awaiting-signal.yaml` are listed apart, as waiting, each with the metric it
  reads that nothing publishes yet**: no Prometheus loads that file, so they
  are never in the loaded list and never shown as inactive, because a rule
  that cannot fire says nothing about health. `WaitingAlerts` copies their
  names and metrics, and the drift gate checks both against the file, and
  against `platform-alerts.yaml`, in which none may appear. The waiting list
  is the host's and is returned whether or not Prometheus answered; the
  loaded list is unavailable, with the reason, when it did not.

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
   is rendered as the timeline's terminal event: the projection queue, then
   every other queue of `PlatformQueues.Table` a span of the timeline named
   (a receive's endpoint or a send's destination; an exchange is not in the
   table and is left out), each with its depth, and a sentence saying that
   the publish runs in a trace this correlation id cannot reach — or, where
   consume-side spans did reach the timeline through traces their services
   logged this id in, that a hop which logged nothing is missing; and a
   timeline with no outbox write at all says nothing was handed over. On the
   live platform the saga's consume-side traces are not reached today (below,
   and #54), so after a write the publish form is the one an operator sees. A
   message
   still waiting in one of them or parked in its `_error` or `_skipped` queue
   shows there rather than as silence.

Restoring the trace across the hop is a `blueprint-backend` change — persist
`traceparent` on the outbox row when it is staged and restore it into an
`Activity` at dispatch — and is raised there, not worked around here.

The result is one ordered timeline of `TraceEvent { At, Source, Service,
Kind, Summary, TraceId, Link }`, where `Kind` is one of `HttpIn`, `Log`,
`Span`, `Outbox`, `Publish`, `Consume`, `Saga`, `Queued`, `Error`. `Outbox`,
`Publish`, `Consume` and `Saga` are recognised from span names and attributes
that MassTransit's instrumentation and `Common.Infrastructure`'s
`OutboxDispatcher` own; the recogniser is a table of patterns, so a renamed
span is a one-line change and an unrecognised span is still shown as `Span`.
MassTransit tags a publish and a command send alike `send`, and both are a
handover (`Publish`). A row may name a step of the platform as well as a kind
of span: the fulfilment saga's step (`Saga`, with the states it moved
between), Inventory's reservation and Payments' authorisation (`Consume`,
matched on the contract's message URN), and Shipping's despatch (`Consume`,
matched on the URN and Shipping's service name, since Notifications consumes
the same event). The tag values are those of the
MassTransit version `SpanRecogniser.MassTransitVersion` names, and the drift
gate holds that version to the backend's pin.

**None of those step rows is fed on the live platform yet.** Measured
2026-10-03 and recorded on #54: the saga's spans live in traces rooted at
outbox sends, and no saga log line carries a correlation id, so step 1 never
names those traces and step 2 never fetches them. The rows recognise the spans
the day a timeline reaches them — the backend carrying the correlation id onto
the saga's log scope or trace context through the outbox, or a search here by
order id, which would be a change to this section — and until then the
timeline ends at the outbox as step 2 says. FakePlatform records no saga trace
for the same reason: a fake that reached one would invent a reach the platform
lacks.
Each row deep-links to Grafana Explore with the datasource and query
pre-filled.

The Trace screen keeps the last few timelines per correlation id for the
browser session (`TraceHistory`, bounded by `KEPT_PER_ID` and `KEPT_IDS`),
in memory and never in storage. A refetch of the same id, over the same
window, names what arrived and what left since the previous fetch, or says
that nothing changed with both fetch times, so a stalled flow reads
differently from a finished one. The terminal `Queued` row is compared by
its summary alone, because its time is the snapshot's own. The diff is over
what the screen already read; it adds no upstream query.

### 5.10 Endpoints

All under `/api`, JSON, loopback only.

| Method and path | Does |
|---|---|
| `GET /stack` | `ServiceStatus[]`, the frontend job summary, reachability of every HTTP surface of §2.2 that answers a readiness route, the object store and image edge among them, and the Scenario lease in force or null (§5.3) |
| `GET /stack/lease`, `POST /stack/lease`, `POST /stack/lease/restore` | §5.3's lease: the table of changes with the lease held; body `{ change, holdSeconds, label }`, which waits for the change and answers the lease held, 400 for a change off the table or a hold off its bounds, 409 naming the claim that refuses it (another lease, or another `ComposeGate` job), 500 for a change that failed, which it has already undone or says it could not; and the restore, 200 with nothing held too, 500 when the restore failed and the lease is still held |
| `GET /stack/doctor` | §5.3's workstation checks, one row each with its verdict and what the reads said |
| `POST /stack/backend/up` | Compose up; returns the job, or the Up still running; 409 while another `ComposeGate` job (§5.3) runs |
| `POST /stack/backend/down` | body `{ wipeVolumes, confirm }`; returns the job, or a Down with the same `wipeVolumes` still running; 409 while another `ComposeGate` job (§5.3) runs, a Down with the other `wipeVolumes` included |
| `POST /stack/backend/reset` | body `{ confirm }`, which must be `down -v`; §5.3's reset, returned as one job, or the one still running; 409 while another `ComposeGate` job runs |
| `POST /stack/frontend/start`, `POST /stack/frontend/stop` | supervisor |
| `POST /stack/frontend/device-build`, `GET /stack/frontend/device-reads` | §5.4's device lane: the build job, or 409 while one runs or without `node_modules`; and the three reads, one row each with its verdict |
| `GET /jobs`, `GET /jobs/{id}` | summaries; one job with its last lines |
| `GET /jobs/{id}/stream` | Server-Sent Events, one event per line, `id:` is the sequence number so `Last-Event-ID` resumes from the ring buffer |
| `POST /logs/follow` | body `{ services }`; stops the previous follow job if it is still running, starts one and returns it — the host keeps one |
| `POST /logs/follow/{id}/stop` | stops that follow job if it is still the current one; 204 either way. Named, so a Stop that reaches the host after the next Follow ends nothing. There is no generic job stop: it would reach `up`, `down -v` and the supervisor's `npm start` |
| `GET /broker/queues`, `/broker/exchanges`, `/broker/permissions` | §5.5; `queues` also carries `projection`, the drain state of `ordering-catalog-events` |
| `GET /broker/projection`, `POST /broker/projection/repair`, `POST /broker/projection/rebuild` | §5.5; the plan (queue, typed confirmation, the tool's limits), then a listed job; the rebuild is refused with 400 unless `confirm` is `bff-replay --reset`; a repair or rebuild is refused with 409 while a run of the other kind is going, and the same kind is returned as it is; a rebuild is also refused with 409 while any other `ComposeGate` job (§5.3) runs |
| `GET /broker/dead-letters`, `GET /broker/dead-letters/{queue}`, `POST /broker/dead-letters/{queue}/{replay\|discard}` | §5.5; the plan (typed confirmation, the read's limit, the record directory), a parked queue's messages, then a listed job for body `{ messageIds, execute, confirm }`. 400 for a queue that is not an `_error` or `_skipped` one, no ids or an id that could read as a flag, or `execute` without `confirm` of `--execute`; 409 while another dead-letter run holds the operator |
| `GET /erasure/holders` | §6's Erasure screen: the holders Privacy asks (`ErasureHolders.Table`, drift-checked against the backend's `Privacy__Responders__n`), each with the depth of its `*-privacy` queue, `privacy-completions`' depth and the overdue runbook's URL. The request itself is raised, read and reissued through `POST /proxy`, as the gateway answers it |
| `GET /mail/messages`, `GET /mail/messages/{id}` | §5.12; the list, and one message read, each 200 whatever the sink did, its 404 included. 400 for an id that is not 1-64 letters or digits, since it becomes a path segment of the URL the host builds |
| `GET /identity/users`, `POST /identity/token`, `POST /identity/clock` | §5.6 |
| `GET /catalog/operations`, `POST /catalog/reload` | §5.7 |
| `POST /catalog/baseline/{service}` | makes that service's last fetched document its baseline, then reloads; 404 for a name the catalog has not fetched |
| `POST /proxy` | §5.7 |
| `POST /product-images/upload` | body `{ uploadUrl, headers }`; §5.7's `PUT` of the sample image, in the proxy's result shape; 400 for a URL that is not the configured object store |
| `GET /trace/{correlationId}?window=15m` | §5.9. `window` is `90s`/`15m`/`2h` bounded to `[1m, 24h]`, default `15m`; a window outside it, and an id outside the backend's adoptable alphabet (the id reaches a LogQL string, so this is the injection boundary), are 400 problem details. |
| `GET /telemetry/health` | the golden-signal strip |
| `GET /telemetry/alerts` | the alert strip: loaded rules and the waiting ones, apart |
| `GET /transcript`, `GET /transcript/script` | §5.11; the entries, and the same as one bash script in `text/plain` |

Streams use Server-Sent Events rather than WebSockets because every stream is
server-to-client and line-oriented, and SSE reconnects for free.

### 5.11 OperatorTranscript

§1 says the console runs the platform's own commands, which only shows if a
person can take them away. `OperatorTranscript` keeps, for the host's life and
in memory only (§8), each child process the operator caused — the argv as given,
the working directory, the exit code once there is one — and each request
`RequestProxy` sent, rendered as the `curl` that sends it from the message as
built, with the identity it went as and the status that came back.

What the screens poll is left out by the line `/jobs` already draws:
`TranscribingProcessRunner` keeps a process only when `ProcessSpec.Listed`
says the registry would (§5.2), so `ps` and the `exec` reads never appear; the
dead-letter operator's password set and clear are `exec`s the operator caused,
and are listed (`ComposeService.ExecListedAsync`). A process is
let go once it exits and only its exit code kept, so the transcript never holds
an output ring the registry has trimmed. A request takes its place when it is
sent, not when it is answered, and is settled whatever became of it, so the
order is the order things began and a send abandoned mid-flight is still on the
record. A token the proxy attached, and any Authorization a caller pasted, keep
their scheme and lose their value, and a Cookie keeps its names and loses its
values: the headers the API screen's history drops, which no pattern can
recognise by value. A value a spec names in `ProcessSpec.Secrets`, the
dead-letter operator's per-run password, is scrubbed outright for the same
reason, whether it is an argument or a variable's value, and so is it in the
spec's `CommandLine`, which the job list and the host's log print. Every word
then passes through `FixtureScrubber` before it
is quoted, so a transcript and a fixture are held to one definition of a
secret. The quoted line is not scrubbed again: the form rule's value may be a
quote, and a second pass would take a closing one. The token grant itself is `TokenService`'s and not a proxied
request, so it is not in the transcript; the script's header says to mint a
token as `run-locally.md` does. The entries kept are bounded by
`OperatorTranscript.Capacity`, and the oldest go first and are counted.

### 5.12 MailSinkClient

Notifications sends every message to a sink, Mailpit (`mailpit` in the
backend's Notifications unit), and the question "did the order tell anyone" is
answered there. `MailSinkClient` makes the three reads `run-locally.md` makes of
it by hand, against `Admin:MailSinkUrl` and the API of the Mailpit version
`MailSinkClient.Image` names, which a drift check holds the Compose unit to:
`GET /api/v1/messages` for the newest `MailSinkClient.Limit`, then each one's
`GET /api/v1/message/{id}/headers` for its `X-Correlation-Id`, and
`GET /api/v1/message/{id}` when one is opened. The header is the one
Notifications writes on every message (drift-checked), and it is offered as a
Trace link only where `CorrelationId.IsAdoptable` would adopt it, since the
trace endpoint refuses anything else.

Opening a message marks it read in the sink, as opening it in the sink's own UI
does; the list and the headers do not. That is the only state the console
changes there. Clearing the sink stays in its UI, which the screen links, and
the chaos API the Compose unit enables (`PUT /api/v1/chaos`) is never called.
A sink that does not answer is `Reachable: false` with the reason, and a
message whose headers cannot be read is still listed, with no link.

The body is shown as the sink split it. An HTML part renders in an `iframe`
whose `sandbox` is empty, so no script in it runs and it has an opaque origin
that cannot reach the console's; the SPA hands it to the frame unsanitised,
because Angular's sanitiser would strip the template's styles, and the empty
sandbox is what makes that safe. No CSP is relaxed for it. A plain-text part,
which is what the backend sends today, is shown as text.

## 6. The SPA

Angular 22 standalone components, signals, the router, no Ionic. The layout
mirrors the reference client's so a reader of one repo can read the other:
`src/app/core/` (config, the host client, the SSE client, identity state),
`src/app/features/` one directory per screen, `src/app/shared/` for the
output pane, status pill and JSON viewer.

The shell's screens are one list, `SCREENS` in `src/app/screens.ts`, read by
the bar and by the command palette. `Ctrl+K` (`Cmd+K`) opens the palette from
any screen: go to a screen, run the Scenario, or open what was typed as a
correlation id on the Trace screen. The palette does not judge the id; the
trace endpoint refuses one `CorrelationId.IsAdoptable` would not adopt, so the
palette is no way round the LogQL boundary of §5.9. A Scenario run is asked
for through `ScenarioLauncher`, never the URL, so a link or a reload never
publishes and orders; a request the screen did not act on is dropped when it
goes. The palette is a keyboard tool: a modal dialog whose input keeps focus
and names the active option through `aria-activedescendant`.

| Screen | Shows | Does |
|---|---|---|
| **Stack** | the workstation doctor's rows above the buttons, read when the screen opens; one row per Compose service with state, health and port; the frontend job; a reachability strip; quick links to the client, Grafana, Keycloak | Check the workstation again; Up, Down, Down and wipe, Reset (both behind one typed confirmation), Start and Stop frontend; opens the job's output pane |
| **Logs** | a follow stream with service filter, text search and correlation-id highlight | start, stop (the host's follow job too, as does leaving the screen), clear |
| **Broker** | queues with depth, `_error` and `_skipped` queues marked with the runbook of one that holds a message, a BFF projection panel (what the rebuild tool cannot restore, the depth of `bff-order-events`), exchanges, permissions; a drained indicator for `ordering-catalog-events`; an opened parked queue's messages, each with its fault, type, body and correlation id as a link into Trace | refresh, auto-refresh of all three; Repair, and Rebuild behind a typed confirmation, each opening the job's output pane and reading the queues again when it starts and when it ends; Open on a parked queue that holds a message; per message a dry-run Replay or Discard, then, once a dry run of that message and verb is started, that one for real behind a typed confirmation, each opening the job's output pane |
| **Erasure** | one row per holder of an erasure request (the backend's `Privacy__Responders__n`), each answered, waiting or overdue as the request reads, with its `*-privacy` queue's depth and `privacy-completions`' beside them; the request's status, dates and reissue count; an answer from outside the request's set marked not counted; the overdue runbook's link | Raise a request for a subject id, as the `privacy` login; read one by id, or every 3 s until it closes; Reissue an overdue one. Nothing here grants the role: the realm's owner does (§13) |
| **Mail** | §5.12's list, newest first: when, to whom, the subject, and the correlation id as a link into Trace, or `none`; how many the sink holds when that is more than listed; a line saying so when no message carries a correlation id; an opened message's addresses, date and body | Refresh; filter by correlation id (also `?correlationId=`), leaving one order's messages; Open a message; follow its Trace link; open the sink's own UI, where clearing is done |
| **API** | operation tree on the left, each source with what its document changed since the kept baseline; request editor (path params, headers, body pre-filled from the schema example, `commandId` generated per send) and identity picker with its token clock (§5.6); response pane with status, timing, headers, body; a history list | Send; accept a changed document as the new baseline; "Trace this call" opens the Trace screen with the response's correlation id |
| **Trace** | the §5.9 timeline for a correlation id, grouped by service, with Grafana deep links, and on a refetch what arrived or left since the last one | enter an id or arrive from the API screen; reload |
| **Scenario** | `run-locally.md`'s calls as one script of a library (`SCRIPTS` in `scenario-run.ts` owns the list; `docs/superpowers/specs/2026-10-15-scenario-scripts.md` the causes and the fakes' rules) — publish, wait for the drain, quote, order, then either watch the order to confirmed, despatched and delivered, cancel it and watch it to cancelled, or apply a cause `run-locally.md` names (a price ending `.01`, stock set to 0) and watch the saga refuse the order, or order to the delivery postal code `SIM-TRANSIT` and watch it stay despatched, and ask to cancel it and see the platform refuse with 422 `order.already_shipped` — each step with its status, body and own correlation id, each watch saying what it waits for and what it last read | Pick the script; run as a realm user; each step that sent links to its trace; the first step that does not succeed ends the run |
| **Transcript** | §5.11's entries in order: each process with its directory and exit code, each request as its `curl` with the identity and status, polled | Copy as shell script, which copies the host's rendered script rather than one built in the page, so the scrubber has the last word |

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
   │             ────▶ mailpit :8025 (GET only)
   └─ ComposeService.Exec ─▶ rabbitmqctl inside the rabbitmq container
```

The SPA never calls the platform directly; §3 says why.

## 8. Security posture

The console holds realm passwords and can wipe volumes, so it is a loopback
tool and nothing else. The host binds `127.0.0.1` only, refuses to start on
any other address, and sets no CORS headers, so a page on another origin
cannot drive it. There is no login of its own. `down -v` requires the typed
confirmation, as do a projection rebuild and a dead letter's real replay or
discard. The one service change the console makes beyond Up, Down and the BFF
rebuild is a Scenario's lease (§5.3): two named changes, journaled before they
are made, always undone, and refused while anything else is taking the stack
apart.

Beside the realm passwords it knows the local stack's development defaults
(`SqlPassword`, `BffBrokerConnection`) and one broker credential it never
stores: the dead-letter operator's password, minted for one run, set before it
and cleared after it (§5.5), so the account has none between runs, as it ships.
That credential reaches the Management API only through the backend's tool;
the console never calls that API itself. The proxy accepts only URLs on the configured surfaces, so it
cannot be used as an open relay from a tab the developer left open.

The mail sink is one origin more that the host reaches, and it is kept off the
proxy's surfaces on purpose. Its API clears the sink and stages a failing
relay (`PUT /api/v1/chaos`), so as a surface it would put a write the platform's
tests rely on within reach of any request the proxy forwards. `MailSinkClient`
(§5.12) instead sends `GET` alone, to `Admin:MailSinkUrl` alone, on three paths
it builds itself, with the one caller-supplied part, a message id, refused
unless it is letters and digits. Linking the sink's UI instead would have cost
the correlation join, which is the screen's whole value. What the host can do
there is what its UI does when a message is opened: mark it read.

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
- Fake `HttpMessageHandler`s for Keycloak, the OpenAPI documents, Grafana
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

**The fixtures are recordings where a recording can be made.** Under
`Admin:Record=true` against a loopback stack, the host itself writes
`Fakes/fixtures` from what the live platform answered. Only a successful
answer is recorded. `FixtureRecordings` owns the list: the upstream answer
each recorded file comes from, and each file written by hand with the
reason a recording run cannot produce it. The answers kept in code
(`WorkstationRecordings`, `FakeGateway`, `FakeKeycloak` and
`FakePlatformScripts`) are outside the switch. A re-recording is reviewed as
its diff.

Every recorded byte passes through `FixtureScrubber`, which owns what is
taken out, before it is written. The gate
(`FixtureGateTests`) reads every file under `Fakes/` from the directory,
rather than from a list, and fails on anything the scrubber would still
change, on a fixture with no source named, and on a fixture the host does not
embed. Recording adds no listener and stores no credential (§8): what it
writes is the platform's answer with its credentials taken out.

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
| 6 Scenario | the Scenario screen: publish → wait for drain → quote → order, then watch the order to delivered, or cancel it and watch it to cancelled, with a trace per HTTP step (below) |

**Phase 6 adds no host endpoint.** Every step is a call the API screen can
already make — `POST /proxy` with the catalog's own operation and its
`run-locally.md` example body, and `GET /broker/queues` for the drain. The
BFF order reads the watches send are curated operations (§2.3), and
FakePlatform answers them with `FakeOrders` (below). Each step carries the id the previous one produced (the
product into the quote and the order, the order into the cancel and the
watches), and each HTTP step its own correlation id, so each
links to its own trace unless nothing left the console (a token Keycloak
refused, a call that never went out); the drain asks the broker and has
none. The drain is the API screen's watch, with its interval and cap, and a
projection that is not drained ends the run rather than ordering against a
price that may not be there. Drained is `run-locally.md`'s wait and no
more: the outbox publishes after the request, so an empty queue can precede
the event, and no surface says one product has been projected (§5.9 is the
same hop). A per-product readiness signal, such as a read of Ordering's price
for one product, is a `blueprint-backend` change.

A watch reads the BFF's buyer order read, the one `run-locally.md` makes by
hand, with the drain's interval and cap, until its timeline shows the step
waited for. The BFF learns from the broker, so a 404 before its first answer
is waited out; any other status ends the step, and so does an order that
ended in a cancellation the watch was not waiting for. The order's state is
the buyer's view, not the saga's: confirmed, despatched and delivered are what
the BFF projected, and the reservation and payment between them are on the
Trace screen. FakePlatform's `FakeOrders` moves each order it placed a step a
second, so the watches have something to watch; it is written by hand, since a
recording holds one instant of an order.

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
- Mobile, Ionic, Capacitor. This is a desktop tool. The one thing it does for the
  mobile client is what it does for the web one, operate it: it runs the client's
  own `emulator:android` npm script and reads what a first device run fails on
  (§5.4), because `run-locally.md` step 4 has both. It starts no emulator,
  installs and launches no app, signs in on no device and holds no mobile
  code; the rest of step 4 stays by hand.
