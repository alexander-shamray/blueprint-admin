# Blueprint admin, phase 5: Trace — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Trace screen that takes a correlation id — typed in, or carried from the API screen's "Trace this call" — and renders one ordered timeline of what that request did across services: the log lines Loki holds for the id, the spans Tempo holds for the traces those lines name, and the broker's current queue snapshot as the last event; each row deep-linking into Grafana Explore. In the real mode and in FakePlatform mode.

**Architecture:** A new `Admin.Host.Telemetry` namespace holds `GrafanaClient` — datasource uid resolution cached after first success, a Loki `query_range` and a Tempo trace fetch, both through Grafana's **datasource proxy**. A new `Admin.Host.Trace` namespace holds the pure `SpanRecogniser` (a table of patterns from span attributes to a `TraceEventKind`), `EventTraceService` (the three-step join of §5.9) and `TraceEndpoints` (`GET /api/trace/{correlationId}`). `BrokerService` is reused unchanged for step 3. FakePlatform answers Grafana from two recorded fixtures. The SPA gets typed client calls, a Trace screen at `/trace` and `/trace/:correlationId`, and a "Trace this call" button on the API screen's response pane.

**Tech Stack:** as phase 4 — .NET SDK 10.0.302, C# 14, minimal APIs, `System.Text.Json` (no new packages), xunit.v3, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`; Angular 22.1.x, Vitest via `ng test`, Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` — §2.2 (Grafana's surface), §5.8 Telemetry, §5.9 EventTrace, §5.10 (`GET /trace/{correlationId}?window=15m`), §6 (Trace screen; "Trace this call"), §9, §10, §12 phase 5. The Prometheus golden-signal strip (`GET /telemetry/health`) is **out of scope**: §5.8 lists it under the Stack screen's health strip, which no phase has built, and phase 5's row in §12 names only `GrafanaClient`, `EventTraceService`, the Trace screen and "Trace this call". Phase 6 Scenario is out of scope.

---

## Measured facts (probed live on 2026-09-16 against the running platform; cite, do not restate elsewhere)

The backend's own source was read for the first two; everything else was measured against
`grafana/otel-lgtm` at `http://localhost:3000` with the collector at `:4318`, by pushing a
synthetic OTLP record and reading it back. **These measurements are the reason several
statements in spec §5.8 and §5.9 are amended in Task 6.**

**M1 — The correlation id.** `blueprint-backend/src/BuildingBlocks/Common.Web/CorrelationIdExtensions.cs`:
`public const string Header = "X-Correlation-Id"` (`:30`); the logging scope key is the literal
`CorrelationId` (`:119`, a `Microsoft.Extensions.Logging` `BeginScope` dictionary — **not** Serilog,
there is no Serilog in the backend); when the caller sends none it falls back to
`Activity.Current?.TraceId.ToString()` then a v7 GUID (`:73-75`); an id is adoptable only at
length 1–128 over `[A-Za-z0-9_-]` (`:151-163`); the id is written back on the response from
`OnStarting` (`:90-98`). `Admin.Host` already mirrors the header and the alphabet in
`Api/RequestProxy.cs:15-32` (`CorrelationHeader`, `IsAdoptable`).

**M2 — The outbox severs W3C trace context. The spec's stated join is wrong.**
Spec §5.9 says "MassTransit propagates W3C trace context through message headers, so the outbox
dispatch, the publish, the consume on the other service and the projection write are spans of the
same trace as the HTTP request that caused them." Measured against the backend source:

- `grep -rn -E "traceparent|ActivitySource|StartActivity" blueprint-backend/src --include=*.cs`
  returns **zero matches**. There is no custom `ActivitySource` in the backend at all; the only
  registered source is `.AddSource("MassTransit")` (`Common.Web/ObservabilityExtensions.cs:136`).
- `Common.Infrastructure/Outbox/OutboxMessage.cs:40-60` — the row's columns are `Id`, `MessageId`,
  `CorrelationId` (a **`Guid`**), `MessageType`, `Payload`, `Lane`, `OccurredAt`, `ProcessedAt`,
  `Attempts`, `LastError`, `LockedUntil`. **There is no `traceparent` column.**
- `OutboxDispatcher` is a `BackgroundService` that polls and publishes (`:295-303`) without starting
  an Activity, so `Activity.Current` is null at publish time and MassTransit's send span is the
  **root of a new trace**.
- The message-level `Guid CorrelationId` is not the HTTP correlation id either: Catalog sets it to
  the product id (`Catalog.Application/Integration/CatalogIntegrationEventMapper.cs:57`).

So a correlation id reaches Loki lines for the HTTP request and its own trace, and **stops at the
outbox**. The consume side is a different trace that no key in this console can reach. This is why
§5.9's step 3 (the broker snapshot) is not a nicety but the only bridge, and why this plan renders
an explicit end-of-timeline marker rather than pretending the silence is the end of the story.

**M3 — Grafana needs no credential.** `deploy/compose/infrastructure.yml:140-142` is the whole
service definition — `grafana/otel-lgtm:latest`, `127.0.0.1:3000:3000`, **no environment block**.
Measured: `curl http://localhost:3000/api/datasources` unauthenticated returns **200**.
`GET /api/health` reports `"version": "13.1.1"`. The console still treats a 401/403 as an
unreachable state (§9), because the credential posture is the image's, not this repo's.

**M4 — Datasource uids.** `GET /api/datasources` returns four entries whose `uid` values are the
literals `loki`, `tempo`, `prometheus` and `pyroscope`, each with a matching `type`. The uids are
stable in this image but **are not provisioned by `blueprint-backend`** (it ships no
`provisioning/` at all), so they are resolved by `type` at first use and cached, as §5.8 requires —
never hard-coded.

