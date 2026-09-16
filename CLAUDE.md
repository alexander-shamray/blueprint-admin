# CLAUDE.md

Guidance for Claude Code in this repository: what it is, where things are,
how to act. Facts that move with a PR live elsewhere and are cited from here.

## What this repo is

`blueprint-admin` is the local admin console for `dotnet-ddd-blueprint`. The
specification is `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`;
read its §1 before changing behaviour and its §13 before adding any. The
implementation plans are under `docs/superpowers/plans/`.

Two projects, mirroring the sibling repositories:

    src/Admin.Host/      .NET 10 minimal API. Runs docker compose and npm as child
                         processes, proxies HTTP to the platform, streams output over SSE.
    src/Admin.Web/       Angular 22 standalone SPA, the console. Built into Admin.Host/wwwroot.
    tests/Admin.Host.Tests/  xunit. The SPA's Vitest specs sit beside the code; Playwright in src/Admin.Web/e2e.

Owners of facts you will be tempted to restate: ports and URLs are
`src/Admin.Host/Config/AdminOptions.cs` (defaults) and `src/Admin.Web/angular.json`
(dev server) and `src/Admin.Web/proxy.conf.json`; the Compose service list is
the backend's `deploy/compose/`; the fake platform's recordings are
`src/Admin.Host/Fakes/`. The gateway's edge policies are copied, with their
owner cited, in `src/Admin.Host/Api/GatewayRoutes.cs`, and the example request
bodies in `src/Admin.Host/Api/RunLocallyExamples.cs`; change them when the
backend's route table or `run-locally.md` changes, not otherwise. The
broker's Compose service and the projection queue name are copied, with their
owners cited, in `src/Admin.Host/Broker/BrokerService.cs`; the operation that
starts the API screen's drain watch is `PUBLISH_OPERATION` in
`src/Admin.Web/src/app/features/api/api-page.ts`. Grafana's query shapes, Loki's
structured-metadata field names and the Explore link form are copied, with the
measurement that established them cited, in
`src/Admin.Host/Telemetry/GrafanaClient.cs` and
`src/Admin.Host/Trace/ExploreLink.cs`; what a Tempo span *is* is decided by
`SpanRecogniser.Table` in `src/Admin.Host/Trace/SpanRecogniser.cs` — from the
span's attributes, with its name read only as the documented last resort inside
the outbox rule, where instrumentation that stamps no statement attribute names
the span after the table it touched. A renamed span is a one-line change there. The
operating system is known in three files only,
`src/Admin.Host/Jobs/ProcessRunner.cs` (tree kill),
`src/Admin.Host/Jobs/WindowsJobObject.cs` (the Windows job object that reaches
orphaned descendants) and `src/Admin.Host/Jobs/ExecutableResolver.cs` (`npm` is
`npm.cmd` on Windows); start commands by bare name.

## Rules that the build enforces

- .NET SDK is pinned by `global.json` with roll-forward disabled.
- `TreatWarningsAsErrors`; IDE0055, IDE0065 and IDE0161 are build errors.
  No column alignment of `=` or `=>`.
- Every `.cs` is CRLF (`.gitattributes`). Files written with LF fail IDE0055
  on every line: run `dotnet format whitespace BlueprintAdmin.slnx` before
  building anything you created.
- Test names are sentences with underscores.

## How to act here

- The host binds loopback only and has no login. Never add a listener, a CORS
  header or a credential store; spec §8 says why.
- Every command the host runs is a line from the workspace's `run-locally.md`.
  If the console needs something that document does not do, change the
  document's owner first, not the console.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited from
  here. If a backend fact is not observable, raise it there.
- FakePlatform is a first-class mode: a new endpoint is not done until it
  works against the fakes and the Playwright smoke covers its screen.
- TDD, one test cycle per change, small commits with `feat:`/`fix:`/`test:`/
  `chore:`/`docs:` prefixes.

## Running and testing

See `README.md`. Short form: `dotnet run --project src/Admin.Host`
(`-- --Admin:FakePlatform=true` for no platform), `npm start` in
`src/Admin.Web` for the dev server on 5301, `dotnet test`, `npm test`,
`npm run e2e`.
