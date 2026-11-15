# The console's game day

blueprint-backend's game day causes a runbook's alert and checks it fires; this
console only shows that catalogue (`GameDayService.cs`). This directory is the
console's own: it causes, for real, each failure the console is written to
survive or report, and checks that the host's `/api` says what the code
promises. FakePlatform cannot do this: a recording of a failure is not one.

## What a pass proves, and what it does not

A pass means the host, against the real platform, put the failure into the
field its contract names: `/api/stack`'s row and probe, a `reachable: false`
with an error, a 502 with its title, a job that reads `Exited`. Every screen
renders those fields, and what the SPA draws from them is the Playwright
smoke's subject, against FakePlatform, not this one's. A check reads `/api`
and nothing in a browser.

The stack-free suite, `test_game_day.py`, proves the other half: that each
check reads the field it names and calls a wrong answer a finding, that the
runner restores after a cause that raised, and that every scenario file is
one the runner runs. It needs no Docker and runs on every pull request that
touches this directory.

## The scenarios

`py -3.12 tools/game-day/game_day.py --list` names them with their claims;
each module's docstring cites the code that makes the claim.

| Scenario | Failure caused | Owner of the claim |
|---|---|---|
| `sibling-dirs` | A second host started with a wrong `Admin:BackendDir`, then `Admin:FrontendDir` | `RepoPaths.From`, resolved before `app.Run` in `Program.cs` |
| `frontend-killed` | The dev server `npm start` runs, killed from outside; then the console's own stop | `ProcessRunner.cs`, `WindowsJobObject.cs`, `FrontendSupervisor` |
| `service-stopped` | `docker compose stop` on a probed service | `StackEndpoints.ReadAsync`, `ComposePsParser`, `PlatformProbe` |
| `service-paused` | `docker compose pause` on the same service | the same, and `PlatformProbe`'s per-target timeout |
| `keycloak-down` | Keycloak stopped | `IdentityEndpoints` (`KeycloakUnreachable`), `PlatformProbe` |
| `grafana-down` | Grafana stopped, and with it Loki and Tempo | `GrafanaClient`, `TelemetryHealthService`, `AlertStripService`, `EventTraceService` |
| `broker-down` | RabbitMQ stopped | `BrokerService.QueuesAsync`, read by the SPA's drain watch |

Two limits, said here rather than discovered:

- **Loki and Tempo are not lost apart from Grafana.** The backend runs all
  three in one `grafana/otel-lgtm` container, and the host reaches Loki and
  Tempo only through Grafana's datasource proxy, so stopping `grafana` is the
  one real way to lose them; there is no separate container to stop.
- **The broker is lost under the drain watch, not under a publish.** The
  watch (`PUBLISH_OPERATION` in `api-client.ts`) polls `/api/broker/queues`
  after a publish, so that read is the host's half of the claim. A real
  `PublishProduct` while RabbitMQ is down is the backend's outbox to hold,
  and its answer passes through the proxy as the gateway sent it.

## Running it

The host must be running against the real platform (not FakePlatform), with
the backend's Compose stack up and the frontend clone installed (`npm ci`
there), as `run-locally.md` says. From the repository root:

```bash
py -3.12 tools/game-day/game_day.py all
py -3.12 tools/game-day/game_day.py keycloak-down --cause    # cause and check, leave it broken
py -3.12 tools/game-day/game_day.py keycloak-down --restore  # put it back
```

`--host` is the console under test, by default `Admin:Port`'s default from
`AdminOptions.cs`; `--compose-file` is the backend's Compose file, by default
where the sibling layout puts it. Each finding prints as a `FINDING:` line,
and the run exits 1 on any, 2 on a scenario it does not know.

Each scenario first reads that it starts from a settled state, and refuses
otherwise, so a pass means the cause did it; and each restore runs whatever
the cause or the check did, then waits for the settled state again, so one
scenario cannot poison the next.

## Running it in CI

`.github/workflows/game-day.yml`'s `suite` job runs the stack-free suite on
a pull request or push that touches this directory. Its `run` job is
`workflow_dispatch` only: it checks out the backend at the commit
`.github/drift/backend.sha` pins and the frontend's `main`, builds the host,
installs the client, starts the Compose stack and the host, and runs the
scenario asked for, `all` by default. A whole platform built and started for
every pull request would cost more than the console's own CI, for failures
whose code changes rarely.