**M5 — A log line in Loki carries `CorrelationId` as structured metadata; `| json` is wrong.**
Logs reach Loki by OTLP only (`ObservabilityExtensions.cs:33,52-64`: `ClearProviders()` then
`AddOpenTelemetry` with `IncludeFormattedMessage` and `IncludeScopes`), so the Loki **line body is
the formatted message**, not JSON. A synthetic OTLP log record carrying a `CorrelationId`
attribute came back from `query_range` as:

```json
{"stream":{"CorrelationId":"probe-corr-12345","RequestType":"GetProductsQuery",
  "detected_level":"info","scope_name":"Common.Web.CorrelationId","service_name":"Catalog.Api",
  "severity_number":"9","severity_text":"Information",
  "span_id":"00f067aa0ba902b7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736"},
 "values":[["1789532582000000000","probe: request start"]]}
```

So the query is `{service_name=~".+"} | CorrelationId = "<id>"` — a structured-metadata label
filter. Spec §5.8's `| json` stage is **measured to be wrong**: with it, Loki stamps
`__error__: "JSONParserErr"` and `__error_details__: "Value looks like object, but can't find
closing '}' symbol"` on every line. It happens not to drop the line today, so the filter still
matches, but it is a parse failure on every record and it poisons any later stage. Drop it.

Field spellings this plan depends on, all from the measurement above: `service_name`, `trace_id`,
`span_id`, `severity_text`, `CorrelationId`. A `values` entry is `[unixNanoString, line]`.

**M6 — Tempo returns OTLP-JSON with base64 ids.** `GET /api/traces/{traceIdHex}` through the
datasource proxy answers `{"batches":[{"resource":{"attributes":[…]},"scopeSpans":[{"scope":…,
"spans":[…]}]}]}`. **`traceId`, `spanId` and `parentSpanId` are base64, not hex** — the trace
`aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1` comes back as `qqqqqqqqqqqqqqqqqqqqoQ==` — so every id must be
base64-decoded to lowercase hex before it can be joined against Loki's `trace_id`. `parentSpanId`
is **absent** on a root span rather than empty. `kind` is the string form (`SPAN_KIND_SERVER`,
`SPAN_KIND_CLIENT`, `SPAN_KIND_PRODUCER`, `SPAN_KIND_CONSUMER`, `SPAN_KIND_INTERNAL`).
`startTimeUnixNano`/`endTimeUnixNano` are **strings**. Attribute values are tagged unions
(`stringValue`, `intValue`, `boolValue`, `doubleValue`); `intValue` is a string.

**M7 — Use the datasource proxy, not `/api/ds/query`.** Spec §5.8 says the console "issues
`POST /api/ds/query` requests". Measured: that endpoint works, but answers Grafana **data frames** —
a columnar `{"results":{"A":{"frames":[{"schema":{"fields":[…]},"data":{"values":[[…]]}}]}}}` whose
layout is announced by `meta.custom.frameType` (`LabeledTimeValues` for a Loki range query) and
belongs to Grafana's internal contract. The proxy path returns Loki's and Tempo's **own documented
response shapes** (M5, M6), and for a trace-by-id fetch there is no frame-free alternative at all.
This plan uses, on `AdminOptions.GrafanaUrl`:

- `GET /api/datasources`
- `GET /api/datasources/proxy/uid/{lokiUid}/loki/api/v1/query_range?query=…&start=…&end=…&limit=…`
  (`start`/`end` in **nanoseconds**)
- `GET /api/datasources/proxy/uid/{tempoUid}/api/traces/{traceIdHex}`

**M8 — Explore deep links.** Grafana 13.1.1 takes the pane form, measured to return 200:
`{grafanaUrl}/explore?schemaVersion=1&orgId=1&panes={"t":{"datasource":"<uid>","queries":[…],"range":{"from":"now-15m","to":"now"}}}`
with the `panes` value URL-encoded. A Loki query is `{"refId":"A","expr":"<logql>"}`; a Tempo
trace fetch is `{"refId":"A","queryType":"traceql","query":"<traceIdHex>"}`.

**M9 — Service names.** `service.name` is the entry assembly name — the literals are
`Gateway.Api`, `Catalog.Api`, `Ordering.Api`, `Web.Bff`
(`ObservabilityExtensions.cs:23`; restated as a warning in
`deploy/observability/alerts/platform-alerts.yaml:30-38`). The console never hard-codes them: it
groups by whatever `service_name` a line or span carries.

---

## Decisions this plan makes where the spec is silent, or where measurement overrode it

- **The Loki query drops `| json`** (M5) and the client queries through the datasource proxy rather
  than `/api/ds/query` (M7). Both are spec amendments, made in Task 6.
- **The timeline ends honestly at the outbox** (M2). `EventTraceService` appends one terminal
  event of kind `Queued` carrying the projection queue's current depth, and the SPA renders the
  reason in words: the broker hop starts a new trace that this correlation id cannot reach. This
  is the §5.9 step-3 snapshot doing the job §5.9 step 2 claimed the trace id would do. **A backend
  issue is raised, not a backend change** — CLAUDE.md forbids editing `../blueprint-backend`, and
  the fix there would be a `traceparent` column on the outbox row.
- **The correlation id is validated against `RequestProxy.IsAdoptable` before it reaches LogQL.**
  The id is interpolated into a query string, so this is the injection boundary, not a nicety: the
  `[A-Za-z0-9_-]` alphabet (M1) contains no quote, brace, backslash or pipe, so a validated id
  cannot close the string literal or add a stage. An id that fails validation is a **400 problem
  detail**, never a query. `IsAdoptable` moves to a shared home in Task 2 so both callers use one
  copy rather than a second table drifting from the first.
- **The window is a `[1m, 24h]` bound with a 15-minute default**, parsed from the `window` query
  string in the `15m`/`2h`/`90s` form §5.10 uses. Out-of-range or unparseable is a 400. The cap
  exists because the window becomes Loki's `start`, and an unbounded range on a workstation's
  retention is a slow query, not a useful one.
