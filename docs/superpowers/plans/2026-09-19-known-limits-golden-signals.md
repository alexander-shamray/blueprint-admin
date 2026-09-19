# Known limits: golden signals, the rest of the row — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Stack screen's golden-signal strip shows every panel of the
backend's golden-signals dashboard per service — rate, errors and duration;
the 422 and 401 refusals; §13.7's command and query p95 — and a service that
served requests with no 5xx shows a 0 % share, not a dash. In the real mode
and in FakePlatform mode.

**Architecture:** `GoldenSignals` gains the four missing queries, copied
verbatim from the dashboard with `$service` at "All". `TelemetryHealthService`
runs seven instant queries instead of three and joins them per `service_name`.
It does the zero-filling itself, because the copied query text stays
untouched: a status code with no series is a zero rate for a service with a
request-rate series, the 5xx share is zero for a service with traffic, and the
422 panel's per-route series are summed per service. `FakeGrafana` answers
each query by exact text, and the strip on the Stack screen becomes a table.

**Tech Stack:** as phase 5 — .NET SDK 10.0.302, C# 14, minimal APIs,
`System.Text.Json` (no new packages), xunit.v3, Shouldly,
`Microsoft.AspNetCore.Mvc.Testing`; Angular 22.1.x, Vitest via `ng test`,
Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` —
§5.8 (the Prometheus bullet), §5.10 (`GET /telemetry/health`), §9, §10. The
limit lifted is `README.md`'s "Known limits" line on the golden-signal strip;
[the index](2026-09-19-known-limits-index.md) says why this one is lifted and
others are kept.

---

## Measured facts (read 2026-09-19; cite, do not restate elsewhere)

**G1 — The dashboard has three rows and seven query panels.**
`blueprint-backend/deploy/observability/dashboards/golden-signals.json`
(last changed at backend `56610db1`), read panel by panel:

| Row | Panel (id) | Grouped by | Unit |
|---|---|---|---|
| Rate, errors, duration | Request rate (2), Error ratio (5xx) (3), Latency p99 (4) | `service_name` | reqps, percentunit, s |
| Refusals — the failures no error graph shows | 422 — refused by the domain (6) | `service_name, http_route` | reqps |
| | 401 — rejected before the domain (7) | `service_name` | reqps |
| §13.7 — the two request rows | Command p95 (9), Query p95 (10) | `service_name` | s |

The first row is already in `GoldenSignals.cs`. The other four expressions
are copied in Task 2 exactly as the panels hold them, with
`service_name=~"$service"` written `service_name=~".+"` as the first three
already are.

**G2 — "§13.7" is the backend's starting SLOs.**
`blueprint-backend/docs/backend-architecture/13-observability.md`, `## 13.7
Starting SLOs`. The two panels read `request.duration` from
`Common.Application/RequestMetrics.cs` (meter `Commerce.Requests`), tagged
`request` with the request type's name. Panel 9's description says the
command/query split is by naming convention (`.+Command`, `.+Query`). Both
are per service, so both fit the strip. The targets live in the panel titles.
They are a backend fact the strip does not restate.

**G3 — Which absences are zeros.** The 5xx ratio is a division, and
Prometheus drops a service from a division whose numerator has no series, so
a service with traffic and no 5xx has no ratio series at all (the reason for
today's dash, `TelemetryHealthService`'s summary). A status-code rate query
has no series for a service whose counter never recorded that code. Neither
absence is unknown: both are zero, for a service the request-rate query says
exists. A quantile with no series or a `NaN` value is different: there were
no requests of that kind to rank, so it stays absent.

## Decisions this plan makes

- **Zero-filling happens in the join, not in PromQL.** An `or on
  (service_name) … * 0` would do it in the query, but the queries are
  *copied, cited* (CLAUDE.md owners table): a query that differs from its
  panel can no longer be checked by reading the two side by side. The join
  already owns "absent vs zero" (`TelemetryHealthService`), so the rule moves
  there with G3 as its argument.
- **The 422 panel's route split is summed per service, not shown.** The
  strip is one row per service. The sum of per-route rates is the
  per-service rate (rates add), so summing in the join keeps the query text
  verbatim and loses only the route, which Grafana shows. README's
  known-limits line says so.
- **All-or-nothing reachability stays.** Seven queries go through the same
  datasource proxy. A strip with a failed query's cells left empty would
  print the dash that means "no series" for "the query failed", two
  different answers made to look alike. One failure still makes the whole
  strip unavailable with the reason.
- **The strip becomes a table.** Seven values per service in a wrapping
  inline list cannot say which number is which. A table with column headers
  can. The Compose services table gets `class="services"`, and every test
  that counted `tbody tr` on the Stack screen names its table, as
  `broker.spec.ts` already does with `table.queues`.
- **Targets and thresholds are not shown.** They are the backend's
  (G2, and panels 3 and 4's descriptions); the strip prints measured values
  only.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`.
  `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No
  column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. After creating or editing `.cs` files and before
  every `dotnet build`/`dotnet test`, run
  `dotnet format whitespace BlueprintAdmin.slnx` from the repository root.
  Run `dotnet format BlueprintAdmin.slnx --verify-no-changes` before each
  commit.
- Test names are sentences with underscores. British spelling in prose and
  comments; identifiers keep their real spelling.
- A comment says why and cites the owner — no history, no PR named.
- Nothing is invented that the backend owns: every query is copied from a
  panel named in G1, and nothing in `../blueprint-backend` is edited.
- FakePlatform is first-class: the endpoint answers every query against the
  fakes, and the Playwright smoke covers the strip.
- `npm ci`, never `npm install`. No new packages on either side.
- Work happens on the branch `/branch` cuts, `feat(telemetry)/golden-signal-rows`,
  in its sibling worktree. All `dotnet` commands run from the worktree root;
  all `npm` commands from `src/Admin.Web` inside it.
- The PR's class is **C+D** (a rule moved in §5.8, plus README), with the
  touch set being the file structure below.
- Before claiming the plan done: `bash .claude/scripts/host-checks.sh all`
  and `bash .claude/scripts/npm-checks.sh all` green, and the Playwright
  smoke green.
- Commit messages are semantic and present-tense and end with the
  attribution trailer the session provides.

---

## File structure

```
src/Admin.Host/Telemetry/TelemetryHealthService.cs         Task 1, 2  the join
tests/Admin.Host.Tests/Telemetry/TelemetryHealthTests.cs   Task 1, 2
src/Admin.Host/Telemetry/GoldenSignals.cs                  Task 2     four copied queries
src/Admin.Host/Fakes/FakeGrafana.cs                        Task 2     exact-match recordings
src/Admin.Web/src/app/core/host/host-types.ts              Task 3
src/Admin.Web/src/app/features/stack/stack-page.{ts,html,css,spec.ts}  Task 3
src/Admin.Web/e2e/stack.spec.ts                            Task 3
docs/superpowers/specs/2026-09-14-blueprint-admin-design.md Task 4    §5.8
README.md                                                  Task 4
```

`GrafanaClient`, `GrafanaContracts`, `TelemetryEndpoints`, `Program.cs` and
`FakePlatformHandler.cs` do not change: `InstantQueryAsync` already reads any
instant vector's `service_name` and ignores other labels, and the fake's
`/api/v1/query` route already reaches `FakeGrafana.Prometheus`.

---

### Task 1: A 0 % share for a service with traffic and no 5xx

**Files:**
- Modify: `src/Admin.Host/Telemetry/TelemetryHealthService.cs`
- Test: `tests/Admin.Host.Tests/Telemetry/TelemetryHealthTests.cs`

**Interfaces:**
- Consumes: `GoldenSignals.RequestRate`, `.ErrorRatio`, `.LatencyP99`;
  `PrometheusResult`, `PrometheusSample` (unchanged).
- Produces: `ServiceSignals.ErrorRatio` is `0` where the service's
  `RequestRate > 0` and the ratio query had no series for it; `null` where
  the rate is `0` or absent. `private static double? ErrorRatioFor(PrometheusResult errors, string service, double? requestRate)`.

- [ ] **Step 1: Write the failing test**

Add to `TelemetryHealthTests`, after
`The_three_dashboard_queries_are_joined_per_service`. The query matching
follows that test's shape: the error ratio is matched first, because its
denominator is the request-rate query.

```csharp
    [Fact]
    public async Task A_service_with_traffic_and_no_5xx_series_has_a_zero_share_not_an_absent_one()
    {
        TelemetryHealthService health = Service(query =>
            query.Contains(GoldenSignals.ErrorRatio, StringComparison.Ordinal) ? FakeJson(Vector())
            : query.Contains(GoldenSignals.LatencyP99, StringComparison.Ordinal) ? FakeJson(Vector())
            : FakeJson(Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0"))));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // Catalog.Api served requests and answered no 5xx; Ordering.Api served none, so its share is undefined.
        view.Services.ShouldBe(
        [
            new ServiceSignals("Catalog.Api", 2, 0, null),
            new ServiceSignals("Ordering.Api", 0, null, null),
        ]);
    }
```

In `The_endpoint_answers_the_recordings_in_FakePlatform_mode`, replace

```csharp
        // Catalog.Api answered no 5xx, so the ratio has no series for it; Ordering.Api had no traffic.
        services[0].GetProperty("errorRatio").ValueKind.ShouldBe(JsonValueKind.Null);
```

with

```csharp
        // Catalog.Api served requests and answered no 5xx: a zero share, where the ratio itself has no series.
        services[0].GetProperty("errorRatio").GetDouble().ShouldBe(0);
        // Ordering.Api had no traffic, so its share is undefined.
        services[2].GetProperty("errorRatio").ValueKind.ShouldBe(JsonValueKind.Null);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter FullyQualifiedName~TelemetryHealthTests`
Expected: two failures —
`A_service_with_traffic_and_no_5xx_series_has_a_zero_share_not_an_absent_one`
(Catalog.Api's `ErrorRatio` is null, 0 expected) and
`The_endpoint_answers_the_recordings_in_FakePlatform_mode` (`errorRatio` is
`Null`, so `GetDouble` throws `InvalidOperationException`).

- [ ] **Step 3: Implement the rule in the join**

In `TelemetryHealthService.cs`, replace the class summary and the
`ValueFor(errors, service)` argument, and add `ErrorRatioFor` above
`ValueFor`:

```csharp
/// <summary>
/// Runs the three <see cref="GoldenSignals"/> queries and joins them on <c>service_name</c>. A
/// service with requests and no 5xx has no error-ratio series at all, because the division has no
/// numerator to match; its share is zero, and it is filled here rather than in the query, whose text
/// is the dashboard's.
/// </summary>
public sealed class TelemetryHealthService(GrafanaClient grafana)
```

```csharp
                .Select(service => new ServiceSignals(
                    service,
                    ValueFor(rate, service),
                    ErrorRatioFor(errors, service, ValueFor(rate, service)),
                    ValueFor(latency, service))),
```

```csharp
    /// <summary>
    /// The 5xx share, or zero where the service served requests and the ratio has no series for it. Over
    /// no requests the share is undefined, and stays absent.
    /// </summary>
    private static double? ErrorRatioFor(PrometheusResult errors, string service, double? requestRate)
    {
        // Presence, not value: ValueFor is null for a present sample that is not finite too, and a NaN
        // share is an unknown one, not a zero.
        if (errors.Samples.FirstOrDefault(s => s.Service == service) is { } sample)
        {
            return sample.Value;
        }

        return requestRate > 0 ? 0 : null;
    }
```

Also change the `ServiceSignals` summary's last sentence from "Each value is
null where Prometheus returned no series for the service, or a value that is
not a finite number." to "Each value is null where it is unknown: no series
for the service, a value that is not a finite number, or a 5xx share over no
requests."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter FullyQualifiedName~Telemetry`
Expected: PASS, every `Telemetry` test.

- [ ] **Step 5: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Telemetry/TelemetryHealthService.cs tests/Admin.Host.Tests/Telemetry/TelemetryHealthTests.cs
git commit -m "fix(telemetry): show a zero 5xx share for a service with traffic and no 5xx series"
```

Body: the division drops a service whose numerator has no series, so a dash
said "unknown" about a service that answered no 5xx. The join fills the zero
because the query text is the dashboard's (G3).

---

### Task 2: The refusals and §13.7 rows, joined per service

**Files:**
- Modify: `src/Admin.Host/Telemetry/GoldenSignals.cs`
- Modify: `src/Admin.Host/Telemetry/TelemetryHealthService.cs`
- Modify: `src/Admin.Host/Fakes/FakeGrafana.cs` (`Prometheus` only)
- Test: `tests/Admin.Host.Tests/Telemetry/TelemetryHealthTests.cs`

**Interfaces:**
- Consumes: `ErrorRatioFor` from Task 1.
- Produces:
  - `GoldenSignals.DomainRefusalRate`, `.UnauthorisedRate`, `.CommandP95`,
    `.QueryP95` (`public const string`).
  - `public sealed record ServiceSignals(string Service, double? RequestRate, double? ErrorRatio, double? LatencyP99Seconds, double? DomainRefusalRate, double? UnauthorisedRate, double? CommandP95Seconds, double? QueryP95Seconds)`.
    On the wire, camelCase: `domainRefusalRate`, `unauthorisedRate`,
    `commandP95Seconds`, `queryP95Seconds`. Task 3 reads these names.
  - FakePlatform answers, per service: Catalog.Api (rate 1.2, share 0, p99
    0.048, 422 0, 401 0.2, command 0.012, query 0.008); Gateway.Api (2.4,
    0.05, 0.09, 422 0.75 as 0.5 + 0.25 over two routes, 401 0, command null,
    query null); Ordering.Api (0, null, null, 0, 0, null, null). Task 3's
    e2e asserts these.

- [ ] **Step 1: Write the failing tests**

Replace `TelemetryHealthTests.cs` whole. The helpers now match a query by
its exact text: with seven queries, the 401 and 422 ones differ from the
request-rate one only inside the selector, and a substring match would
answer the wrong one.

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Telemetry;

public sealed class TelemetryHealthTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Datasources = """[{"uid":"loki","type":"loki"},{"uid":"tempo","type":"tempo"},{"uid":"prometheus","type":"prometheus"}]""";

    private static HttpResponseMessage FakeJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Vector(params string[] series) =>
        $$$"""{"status":"success","data":{"resultType":"vector","result":[{{{string.Join(',', series)}}}]}}""";

    private static string Series(string service, string value) =>
        $$$"""{"metric":{"service_name":"{{{service}}}"},"value":[1789532580,"{{{value}}}"]}""";

    private static string Series(string service, string route, string value) =>
        $$$"""{"metric":{"service_name":"{{{service}}}","http_route":"{{{route}}}"},"value":[1789532580,"{{{value}}}"]}""";

    private static TelemetryHealthService Service(Func<string, HttpResponseMessage> prometheus) =>
        new(new GrafanaClient(
            new HttpClient(new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/api/datasources"
                ? FakeJson(Datasources)
                : prometheus(PromQlIn(request)))),
            Options.Create(new AdminOptions())));

    /// <summary>The query as sent, whole: <c>GrafanaClient.InstantQueryAsync</c> escapes it into the one <c>query</c> parameter.</summary>
    private static string PromQlIn(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.Query["?query=".Length..]);

    /// <summary>Each query named answers its vector; any other answers an empty one, as Prometheus does for no series.</summary>
    private static Func<string, HttpResponseMessage> Answering(Dictionary<string, string> vectors) =>
        promQl => FakeJson(vectors.GetValueOrDefault(promQl) ?? Vector());

    [Fact]
    public async Task All_seven_dashboard_queries_are_joined_per_service()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.ErrorRatio] = Vector(Series("Catalog.Api", "0.1")),
            [GoldenSignals.LatencyP99] = Vector(Series("Catalog.Api", "0.05")),
            [GoldenSignals.DomainRefusalRate] = Vector(Series("Catalog.Api", "/v1/catalog/products/", "0.5")),
            [GoldenSignals.UnauthorisedRate] = Vector(Series("Catalog.Api", "0.25")),
            [GoldenSignals.CommandP95] = Vector(Series("Catalog.Api", "0.012")),
            [GoldenSignals.QueryP95] = Vector(Series("Catalog.Api", "0.008")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeTrue();
        view.Services.ShouldBe([new ServiceSignals("Catalog.Api", 2, 0.1, 0.05, 0.5, 0.25, 0.012, 0.008)]);
    }

    [Fact]
    public async Task The_422_panel_split_by_route_is_summed_per_service()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Gateway.Api", "3")),
            [GoldenSignals.DomainRefusalRate] = Vector(
                Series("Gateway.Api", "/api/v1/orders/", "0.5"),
                Series("Gateway.Api", "/api/v1/catalog/", "0.25")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Services.ShouldHaveSingleItem().DomainRefusalRate.ShouldBe(0.75);
    }

    [Fact]
    public async Task A_service_with_traffic_and_no_5xx_series_has_a_zero_share_not_an_absent_one()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // Catalog.Api served requests and answered no 5xx; Ordering.Api served none, so its share is undefined.
        view.Services.Select(s => s.ErrorRatio).ShouldBe([0, null]);
    }

    [Fact]
    public async Task A_5xx_share_that_is_present_but_not_finite_stays_absent_even_with_traffic()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.ErrorRatio] = Vector(Series("Catalog.Api", "NaN")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // The zero fill is for a series that is absent; a NaN one is present and unknown.
        view.Services.ShouldHaveSingleItem().ErrorRatio.ShouldBeNull();
    }

    [Fact]
    public async Task A_status_code_with_no_series_is_a_zero_rate_only_for_a_service_the_request_rate_names()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0")),
            [GoldenSignals.CommandP95] = Vector(Series("Worker.Api", "0.01")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // A counter that never recorded a 401 has no series: its rate is zero. Worker.Api is named only by
        // the command quantile, so nothing says it served HTTP at all.
        view.Services.Select(s => (s.Service, s.DomainRefusalRate, s.UnauthorisedRate)).ShouldBe<(string, double?, double?)>(
        [
            ("Catalog.Api", 0, 0),
            ("Ordering.Api", 0, 0),
            ("Worker.Api", null, null),
        ]);
    }

    [Fact]
    public async Task A_quantile_with_no_series_or_no_finite_value_stays_absent()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.LatencyP99] = Vector(Series("Catalog.Api", "NaN")),
            [GoldenSignals.CommandP95] = Vector(Series("Catalog.Api", "NaN")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        ServiceSignals catalog = view.Services.ShouldHaveSingleItem();
        catalog.LatencyP99Seconds.ShouldBeNull();
        catalog.CommandP95Seconds.ShouldBeNull();
        catalog.QueryP95Seconds.ShouldBeNull();
    }

    [Fact]
    public async Task One_query_that_fails_makes_the_strip_unreachable_rather_than_partial()
    {
        TelemetryHealthService health = Service(promQl => promQl == GoldenSignals.QueryP95
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream down") }
            : FakeJson(Vector(Series("Catalog.Api", "1"))));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("502");
        view.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_datasource_list_is_asked_once_per_read_not_once_per_query()
    {
        // GrafanaClient caches a uid only once resolved and does not gate a resolve in flight, so
        // seven cold queries fanned out together would each ask for the list; a Grafana with no
        // Prometheus, which never resolves, would be asked seven times on every poll.
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson("""[{"uid":"loki","type":"loki"}]""")
            : FakeJson(Vector()));
        TelemetryHealthService health = new(new GrafanaClient(new HttpClient(handler), Options.Create(new AdminOptions())));

        TelemetryHealthView first = await health.ReadAsync(Token);
        TelemetryHealthView second = await health.ReadAsync(Token);

        first.Reachable.ShouldBeFalse();
        second.Reachable.ShouldBeFalse();
        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/api/datasources").ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_endpoint_answers_the_recordings_in_FakePlatform_mode()
    {
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/health", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement[] services = [.. view.GetProperty("services").EnumerateArray()];
        services.Select(s => s.GetProperty("service").GetString()).ShouldBe(["Catalog.Api", "Gateway.Api", "Ordering.Api"]);

        // Catalog.Api served requests and answered no 5xx: a zero share, where the ratio itself has no series.
        services[0].GetProperty("errorRatio").GetDouble().ShouldBe(0);
        services[0].GetProperty("commandP95Seconds").GetDouble().ShouldBe(0.012);
        services[1].GetProperty("errorRatio").GetDouble().ShouldBe(0.05);
        // Gateway.Api's 422s are recorded on two routes and summed; it runs no commands or queries.
        services[1].GetProperty("domainRefusalRate").GetDouble().ShouldBe(0.75);
        services[1].GetProperty("queryP95Seconds").ValueKind.ShouldBe(JsonValueKind.Null);
        // Ordering.Api had no traffic: its share and quantiles are undefined, its refusals zero.
        services[2].GetProperty("errorRatio").ValueKind.ShouldBe(JsonValueKind.Null);
        services[2].GetProperty("latencyP99Seconds").ValueKind.ShouldBe(JsonValueKind.Null);
        services[2].GetProperty("unauthorisedRate").GetDouble().ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter FullyQualifiedName~TelemetryHealthTests`
Expected: the test project does not compile — `CS0117: 'GoldenSignals'
does not contain a definition for 'DomainRefusalRate'` (and for the other
three), and `ServiceSignals` has no eight-argument constructor.

- [ ] **Step 3: Copy the four queries**

Replace `GoldenSignals.cs` whole. The expressions are panels 6, 7, 9 and 10
of G1, character for character, with `"$service"` written `".+"`.

```csharp
namespace Admin.Host.Telemetry;

/// <summary>
/// The PromQL of every query panel in blueprint-backend's
/// <c>deploy/observability/dashboards/golden-signals.json</c> (spec §5.8), copied with the dashboard's
/// <c>$service</c> variable at "All". A changed panel there is a change here; the window each query
/// ranges over is the dashboard's, not this console's, and so are the thresholds and targets its titles
/// and descriptions carry.
/// </summary>
public static class GoldenSignals
{
    /// <summary>The "Request rate" panel, in requests per second.</summary>
    public const string RequestRate =
        """sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+"}[5m]))""";

    /// <summary>The "Error ratio (5xx)" panel, a fraction of the request rate.</summary>
    public const string ErrorRatio =
        """sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+", http_response_status_code=~"5.."}[5m])) / sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+"}[5m]))""";

    /// <summary>The "Latency p99" panel, in seconds.</summary>
    public const string LatencyP99 =
        """histogram_quantile(0.99, sum by (service_name, le) (rate(http_server_request_duration_seconds_bucket{service_name=~".+"}[10m])))""";

    /// <summary>
    /// The "422 — refused by the domain" panel, in requests per second. It is split by route as well as
    /// service; <see cref="TelemetryHealthService"/> sums the routes, because the strip is per service.
    /// </summary>
    public const string DomainRefusalRate =
        """sum by (service_name, http_route) (rate(http_server_request_duration_seconds_count{service_name=~".+", http_response_status_code="422"}[5m]))""";

    /// <summary>The "401 — rejected before the domain" panel, in requests per second.</summary>
    public const string UnauthorisedRate =
        """sum by (service_name) (rate(http_server_request_duration_seconds_count{service_name=~".+", http_response_status_code="401"}[5m]))""";

    /// <summary>
    /// The "Command p95" panel of the §13.7 row, in seconds: blueprint-backend's <c>RequestMetrics</c>, split
    /// from queries by the request type's name, as the panel's description says.
    /// </summary>
    public const string CommandP95 =
        """histogram_quantile(0.95, sum by (service_name, le) (rate(request_duration_seconds_bucket{service_name=~".+", request=~".+Command"}[5m])))""";

    /// <summary>The "Query p95" panel of the §13.7 row, in seconds.</summary>
    public const string QueryP95 =
        """histogram_quantile(0.95, sum by (service_name, le) (rate(request_duration_seconds_bucket{service_name=~".+", request=~".+Query"}[5m])))""";
}
```

Before moving on, diff each new string against its panel: open
`../blueprint-backend/deploy/observability/dashboards/golden-signals.json`,
take panel `targets[0].expr` for ids 6, 7, 9 and 10, replace `"$service"`
with `".+"`, and compare. They must be identical.

- [ ] **Step 4: Join seven queries**

Replace `TelemetryHealthService.cs` whole:

```csharp
namespace Admin.Host.Telemetry;

/// <summary>
/// One service's golden signals, as the dashboard's three rows show them: rate, errors and duration; the
/// 422 and 401 refusals; and §13.7's command and query p95. Rates are per second, durations in seconds.
/// Each value is null where it is unknown: a quantile with no series or a value that is not a finite
/// number, a 5xx share over no requests, or a refusal rate for a service the request-rate query does
/// not name.
/// </summary>
public sealed record ServiceSignals(
    string Service,
    double? RequestRate,
    double? ErrorRatio,
    double? LatencyP99Seconds,
    double? DomainRefusalRate,
    double? UnauthorisedRate,
    double? CommandP95Seconds,
    double? QueryP95Seconds);

/// <summary>The Stack screen's health strip (spec §5.8). Unreachable is a state, not an exception (spec §9).</summary>
public sealed record TelemetryHealthView(bool Reachable, string? Error, IReadOnlyList<ServiceSignals> Services);

/// <summary>
/// Runs the <see cref="GoldenSignals"/> queries and joins them on <c>service_name</c>. Where an absent
/// series means zero it is filled here rather than in the query, whose text is the dashboard's. One query
/// that fails makes the whole strip unreachable: a cell left empty by a failure would print the dash that
/// means "no series", and those are different answers.
/// </summary>
public sealed class TelemetryHealthService(GrafanaClient grafana)
{
    public async Task<TelemetryHealthView> ReadAsync(CancellationToken cancellationToken)
    {
        // The first query runs alone: it resolves Prometheus's uid, which GrafanaClient caches once
        // resolved but does not gate while resolving, so the six fanned out after it read the cache
        // rather than each asking /api/datasources. A Grafana with no Prometheus stops here, asked once.
        PrometheusResult first = await grafana.InstantQueryAsync(GoldenSignals.RequestRate, cancellationToken);

        if (!first.Reachable)
        {
            return new TelemetryHealthView(false, first.Error, []);
        }

        PrometheusResult[] rest = await Task.WhenAll(
            grafana.InstantQueryAsync(GoldenSignals.ErrorRatio, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.LatencyP99, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.DomainRefusalRate, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.UnauthorisedRate, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.CommandP95, cancellationToken),
            grafana.InstantQueryAsync(GoldenSignals.QueryP95, cancellationToken));
        PrometheusResult[] results = [first, .. rest];

        if (results.FirstOrDefault(r => !r.Reachable) is { } failed)
        {
            return new TelemetryHealthView(false, failed.Error, []);
        }

        ServiceSignals[] services =
        [
            .. results
                .SelectMany(r => r.Samples)
                .Select(s => s.Service)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(service => Join(results, service)),
        ];

        return new TelemetryHealthView(true, null, services);
    }

    /// <summary>One service's row; <paramref name="results"/> is in the order <see cref="ReadAsync"/> asks.</summary>
    private static ServiceSignals Join(PrometheusResult[] results, string service)
    {
        double? requestRate = ValueFor(results[0], service);

        return new ServiceSignals(
            service,
            requestRate,
            ErrorRatioFor(results[1], service, requestRate),
            ValueFor(results[2], service),
            StatusRateFor(results[3], service, requestRate),
            StatusRateFor(results[4], service, requestRate),
            ValueFor(results[5], service),
            ValueFor(results[6], service));
    }

    /// <summary>
    /// The 5xx share, or zero where the service served requests and the ratio has no series for it: the
    /// division drops a service whose numerator has no series. Over no requests the share is undefined,
    /// and stays absent.
    /// </summary>
    private static double? ErrorRatioFor(PrometheusResult errors, string service, double? requestRate)
    {
        // Presence, not value: ValueFor is null for a present sample that is not finite too, and a NaN
        // share is an unknown one, not a zero.
        if (errors.Samples.FirstOrDefault(s => s.Service == service) is { } sample)
        {
            return sample.Value;
        }

        return requestRate > 0 ? 0 : null;
    }

    /// <summary>
    /// One status code's rate, summed over the series the panel splits it into (the 422 panel is by route
    /// too; rates add). A counter that never recorded the code has no series, so for a service the
    /// request-rate query names, no series is zero. A rate is never <c>NaN</c>, so a sum has no unknown
    /// term to hide.
    /// </summary>
    private static double? StatusRateFor(PrometheusResult result, string service, double? requestRate)
    {
        PrometheusSample[] series = [.. result.Samples.Where(s => s.Service == service)];

        if (series.Length == 0)
        {
            return requestRate is null ? null : 0;
        }

        return series.Sum(s => s.Value);
    }

    private static double? ValueFor(PrometheusResult result, string service) =>
        result.Samples.FirstOrDefault(s => s.Service == service)?.Value;
}
```

- [ ] **Step 5: Answer every query in FakePlatform, by exact text**

In `FakeGrafana.cs`, replace the `Prometheus` method and its summary:

```csharp
    /// <summary>
    /// One recording per <see cref="Telemetry.GoldenSignals"/> query, chosen by the query's exact text:
    /// the 401 and 422 queries differ from the request-rate one only inside the selector, so a substring
    /// match would answer the wrong one. The shape is Prometheus's documented <c>/api/v1/query</c> vector,
    /// not a measurement through this Grafana, and the <c>http_route</c> values are the prefixes
    /// <c>GatewayRoutes</c> copies, not recorded routes. Ordering.Api has no traffic, so its ratio and
    /// quantiles are <c>NaN</c> or absent; Catalog.Api has no 5xx and no 422, so it has no series for
    /// either; Gateway.Api runs no commands or queries.
    /// </summary>
    public static HttpResponseMessage Prometheus(HttpRequestMessage request)
    {
        const string prefix = "?query=";
        string raw = request.RequestUri!.Query;
        string promQl = raw.StartsWith(prefix, StringComparison.Ordinal) ? Uri.UnescapeDataString(raw[prefix.Length..]) : "";

        string series = promQl switch
        {
            Telemetry.GoldenSignals.RequestRate => """
                {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"1.2"]},
                {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"2.4"]},
                {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"0"]}
                """,
            Telemetry.GoldenSignals.ErrorRatio => """
                {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"0.05"]},
                {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"NaN"]}
                """,
            Telemetry.GoldenSignals.LatencyP99 => """
                {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"0.048"]},
                {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"0.09"]},
                {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"NaN"]}
                """,
            Telemetry.GoldenSignals.DomainRefusalRate => """
                {"metric":{"service_name":"Gateway.Api","http_route":"/api/v1/orders/"},"value":[1789532580,"0.5"]},
                {"metric":{"service_name":"Gateway.Api","http_route":"/api/v1/catalog/"},"value":[1789532580,"0.25"]}
                """,
            Telemetry.GoldenSignals.UnauthorisedRate => """
                {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"0.2"]}
                """,
            Telemetry.GoldenSignals.CommandP95 => """
                {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"0.012"]},
                {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"NaN"]}
                """,
            Telemetry.GoldenSignals.QueryP95 => """
                {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"0.008"]}
                """,
            _ => "",
        };

        return FakeHttp.Json(HttpStatusCode.OK, $$$"""{"status":"success","data":{"resultType":"vector","result":[{{{series}}}]}}""");
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet format whitespace BlueprintAdmin.slnx; dotnet test BlueprintAdmin.slnx --filter FullyQualifiedName~Telemetry`
Expected: PASS, all seven `TelemetryHealthTests` and every `GrafanaClientTests` case.

Then the whole host suite, which also runs the other FakePlatform endpoints:

Run: `bash .claude/scripts/host-checks.sh all`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Telemetry/GoldenSignals.cs src/Admin.Host/Telemetry/TelemetryHealthService.cs src/Admin.Host/Fakes/FakeGrafana.cs tests/Admin.Host.Tests/Telemetry/TelemetryHealthTests.cs
git commit -m "feat(telemetry): read the dashboard's refusals and §13.7 rows into the strip"
```

Body: the four panels are copied verbatim (G1), and the join owns three
decisions, each argued in its summary:
- a status code with no series is zero for a service the request rate names;
- the 422 routes are summed;
- one failed query still makes the strip unavailable.

The fake now matches by exact text, because a substring match cannot tell
the 401 query from the request-rate one.

---

### Task 3: The strip as a table on the Stack screen

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`
- Modify: `src/Admin.Web/src/app/features/stack/stack-page.ts`,
  `stack-page.html`, `stack-page.css`, `stack-page.spec.ts`
- Test: `src/Admin.Web/e2e/stack.spec.ts`

**Interfaces:**
- Consumes: the wire shape Task 2 produces (camelCase `ServiceSignals`).
- Produces: `section.signals table` with one `thead` row and one `tbody tr`
  per service (`th[scope=row]` for the name, then seven `td`); the Compose
  services table is `table.services`.

- [ ] **Step 1: Write the failing unit tests**

In `stack-page.spec.ts`:

Replace the `health` fixture:

```typescript
const health = {
  reachable: true,
  error: null,
  services: [
    {
      service: 'Catalog.Api',
      requestRate: 1.2,
      errorRatio: 0,
      latencyP99Seconds: 0.048,
      domainRefusalRate: 0,
      unauthorisedRate: 0.2,
      commandP95Seconds: 0.012,
      queryP95Seconds: 0.008,
    },
    {
      service: 'Ordering.Api',
      requestRate: 0,
      errorRatio: null,
      latencyP99Seconds: null,
      domainRefusalRate: 0,
      unauthorisedRate: 0,
      commandP95Seconds: null,
      queryP95Seconds: null,
    },
  ],
};
```

Replace the test `'prints rate, 5xx share and p99 per service, with a dash where Prometheus had no value'` with:

```typescript
    it('prints one row per service across the dashboard rows, with a dash where the host had no value', async () => {
      const fixture = await render();

      expect(text(fixture.nativeElement.querySelector('.signals thead'))).toBe(
        'Service Requests 5xx p99 422 refused 401 rejected Command p95 Query p95',
      );
      const rows = Array.from(fixture.nativeElement.querySelectorAll('.signals tbody tr')) as HTMLElement[];
      expect(rows.map((r) => text(r))).toEqual([
        'Catalog.Api 1.20/s 0.0 % 48 ms 0.00/s 0.20/s 12 ms 8 ms',
        'Ordering.Api 0.00/s — — 0.00/s 0.00/s — —',
      ]);
    });
```

In `'polls on its own slower timer and recovers after a failed read'`, change
`querySelectorAll('.signals li')` to `querySelectorAll('.signals tbody tr')`.

Name the Compose table in every other query of that file: each
`querySelectorAll('tbody tr')` (in `'lists services with state, health and
ports, and the reachability strip'`, `'keeps polling after a failed
request'` and `'lets a slow poll finish instead of cancelling it on the next
tick'`) becomes `querySelectorAll('table.services tbody tr')`. Search for
`'tbody tr'` afterwards; no unqualified one may remain.

- [ ] **Step 2: Run the unit tests to verify they fail**

Run (in `src/Admin.Web`): `npm test`
Expected: FAIL. The new strip test finds no `.signals thead` (the strip is
still a `ul`), the poll test counts no `.signals tbody tr`, and the Compose
table tests find no `table.services`.

- [ ] **Step 3: Implement the types, the template, the formatters and the style**

`host-types.ts` — replace `ServiceSignals` and its comment:

```typescript
/**
 * One service's golden signals (spec §5.8): the dashboard's rate, errors and duration, its 422 and 401
 * refusals, and §13.7's command and query p95 — rates per second, durations in seconds. Null where the
 * host could not say: no series, no finite value, a 5xx share over no requests (the host's
 * `ServiceSignals`).
 */
export interface ServiceSignals {
  service: string;
  requestRate: number | null;
  errorRatio: number | null;
  latencyP99Seconds: number | null;
  domainRefusalRate: number | null;
  unauthorisedRate: number | null;
  commandP95Seconds: number | null;
  queryP95Seconds: number | null;
}
```

`stack-page.html` — replace the strip's `<ul>…</ul>` (inside the `@else`
branch of `section.signals`) with:

```html
        <table>
          <thead>
            <tr>
              <th scope="col">Service</th>
              <th scope="col">Requests</th>
              <th scope="col">5xx</th>
              <th scope="col">p99</th>
              <th scope="col">422 refused</th>
              <th scope="col">401 rejected</th>
              <th scope="col">Command p95</th>
              <th scope="col">Query p95</th>
            </tr>
          </thead>
          <tbody>
            @for (svc of h.services; track svc.service) {
              <tr>
                <th scope="row">{{ svc.service }}</th>
                <td>{{ rate(svc.requestRate) }}</td>
                <td>{{ ratio(svc.errorRatio) }}</td>
                <td>{{ latency(svc.latencyP99Seconds) }}</td>
                <td>{{ rate(svc.domainRefusalRate) }}</td>
                <td>{{ rate(svc.unauthorisedRate) }}</td>
                <td>{{ latency(svc.commandP95Seconds) }}</td>
                <td>{{ latency(svc.queryP95Seconds) }}</td>
              </tr>
            }
          </tbody>
        </table>
```

and give the Compose services table its class: `<table>` directly after the
`Docker did not answer` block becomes `<table class="services">`.

`stack-page.ts` — the poll's comment says "every read is three Prometheus
queries"; make it "seven". Replace the three formatters and their comment:

```typescript
  /** A rate per second, a share and a duration as the strip's cells print them; absent is a dash, not a zero. */
  rate(value: number | null): string {
    return value === null ? '—' : `${value.toFixed(2)}/s`;
  }

  ratio(value: number | null): string {
    return value === null ? '—' : `${(value * 100).toFixed(1)} %`;
  }

  latency(seconds: number | null): string {
    return seconds === null ? '—' : `${Math.round(seconds * 1000)} ms`;
  }
```

`stack-page.css` — replace the two list rules
(`.signals ul { … }` and `.signals li span { … }`) with:

```css
.signals td { text-align: right; font-variant-numeric: tabular-nums; }
.signals tbody th { font-weight: normal; text-align: left; }
```

(`.signals-empty` stays; the shared `table` and `td, th` rules already give
the strip its spacing.)

- [ ] **Step 4: Run lint, unit tests and the build**

Run (from the worktree root; the script `cd`s into `src/Admin.Web`):
`bash .claude/scripts/npm-checks.sh all`
Expected: lint clean, every Vitest spec PASS, build succeeds into
`src/Admin.Host/wwwroot`.

- [ ] **Step 5: Update the Playwright smoke**

In `e2e/stack.spec.ts`, name the Compose table in the first test:

```typescript
  await expect(page.locator('table.services tbody tr')).toHaveCount(13);
  await expect(page.locator('table.services tbody tr', { hasText: 'gateway' })).toContainText('healthy');
```

and replace the golden-signal test with:

```typescript
test('the stack screen shows the recorded golden signals per service', async ({ page }) => {
  await page.goto('/stack');
  const rows = page.getByRole('region', { name: 'Golden signals' }).locator('tbody tr');

  await expect(rows).toHaveCount(3);
  const gateway = rows.filter({ hasText: 'Gateway.Api' });
  await expect(gateway).toContainText('2.40/s');
  await expect(gateway).toContainText('5.0 %');
  await expect(gateway).toContainText('0.75/s');
  // Catalog.Api served requests and answered no 5xx: a zero share, not a dash.
  const catalog = rows.filter({ hasText: 'Catalog.Api' });
  await expect(catalog).toContainText('0.0 %');
  await expect(catalog).toContainText('48 ms');
  await expect(catalog).toContainText('12 ms');
});
```

- [ ] **Step 6: Run the Playwright smoke**

From the worktree root: `dotnet build BlueprintAdmin.slnx`; then in
`src/Admin.Web`: `npm run build` and `npx playwright test e2e/stack.spec.ts`.
The config starts the host with `--no-build` under FakePlatform, which is
why both builds come first.
Expected: every `stack.spec.ts` test PASS. Then `npm run e2e` for the whole
smoke: PASS. No other e2e file counts an unqualified `tbody tr` on `/stack`,
but the full run is the proof.

- [ ] **Step 7: Commit**

```bash
git add src/Admin.Web/src/app/core/host/host-types.ts src/Admin.Web/src/app/features/stack src/Admin.Web/e2e/stack.spec.ts
git commit -m "feat(stack): show the whole golden-signal row as a table"
```

Body: seven values per service in an inline list cannot say which number is
which, and a table with headers can. The Compose table is named `services`
so that every test counting its rows says which table it means, as
`broker.spec.ts` does.

---

### Task 4: Amend spec §5.8 and README

**Files:**
- Modify: `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` (§5.8)
- Modify: `README.md`

**Interfaces:** none. Prose only; wrap at 80 columns, British spelling.

- [ ] **Step 1: Amend §5.8's Prometheus bullet**

Replace:

```markdown
  screen's health strip. The strip is that dashboard's rate, errors and
  duration row, joined per `service_name`; `GoldenSignals` owns the copied
  queries. A value that is absent or not finite is shown as absent: a ratio
  with no 5xx has no series, and one over no requests is `NaN`.
```

with:

```markdown
  screen's health strip. The strip is every query panel of that dashboard —
  rate, errors and duration; the 422 and 401 refusals; the backend's §13.7
  command and query p95 — joined per `service_name`; `GoldenSignals` owns
  the copied queries, and their text is the panels'. The 422 panel is split
  by route too, and the strip sums it per service. Where an absent series
  means zero, the join says zero: a status code no counter recorded, for a
  service the request rate names, and the 5xx share of a service with
  traffic, which the ratio drops when its numerator has no series. A share
  over no requests, and a quantile with no series or a value that is not
  finite, are shown as absent. One query that fails makes the strip
  unavailable rather than partial.
```

- [ ] **Step 2: README — the screen and the known limit**

In "What it does today", replace:

```markdown
client's `npm start` with start, stop and its output; each service's request
rate, 5xx share and p99 latency from Prometheus, the backend golden-signals
dashboard's first row), the Logs screen
```

with:

```markdown
client's `npm start` with start, stop and its output; each service's request
rate, 5xx share, p99, 422 and 401 rates and command and query p95 from
Prometheus, every panel of the backend golden-signals dashboard), the Logs
screen
```

In "Known limits", replace:

```markdown
- The golden-signal strip shows the dashboard's rate, errors and duration row
  only, not its refusals or §13.7 rows. A service with no 5xx shows a dash
  for its 5xx share, not a zero: the ratio has no series for it.
```

with:

```markdown
- The golden-signal strip sums the dashboard's 422 panel per service; its
  split by route, and the panels' thresholds and targets, are in Grafana.
```

Re-wrap only the lines you changed, and check the paragraph around each
still reads.

- [ ] **Step 3: Check nothing else restates the old claim**

Run: `git grep -n -i "first row\|rate, errors and duration row\|dash for its 5xx" -- README.md CLAUDE.md docs/superpowers/specs src`
Expected: no hits. `docs/superpowers/plans/` is a frozen record and is not
searched or edited.

- [ ] **Step 4: Run every check**

Run: `bash .claude/scripts/host-checks.sh all` and
`bash .claude/scripts/npm-checks.sh all`
Expected: both PASS. The Playwright smoke was run in Task 3; CI's `smoke`
job runs it again.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-14-blueprint-admin-design.md README.md
git commit -m "docs: amend §5.8 and README for the whole golden-signal strip"
```

Body: §5.8 now states which absences the join fills as zero and why, and
that the query text stays the panels'. README's known-limits line shrinks to
what is still true: the 422 routes are summed, and the thresholds and
targets are Grafana's to show.