- **At most 10 traces are fetched from Tempo per build**, newest first, and the view says when it
  truncated. A correlation id reused across many requests (the fallback at M1 makes ids unique in
  practice, but a caller may send anything) must not turn one screen into a hundred fetches.
- **Tempo is queried once per distinct trace id, concurrently**, with the same per-call timeout the
  other host clients use. A trace that fails to fetch is a missing trace, not a failed build: its
  Loki lines still render.
- **`Kind` is decided by a pattern table over span attributes, not by span name** (except as a last
  resort), because §5.9 promises "a renamed span is a one-line change". The table is
  `SpanRecogniser.Table`, a `static readonly` array of rules, and it is tested directly.
  `Outbox` is recognised from a database span whose statement or collection names the outbox
  table — the backend starts no outbox span of its own (M2), so this is the only honest source.
- **Grafana that does not answer is a state** (§9): `TraceView` carries `reachable` and `error`
  like `QueuesView` does, and the screen keeps the last good timeline on screen behind the error,
  as the Broker screen keeps its last queues.
- **"Trace this call" appears on the `responded` and `unreached` outcomes only.** On
  `tokenRejected` nothing was sent (`Api/ProxyContracts.cs`), so there is nothing to trace and a
  button there would be a lie.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`. `TreatWarningsAsErrors`; IDE0055,
  IDE0065, IDE0161 fail the build. **No column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or editing `.cs` files and
  before every `dotnet build`/`dotnet test`, run `dotnet format whitespace BlueprintAdmin.slnx`
  from the repo root. Run `dotnet format BlueprintAdmin.slnx --verify-no-changes` before each commit.
- Test names are sentences with underscores.
- The host listens on `127.0.0.1:5300` only; never add a listener, CORS header or credential store
  (spec §8).
- Nothing is invented that the backend or Grafana owns: every hard-coded fact carries a comment
  naming its owner file and symbol, or the measurement above.
- FakePlatform is first-class: the trace endpoint works against the fakes and the Playwright smoke
  covers the Trace screen.
- The repository root is the worktree `C:\dev\ashamray\blueprint-admin-phase-5-trace`. All `dotnet`
  commands run from it; all `npm` commands from `src/Admin.Web` inside it. Before claiming a task
  done: `dotnet test BlueprintAdmin.slnx` green, and for SPA tasks `npm run lint`, `npm test`,
  `npm run build` green.
- Commit messages use `feat:`/`fix:`/`test:`/`docs:` prefixes and end with the attribution trailer
  the session provides. Before every commit, `git branch --show-current` must print
  `feat/phase-5-trace`.
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Telemetry/GrafanaContracts.cs               Task 1  wire records
src/Admin.Host/Telemetry/GrafanaClient.cs                  Task 1  uids + Loki + Tempo
tests/Admin.Host.Tests/Telemetry/GrafanaClientTests.cs     Task 1
src/Admin.Host/Trace/TraceContracts.cs                     Task 2  TraceEvent, TraceView
src/Admin.Host/Trace/SpanRecogniser.cs                     Task 2  the pattern table
src/Admin.Host/Trace/TraceWindow.cs                        Task 2  window parsing
src/Admin.Host/Trace/EventTraceService.cs                  Task 2  the three-step join
tests/Admin.Host.Tests/Trace/SpanRecogniserTests.cs        Task 2
tests/Admin.Host.Tests/Trace/TraceWindowTests.cs           Task 2
tests/Admin.Host.Tests/Trace/EventTraceServiceTests.cs     Task 2
src/Admin.Host/Trace/TraceEndpoints.cs                     Task 3
src/Admin.Host/Fakes/FakeGrafana.cs                        Task 3
src/Admin.Host/Fakes/fixtures/grafana-datasources.json     Task 3
src/Admin.Host/Fakes/fixtures/grafana-loki-query.json      Task 3
src/Admin.Host/Fakes/fixtures/grafana-tempo-trace.json     Task 3
src/Admin.Host/Fakes/FakePlatformHandler.cs                Task 3  Grafana branches
src/Admin.Host/Admin.Host.csproj                           Task 3  embed fixtures
src/Admin.Host/Program.cs                                  Task 3  register + MapTrace
tests/Admin.Host.Tests/Trace/TraceEndpointTests.cs         Task 3
src/Admin.Web/src/app/core/host/host-types.ts              Task 4
src/Admin.Web/src/app/core/host/host-client.ts(+spec)      Task 4
src/Admin.Web/src/app/features/trace/trace-page.{ts,html,css,spec.ts}  Task 4
src/Admin.Web/src/app/app.routes.ts, app.html, app.spec.ts Task 4  routes + nav
src/Admin.Web/src/app/features/api/api-page.{ts,html,css,spec.ts}      Task 5
src/Admin.Web/e2e/trace.spec.ts, e2e/api.spec.ts           Task 6
README.md, CLAUDE.md, spec §5.8/§5.9/§5.10                 Task 6
```

## Task 0 (controller, not a subagent): branch

- [x] `git worktree add ../blueprint-admin-phase-5-trace -b feat/phase-5-trace main` at `f55d516`;
  `npm ci` in `src/Admin.Web`; commit this plan:
  `git add docs/superpowers/plans/2026-09-16-blueprint-admin-phase-5-trace.md && git commit -m "docs: phase 5 trace implementation plan"`.

---

### Task 1: GrafanaClient — datasource uids, a Loki range query and a Tempo trace

**Files:**
- Create: `src/Admin.Host/Telemetry/GrafanaContracts.cs`, `src/Admin.Host/Telemetry/GrafanaClient.cs`
- Test: `tests/Admin.Host.Tests/Telemetry/GrafanaClientTests.cs`

**Interfaces:**
- Consumes: `HttpClient` (the named `"platform"` client), `IOptions<AdminOptions>` (`GrafanaUrl`),
  `TimeProvider`.
- Produces (all `namespace Admin.Host.Telemetry`; JSON is camelCase by the host's defaults):
  - `public sealed record DatasourceUids(string? Loki, string? Tempo, string? Prometheus)`
  - `public sealed record LokiLine(DateTimeOffset At, string Service, string? Level, string Message, string? TraceId)`
  - `public sealed record LokiResult(bool Reachable, string? Error, IReadOnlyList<LokiLine> Lines)`
  - `public sealed record SpanAttribute(string Key, string Value)`
  - `public sealed record TempoSpan(string TraceId, string SpanId, string? ParentSpanId, string Service, string Name, string Kind, DateTimeOffset At, TimeSpan Duration, IReadOnlyDictionary<string, string> Attributes, bool Failed)`
  - `public sealed record TempoResult(bool Reachable, string? Error, IReadOnlyList<TempoSpan> Spans)`
  - `public sealed class GrafanaClient(HttpClient http, IOptions<AdminOptions> options, TimeProvider time)` with
    `Task<DatasourceUids> UidsAsync(CancellationToken)` (cached after the first success),
    `Task<LokiResult> QueryAsync(string logQl, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken)`,
    `Task<TempoResult> TraceAsync(string traceIdHex, CancellationToken)`,
    and `public static string Hex(ReadOnlySpan<byte> bytes)` / `internal static string? FromBase64(string?)`.

**Design notes for the implementer:**
- Follow `Identity/TokenService.cs` exactly for the outbound shape: read `options.Value.GrafanaUrl`
  **at call time**, `TrimEnd('/')`, compose, `Uri.TryCreate(..., UriKind.Absolute)` plus an
  http/https scheme check, a linked `CancellationTokenSource` with `CancelAfter`, and
  `catch (HttpRequestException or JsonException or OperationCanceledException when (!cancellationToken.IsCancellationRequested))`
  turning every failure into `Reachable: false` with the message. Never throw to the endpoint.
- The uid cache is a field guarded so that a failed resolve is **not** cached — a Grafana that was
  down at first use must be retried. Cache only a result where at least `Loki` is non-null.
- `start`/`end` on the Loki query are **nanoseconds** (M7):
  `from.ToUnixTimeMilliseconds() * 1_000_000L`.
- Loki's `values` entries are `[unixNanoString, line]`; parse the first element as `long` and build
  the timestamp with `DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000)`.
- Tempo ids are base64 (M6): decode with `Convert.FromBase64String` then `Convert.ToHexStringLower`.
  A malformed id is skipped, not fatal.
- `Failed` is `status.code == "STATUS_CODE_ERROR"` on the span, defaulting to false when `status` is
  absent or `{}`.

- [ ] **Step 1: Write the failing tests**

`tests/Admin.Host.Tests/Telemetry/GrafanaClientTests.cs` — the shape to write, with
`ScriptedHandler` from `TestSupport` and `Options.Create(new AdminOptions())`:

```csharp
using System.Net;
using Admin.Host.Config;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Telemetry;

public sealed class GrafanaClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));

    private GrafanaClient Client(ScriptedHandler handler, AdminOptions? options = null) =>
        new(new HttpClient(handler), Options.Create(options ?? new AdminOptions()), time);

    // Measured 2026-09-16: GET /api/datasources on grafana/otel-lgtm, trimmed to the fields read.
    private const string Datasources = """
        [{"id":3,"uid":"loki","name":"Loki","type":"loki"},
         {"id":1,"uid":"prometheus","name":"Prometheus","type":"prometheus"},
         {"id":2,"uid":"tempo","name":"Tempo","type":"tempo"}]
        """;

    [Fact]
    public async Task The_datasource_uids_are_resolved_by_type_and_read_once()
    {
        ScriptedHandler handler = new(_ => FakeJson(Datasources));
        GrafanaClient client = Client(handler);

        DatasourceUids first = await client.UidsAsync(Token);
        await client.UidsAsync(Token);

        first.Loki.ShouldBe("loki");
        first.Tempo.ShouldBe("tempo");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Request.RequestUri!.ToString().ShouldBe("http://localhost:3000/api/datasources");
    }

    [Fact]
    public async Task A_grafana_that_does_not_answer_is_a_state_and_is_not_cached()
    {
        // ... first call throws HttpRequestException, second returns Datasources;
        //     the first result is Loki: null, the second resolves. Two requests are made.
    }

    [Fact]
    public async Task A_loki_range_query_is_sent_through_the_datasource_proxy_with_nanosecond_bounds()
    {
        // Assert the URI is
        // http://localhost:3000/api/datasources/proxy/uid/loki/loki/api/v1/query_range
        // and that query, start, end and limit are the escaped expression and nanosecond bounds.
    }

    [Fact]
    public async Task Loki_lines_carry_the_service_level_message_and_trace_id()
    {
        // Feed the measured stream shape (M5) and assert one LokiLine with
        // Service "Catalog.Api", Level "Information", TraceId "4bf92f35...", and the body as Message.
    }

    [Fact]
    public async Task A_tempo_trace_decodes_base64_ids_to_hex_and_names_each_span_service()
    {
        // Feed the measured batches shape (M6) and assert TraceId/SpanId/ParentSpanId are hex,
        // that a root span has ParentSpanId null, and that Service comes from resource service.name.
    }
}
```

Fixtures for the last two tests are the measured payloads in M5 and M6; copy them verbatim into
the test file as raw string literals, with a comment saying they were measured on 2026-09-16.

- [ ] **Step 2: Implement `GrafanaContracts.cs` and `GrafanaClient.cs` until the tests pass**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter FullyQualifiedName~Telemetry`
Expected: green.

- [ ] **Step 3: Full verification and commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes && dotnet test BlueprintAdmin.slnx
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Host/Telemetry tests/Admin.Host.Tests/Telemetry
git commit -m "feat(host): GrafanaClient reads datasource uids, Loki ranges and Tempo traces"
```

---

### Task 2: EventTraceService — the recogniser, the window and the three-step join

**Files:**
- Create: `src/Admin.Host/Trace/TraceContracts.cs`, `src/Admin.Host/Trace/SpanRecogniser.cs`,
  `src/Admin.Host/Trace/TraceWindow.cs`, `src/Admin.Host/Trace/EventTraceService.cs`
- Modify: `src/Admin.Host/Api/RequestProxy.cs` (move `IsAdoptable`/`CorrelationHeader` to a shared
  home and forward, so there is one copy — see below)
- Test: `tests/Admin.Host.Tests/Trace/SpanRecogniserTests.cs`,
  `tests/Admin.Host.Tests/Trace/TraceWindowTests.cs`,
  `tests/Admin.Host.Tests/Trace/EventTraceServiceTests.cs`

**Interfaces** (all `namespace Admin.Host.Trace`):
- `public enum TraceEventKind { HttpIn, Log, Span, Outbox, Publish, Consume, Queued, Error }`
- `public sealed record TraceEvent(DateTimeOffset At, string Source, string Service, TraceEventKind Kind, string Summary, string? TraceId, string? Link)`
- `public sealed record TraceView(string CorrelationId, string Window, bool Reachable, string? Error, IReadOnlyList<string> TraceIds, bool TracesTruncated, IReadOnlyList<TraceEvent> Events)`
- `public static class SpanRecogniser` — `public static TraceEventKind Kind(TempoSpan span)` and the
  `static readonly` rule table it walks.
- `public static class TraceWindow` — `public static bool TryParse(string? text, out TimeSpan window, out string? error)`,
  `public const string Default = "15m"`, `Min = 1 minute`, `Max = 24 hours`.
- `public sealed class EventTraceService(GrafanaClient grafana, BrokerService broker, IOptions<AdminOptions> options, TimeProvider time)`
  with `public const int MaxTraces = 10`, `public const int MaxLines = 1000`,
  `public static string LogQl(string correlationId)`,
  `public Task<TraceView> BuildAsync(string correlationId, TimeSpan window, CancellationToken)`.

**The one-copy rule for `IsAdoptable`.** `Api/RequestProxy.cs:15-32` already owns
`CorrelationHeader` and `IsAdoptable`, citing `Common.Web.CorrelationIdExtensions`. Two callers now
need it, so move both members to `src/Admin.Host/Api/CorrelationId.cs`
(`public static class CorrelationId`) and leave `RequestProxy` referring to it. Do **not**
copy the alphabet into `Trace/`; a second table is exactly the drift the citation comment exists to
prevent. Update `RequestProxyTests` references accordingly.

**`LogQl`** is, with the id already validated by the caller:

```csharp
// Measured 2026-09-16 (plan M5): CorrelationId is Loki structured metadata, the line body is the
// formatted message, and a `| json` stage stamps __error__ on every record. Owner of the scope key:
// blueprint-backend Common.Web.CorrelationIdExtensions (the literal "CorrelationId").
public static string LogQl(string correlationId) =>
    $$"""{service_name=~".+"} | CorrelationId = "{{correlationId}}"""";
```

**`BuildAsync` steps** (§5.9, as amended by M2):

1. `grafana.QueryAsync(LogQl(id), now - window, now, MaxLines, ct)`. Not reachable ⇒ return a
   `TraceView` with `Reachable: false`, the error, and no events.
2. Distinct non-null `TraceId`s from those lines, newest first, capped at `MaxTraces`
   (`TracesTruncated` records whether the cap bit). Fetch each with `grafana.TraceAsync`
   concurrently (`Task.WhenAll`); a trace that fails contributes nothing.
3. `broker.QueuesAsync(ct)` for the projection snapshot.
4. Build events: one `Log` (or `Error`, when `Level` is `Error`/`Critical`) per Loki line; one event
   per span with `SpanRecogniser.Kind`; then **one terminal `Queued` event** carrying the projection
   queue's state. Order by `At`, then by `Kind` so an `HttpIn` precedes the `Log`s it scopes.
5. Every event gets a `Link` built by `ExploreLink` (M8) — Loki events link to the correlation-id
   query, span events link to their trace in Tempo.

**The terminal event is the honest end of the timeline (M2).** Its summary states the projection
queue, its depth, and that the broker hop runs in a trace this correlation id cannot reach. Word it
so the Trace screen can render it verbatim, for example:
`ordering-catalog-events: 0 messages (drained). The publish runs in a new trace — the outbox carries no trace context, so the consume side is not joinable by this correlation id.`

- [ ] **Step 1: Write the failing recogniser tests**

`tests/Admin.Host.Tests/Trace/SpanRecogniserTests.cs` — a `[Theory]` table is the right shape here,
one row per rule, each built from a small `Span(name, kind, attrs)` helper:

| Span | Expected |
|---|---|
| `SPAN_KIND_SERVER` with `http.route` | `HttpIn` |
| `messaging.operation = "publish"` | `Publish` |
| `messaging.operation = "receive"` or `"process"` | `Consume` |
| `SPAN_KIND_CONSUMER` with `messaging.system` | `Consume` |
| `db.system` present and the statement or collection names `OutboxMessages` | `Outbox` |
| any span whose `Failed` is true | `Error` (and it wins over the others) |
| anything else | `Span` |

Assert also that the table is walked in order and that an unrecognised span is `Span`, never an
exception — §5.9 requires an unknown span to still be shown.

- [ ] **Step 2: Write the failing window tests**

`15m` ⇒ 15 minutes; `2h` ⇒ 2 hours; `90s` ⇒ 90 seconds; `null`/empty ⇒ the 15-minute default;
`0m`, `48h`, `banana`, `-5m`, `15` ⇒ false with an error naming the accepted form.

- [ ] **Step 3: Write the failing service tests**

`tests/Admin.Host.Tests/Trace/EventTraceServiceTests.cs`, against a `GrafanaClient` built on
`ScriptedHandler` (route by `request.RequestUri.AbsolutePath`) and a `BrokerService` on
`FakeProcessRunner` as `BrokerServiceTests` does. Cover:

- the LogQL sent is `{service_name=~".+"} | CorrelationId = "abc-123"` with **no `| json` stage**;
- lines and spans merge into one list ordered by time;
- a span with `http.route` on a server span becomes `HttpIn`, a publish span becomes `Publish`;
- the timeline's **last** event is the `Queued` projection snapshot, even when a span is later;
- a Loki that does not answer gives `Reachable: false` and no events;
- a Tempo that fails for one trace still yields that trace's Loki lines;
- more than `MaxTraces` distinct trace ids sets `TracesTruncated` and fetches exactly `MaxTraces`;
- an `Error`-level line becomes kind `Error`.

- [ ] **Step 4: Implement until green, then commit**

```bash
dotnet format whitespace BlueprintAdmin.slnx && dotnet format BlueprintAdmin.slnx --verify-no-changes && dotnet test BlueprintAdmin.slnx
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Host/Trace src/Admin.Host/Api tests/Admin.Host.Tests/Trace tests/Admin.Host.Tests/Api
git commit -m "feat(host): EventTraceService joins Loki lines, Tempo spans and the projection snapshot"
```

---

### Task 3: The endpoint, the Grafana fakes and the wiring

**Files:**
- Create: `src/Admin.Host/Trace/TraceEndpoints.cs`, `src/Admin.Host/Fakes/FakeGrafana.cs`,
  `src/Admin.Host/Fakes/fixtures/grafana-datasources.json`,
  `src/Admin.Host/Fakes/fixtures/grafana-loki-query.json`,
  `src/Admin.Host/Fakes/fixtures/grafana-tempo-trace.json`
- Modify: `src/Admin.Host/Fakes/FakePlatformHandler.cs`, `src/Admin.Host/Admin.Host.csproj`,
  `src/Admin.Host/Program.cs`
- Test: `tests/Admin.Host.Tests/Trace/TraceEndpointTests.cs`

**Interfaces:**
- `public static class TraceEndpoints` with `public static IEndpointRouteBuilder MapTrace(this IEndpointRouteBuilder app)`,
  mapping `GET /api/trace/{correlationId}`:
  - `correlationId` failing `CorrelationId.IsAdoptable` ⇒ `TypedResults.Problem(statusCode: 400, title: "Invalid correlation id", detail: …)`.
  - `window` failing `TraceWindow.TryParse` ⇒ 400 with the parser's error.
  - otherwise `TypedResults.Ok(await trace.BuildAsync(...))`.
  - Signature `Results<Ok<TraceView>, ProblemHttpResult>`, services by lambda parameter,
    `CancellationToken` last.
- `Program.cs`: `builder.Services.AddSingleton(sp => new GrafanaClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"), sp.GetRequiredService<IOptions<AdminOptions>>(), sp.GetRequiredService<TimeProvider>()));`
  and `builder.Services.AddSingleton<EventTraceService>();`, then `app.MapTrace();` appended to the
  `app.MapX()` list before the `/api/{**catchAll}` 404.

**The fakes.** `FakePlatformHandler` gains, before its `Is(uri, options.GatewayUrl)` branch — note
the existing `path == "/api/health"` branch already answers Grafana's health and must keep working:

```csharp
: Is(uri, options.GrafanaUrl) && path == "/api/datasources" ? FakeGrafana.Datasources()
: Is(uri, options.GrafanaUrl) && path.Contains("/loki/api/v1/query_range", StringComparison.Ordinal) ? FakeGrafana.Loki(request)
: Is(uri, options.GrafanaUrl) && path.Contains("/api/traces/", StringComparison.Ordinal) ? FakeGrafana.Tempo(request)
```

`FakeGrafana` is an `internal static class` in the `FakeKeycloak` mould, streaming the three
embedded fixtures. Its XML doc cites this plan's M5/M6 as what the recordings mirror.

**The fixtures must tell a coherent story**, because the Playwright smoke asserts on them and a
reader meets the console through them. Record them as one publish of a product:

- `grafana-datasources.json` — the measured four entries, trimmed to `id`, `uid`, `name`, `type`.
- `grafana-loki-query.json` — the measured `streams` envelope with **six lines** for correlation id
  `demo-trace-0001`: a gateway request-start line, two `Catalog.Api` lines (one of them the
  outbox write), a `Gateway.Api` response line, one `Warning`, and one `Error` line, all carrying
  `trace_id` `4bf92f3577b34da6a3ce929d0e0e4736` except the error line, which carries a second
  trace id so the truncation and multi-trace paths are exercised.
- `grafana-tempo-trace.json` — the measured `batches` envelope for that trace: a `Gateway.Api`
  `SPAN_KIND_SERVER` span with `http.route`, a `Catalog.Api` server span and a `Catalog.Api`
  `SPAN_KIND_CLIENT` db span naming `OutboxMessages` — so the recogniser returns `HttpIn`, `HttpIn`
  and `Outbox`, and the smoke can assert one of each.
  **Corrected during execution (ruling R11): this bullet originally also asked for a publish span
  with `messaging.operation = "publish"`. It cannot exist.** M2 measured that the outbox dispatcher
  publishes in a *new* trace, so no publish span can appear in the HTTP request's trace, and a
  recording showing one would contradict the terminal event rendered directly beneath it on the same
  screen. `SpanRecogniser`'s `Publish` and `Consume` rules stay covered by their unit tests.

`FakeGrafana.Loki` should answer the *same* fixture whatever the query, but **echo the requested
correlation id** into the returned `CorrelationId` metadata so the screen shows the id the user
typed; `FakeGrafana.Tempo` answers the trace fixture for any id and rewrites its `traceId` to the
requested one. Keep both rewrites to a documented `string.Replace` of the fixture's placeholder —
the fixture is a recording, and the rewrite is what makes it answer any id.

Register the three fixtures in `Admin.Host.csproj` beside the existing ones, flat `LogicalName`.

- [ ] **Step 1: Write the failing endpoint tests**

`tests/Admin.Host.Tests/Trace/TraceEndpointTests.cs`, `IClassFixture<AdminHostFactory>`, asserting
on raw `JsonElement` so the wire names are tested:

- `GET /api/trace/demo-trace-0001` ⇒ 200, `reachable` true, `events` non-empty, the last event's
  `kind` is `Queued`, and at least one event of each of `HttpIn` and `Outbox` — and **no** `Publish`,
  per R11 above;
- `correlationId` of `a"b` (and one of 200 characters) ⇒ 400 problem details;
- `?window=banana` ⇒ 400; `?window=2h` ⇒ 200;
- the default window appears in the view as `15m`.

- [ ] **Step 2: Implement until green**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx`
Expected: green, including the existing suites.

- [ ] **Step 3: Commit**

```bash
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Host tests/Admin.Host.Tests/Trace
git commit -m "feat(host): GET /api/trace/{correlationId} with the fake Grafana recordings"
```

---

### Task 4: The Trace screen

**Files:**
- Create: `src/Admin.Web/src/app/features/trace/trace-page.{ts,html,css,spec.ts}`
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`,
  `src/Admin.Web/src/app/core/host/host-client.ts` (+ `host-client.spec.ts`),
  `src/Admin.Web/src/app/app.routes.ts`, `src/Admin.Web/src/app/app.html`,
  `src/Admin.Web/src/app/app.spec.ts`

**Interfaces:**
- `host-types.ts`: `TraceEventKind` as a string union, `TraceEvent`, `TraceView` mirroring the C#
  records camelCase, `| null` for nullables, doc comments citing §5.9.
- `host-client.ts`: one thin method, no error handling —
  ```typescript
  trace(correlationId: string, window?: string): Observable<TraceView> {
    const params = window ? new HttpParams().set('window', window) : undefined;
    return this.http.get<TraceView>(`/api/trace/${encodeURIComponent(correlationId)}`, { params });
  }
  ```
- `app.routes.ts`: two lazy entries, `trace` and `trace/:correlationId`, both loading `TracePage`.
- `app.html`: `<a routerLink="/trace" routerLinkActive="active">Trace</a>` after the API link.

**The screen** (§6: "enter an id or arrive from the API screen"):
- An id input (`aria-label="Correlation id"`), a window select (`15m`, `1h`, `6h`, `24h`) and a
  `button.trace` labelled `Trace`. Submitting navigates to `/trace/<id>` so the URL is shareable
  and the back button works; the route param drives the load, not the button.
- On arriving with a param, load immediately. `signal<TraceView | null>` for the view,
  `loading`, and one `error` signal, filled by the standard `describeError` (copy the
  `broker-page.ts:113-116` form).
- **Keep the last good timeline on screen behind an error**, as the Broker screen does.
- Render the timeline as `table.timeline`, one row per event, with stable classes the tests query:
  `td.at`, `td.service`, `td.kind`, `td.summary`, and the row carrying `class="kind-<kind>"`.
  Group by service with a `th` per group, or carry the service in its own column — either satisfies
  §6's "grouped by service" as long as the service is visible per row and the order stays temporal.
- **State in words, not only colour** (the repo's rule): each row shows its kind as text
  (`[http]`, `[log]`, `[outbox]`, `[publish]`, `[consume]`, `[queued]`, `[error]`, `[span]`).
- Each row's `Link`, when present, is an `<a>` to Grafana Explore with
  `target="_blank" rel="noopener"` and an `aria-label` naming what it opens.
- `role="alert"` on the error paragraph, `role="status"` on the "n events over the last 15m" line.
- An empty result is its own state, not a blank table: say that no line carries that correlation id
  in the window, and that the id may be older than the window or never have reached the platform.
- The terminal `Queued` event is rendered with its summary verbatim — it is the sentence that
  explains why the timeline stops (M2).

- [ ] **Step 1: Write the failing component spec**

`trace-page.spec.ts` in the `broker-page.spec.ts` mould: module-level `TraceView` fixtures typed
with the wire interfaces, a `vi.fn()` `HostClient` double, `provideRouter([])` and a stubbed
`ActivatedRoute` param, a local `render()` helper, `rows(fixture, 'table.timeline')`. Cover: a
timeline renders a row per event in time order with the kind in words; the terminal queued row is
last and shows its sentence; an error shows in `role="alert"` and the previous timeline stays; an
empty result shows the explanatory empty state; submitting the form navigates to `/trace/<id>`.

- [ ] **Step 2: Implement until green**

Run (in `src/Admin.Web`): `npm run lint; npm test; npm run build`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Web/src
git commit -m "feat(web): the Trace screen renders a correlation id's timeline"
```

---

### Task 5: "Trace this call" on the API screen

**Files:**
- Modify: `src/Admin.Web/src/app/features/api/api-page.{ts,html,css,spec.ts}`

**Interfaces:**
- `ApiPage` gains `private readonly router = inject(Router)` and
  `traceThisCall(correlationId: string): void` navigating to `/trace/<id>`.
- The button goes beside the existing `<span class="correlation">correlation id {{ r.correlationId }}</span>`
  in `api-page.html` — in the `responded` and `unreached` `@case` blocks **only**, not
  `tokenRejected`, where nothing was sent.
- `button.trace-call`, labelled `Trace this call`, with an `aria-label` naming the id.
- History rows keep their `result.correlationId`, so the same button belongs on a history entry;
  add it there too if it costs one binding, otherwise leave history alone and say so in the commit.

- [ ] **Step 1: Extend the failing spec**

In `api-page.spec.ts`, add `provideRouter([])` to the TestBed and a `Router.navigate` spy. Assert:
after a `responded` result, `button.trace-call` exists and clicking it navigates to
`/trace/<the correlation id>`; after a `tokenRejected` result, `button.trace-call` does **not**
exist.

- [ ] **Step 2: Implement until green**

Run (in `src/Admin.Web`): `npm run lint; npm test; npm run build`
Expected: green, and the existing api-page specs still pass.

- [ ] **Step 3: Commit**

```bash
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Web/src/app/features/api
git commit -m "feat(web): Trace this call opens the response's correlation id on the Trace screen"
```

---

### Task 6: Playwright smoke and documents

**Files:**
- Create: `src/Admin.Web/e2e/trace.spec.ts`
- Modify: `src/Admin.Web/e2e/api.spec.ts`, `README.md`, `CLAUDE.md`,
  `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`

- [ ] **Step 1: The Trace smoke**

`src/Admin.Web/e2e/trace.spec.ts`, with a leading comment citing the fixtures the counts come from
(`src/Admin.Host/Fakes/fixtures/grafana-*.json`): navigate to `/trace`, type
`demo-trace-0001`, submit, and assert the URL became `/trace/demo-trace-0001`, that
`table.timeline tbody tr` has the fixture's row count, that `[http]` and `[outbox]` rows are
present and no `[publish]` row is (R11), that the last row is the `[queued]` projection sentence, and that a
Grafana Explore link is present with an `http://localhost:3000/explore?` href.

- [ ] **Step 2: The click-through from the API screen**

In `e2e/api.spec.ts`, after the existing `200` assertion, click `button.trace-call` and assert the
Trace screen opened on that response's correlation id and rendered a timeline — this is spec §10's
"a trace renders a timeline".

- [ ] **Step 3: Run the smoke**

Run (in `src/Admin.Web`): `npm run e2e`
Expected: every spec passes, `trace.spec.ts` included.

- [ ] **Step 4: Documents**

`README.md`:
- "What it does today": change "Phases 0 to 4" to "Phases 0 to 5"; add a sentence that the Trace
  screen joins Loki lines and Tempo spans for a correlation id into one timeline with Grafana
  deep links, and that the API screen's "Trace this call" opens it; remove
  "The event trace is the spec's next phase."
- "Known limits": replace "No event trace yet." with the honest limit from M2 — the timeline ends
  at the outbox, because the outbox row carries no trace context, so the publish and the consume on
  the other service run in a trace the correlation id cannot reach; the Trace screen shows the
  projection queue's depth instead. Add that the Prometheus golden-signal strip
  (`GET /telemetry/health`) is still unbuilt.

`CLAUDE.md`, "Owners of facts" paragraph — after the broker sentence, add that the Grafana query
shapes, the Loki structured-metadata field names and the Explore link form are copied, with their
measurement cited, in `src/Admin.Host/Telemetry/GrafanaClient.cs`, and that the span-to-kind table
is `SpanRecogniser.Table` in `src/Admin.Host/Trace/SpanRecogniser.cs`.

**Spec amendments** (the spec is amended where measurement overrode it; keep the rest):
- §5.8: replace the `POST /api/ds/query` sentence with the datasource-proxy calls of M7, and the
  Loki bullet's `| json |` pipeline with the structured-metadata filter of M5, noting that the line
  body is the formatted message because the backend exports logs over OTLP only.
- §5.8, the paragraph beginning "How the correlation id reaches telemetry": keep it, and correct
  its last clause — the Loki lines for a correlation id do carry `trace_id`, and those trace ids
  are what Tempo is asked for, but that reaches only the request's own trace.
- §5.9 step 2: replace the claim that the outbox dispatch, publish, consume and projection write
  "are spans of the same trace as the HTTP request" with M2 — the outbox carries no trace context,
  so the publish starts a new trace; the join reaches the request's own spans only.
- §5.9 step 3 and the paragraph after: say that the broker snapshot is therefore the timeline's
  terminal event and the only bridge across the hop.
- §5.10: change the `/trace/{correlationId}` row's "Does" cell to note the `[1m, 24h]` window bound
  and the 400 on an id outside the adoptable alphabet.

- [ ] **Step 5: Raise the backend gap**

The outbox's missing trace context is a backend fact this console cannot fix (CLAUDE.md). Write the
issue text into the PR body as a section titled "Raised for `blueprint-backend`", naming
`OutboxMessage` and `OutboxDispatcher`, the measurement, and the shape of a fix (persist
`traceparent` on the row at `Stage`, restore it into an `Activity` at dispatch). **Do not open the
issue from here** and do not edit the backend.

- [ ] **Step 6: Full verification**

Run: `dotnet format BlueprintAdmin.slnx --verify-no-changes; dotnet test BlueprintAdmin.slnx`;
in `src/Admin.Web`: `npm run lint; npm test; npm run build; npm run e2e`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git branch --show-current   # feat/phase-5-trace
git add src/Admin.Web/e2e README.md CLAUDE.md docs/superpowers/specs/2026-09-14-blueprint-admin-design.md
git commit -m "test(e2e): Trace screen smoke; docs: phase 5 in README, CLAUDE.md and spec §5.8, §5.9, §5.10"
```
