using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Telemetry;
using Admin.Host.Trace;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Trace;

public sealed class EventTraceServiceTests : IAsyncDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly string Exec = $"compose -f {Paths.ComposeFile} exec -T rabbitmq rabbitmqctl ";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private const string Datasources = """
        [{"uid":"loki","type":"loki"},{"uid":"prometheus","type":"prometheus"},{"uid":"tempo","type":"tempo"}]
        """;

    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeProcessRunner runner;

    public EventTraceServiceTests() => runner = new FakeProcessRunner(new JobRegistry(time));

    /// <summary>A reachable broker whose projection queue has caught up. Unregistered, the fake exits 127 and the broker reads as unreachable.</summary>
    private void TheProjectionQueueIsDrained() =>
        runner.On("docker", Exec + "list_queues", 0, "[", """{"name":"ordering-catalog-events","messages":0}""", "]");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => runner.DisposeAsync();

    // ---- fixtures ------------------------------------------------------------------------------

    /// <summary>One Loki stream per line, shaped as plan M5 measured query_range to answer.</summary>
    private static string LokiStreams(params (string Service, string? Level, string? TraceId, DateTimeOffset At, string Message)[] lines)
    {
        string streams = string.Join(",", lines.Select(line =>
        {
            string trace = line.TraceId is null ? "" : $$""","trace_id":"{{line.TraceId}}" """.TrimEnd();
            string level = line.Level is null ? "" : $$""","severity_text":"{{line.Level}}" """.TrimEnd();

            return $$$"""
                {"stream":{"service_name":"{{{line.Service}}}"{{{level}}}{{{trace}}}},
                 "values":[["{{{line.At.ToUnixTimeMilliseconds() * 1_000_000L}}}","{{{line.Message}}}"]]}
                """;
        }));

        return $$$"""{"status":"success","data":{"resultType":"streams","result":[{{{streams}}}]}}""";
    }

    /// <summary>One Tempo batch, shaped as plan M6 measured: base64 ids, nanosecond strings, tagged-union attributes.</summary>
    private static string TempoBatch(string traceIdHex, string service, params (string SpanIdHex, string Name, string Kind, DateTimeOffset At, string Attributes)[] spans)
    {
        string body = string.Join(",", spans.Select(span =>
        {
            long startNs = span.At.ToUnixTimeMilliseconds() * 1_000_000L;
            string attributes = string.Join(",", span.Attributes
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(parts => $$$"""{"key":"{{{parts[0]}}}","value":{"stringValue":"{{{parts[1]}}}"}}"""));

            return $$$"""
                {"traceId":"{{{Base64(traceIdHex)}}}","spanId":"{{{Base64(span.SpanIdHex)}}}",
                 "name":"{{{span.Name}}}","kind":"{{{span.Kind}}}",
                 "startTimeUnixNano":"{{{startNs}}}","endTimeUnixNano":"{{{startNs + 5_000_000L}}}",
                 "attributes":[{{{attributes}}}]}
                """;
        }));

        return $$$"""
            {"batches":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"{{{service}}}"}}]},
             "scopeSpans":[{"scope":{"name":"{{{service}}}"},"spans":[{{{body}}}]}]}]}
            """;
    }

    private static string Base64(string hex) => Convert.ToBase64String(Convert.FromHexString(hex));

    private static string TraceHex(int n) => n.ToString("x", CultureInfo.InvariantCulture).PadLeft(32, 'a');

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>Routes a Grafana request the way the datasource proxy does (plan M7).</summary>
    private static ScriptedHandler Grafana(string loki, Func<string, HttpResponseMessage>? tempo = null) =>
        new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/datasources")
            {
                return Json(Datasources);
            }

            if (path.EndsWith("/loki/api/v1/query_range", StringComparison.Ordinal))
            {
                return Json(loki);
            }

            string traceIdHex = path[(path.LastIndexOf('/') + 1)..];

            return tempo is null
                ? Json(TempoBatch(traceIdHex, "Catalog.Api"))
                : tempo(traceIdHex);
        });

    private EventTraceService Service(ScriptedHandler handler, AdminOptions? options = null) =>
        new(new GrafanaClient(new HttpClient(handler), Options.Create(options ?? new AdminOptions())),
            new BrokerService(new ComposeService(runner, Paths, time)),
            Options.Create(options ?? new AdminOptions()),
            time);

    // ---- tests ---------------------------------------------------------------------------------

    [Fact]
    public void The_LogQL_filters_on_structured_metadata_with_no_json_stage()
    {
        EventTraceService.LogQl("abc-123").ShouldBe(""""
            {service_name=~".+"} | CorrelationId = "abc-123"
            """");
        EventTraceService.LogQl("abc-123").ShouldNotContain("| json");
    }

    [Fact]
    public async Task The_query_Loki_is_sent_is_the_one_LogQl_builds()
    {
        ScriptedHandler handler = Grafana(LokiStreams());
        EventTraceService service = Service(handler);

        await service.BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        Uri query = handler.Requests
            .Select(r => r.Request.RequestUri!)
            .Single(uri => uri.AbsolutePath.EndsWith("/loki/api/v1/query_range", StringComparison.Ordinal));

        Uri.UnescapeDataString(query.Query).ShouldContain(EventTraceService.LogQl("abc-123"));
        query.Query.ShouldContain($"limit={EventTraceService.MaxLines + 1}");
    }

    [Fact]
    public async Task Lines_and_spans_merge_into_one_timeline_ordered_by_time()
    {
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(
                ("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "request start"),
                ("Catalog.Api", "Information", trace, Now.AddSeconds(-10), "request done")),
            _ => Json(TempoBatch(trace, "Catalog.Api",
                ("00f067aa0ba902b7", "POST /api/v1/products", "SPAN_KIND_SERVER", Now.AddSeconds(-20), "http.route=/api/v1/products"))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Reachable.ShouldBeTrue();
        view.TraceIds.ShouldBe([trace]);
        view.Events.SkipLast(1).Select(e => e.Summary).ShouldBe(
        [
            "request start",
            "POST /api/v1/products (5 ms)",
            "request done",
        ]);
        view.Events[^1].Kind.ShouldBe(TraceEventKind.Queued);
    }

    [Fact]
    public async Task A_routed_server_span_is_an_inbound_request_and_a_publish_span_is_a_publish()
    {
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "request start")),
            _ => Json(TempoBatch(trace, "Catalog.Api",
                ("00f067aa0ba902b7", "POST /api/v1/products", "SPAN_KIND_SERVER", Now.AddSeconds(-25), "http.route=/api/v1/products"),
                ("00f067aa0ba902b8", "catalog-events send", "SPAN_KIND_PRODUCER", Now.AddSeconds(-20), "messaging.operation=publish"))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Events.Single(e => e.Summary.StartsWith("POST", StringComparison.Ordinal)).Kind.ShouldBe(TraceEventKind.HttpIn);
        view.Events.Single(e => e.Summary.StartsWith("catalog-events", StringComparison.Ordinal)).Kind.ShouldBe(TraceEventKind.Publish);
    }

    [Fact]
    public async Task The_timeline_ends_with_the_projection_snapshot_even_when_a_span_is_later()
    {
        TheProjectionQueueIsDrained();
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "request start")),
            _ => Json(TempoBatch(trace, "Catalog.Api",
                ("00f067aa0ba902b7", "late span", "SPAN_KIND_INTERNAL", Now.AddHours(1), ""))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        TraceEvent last = view.Events[^1];
        last.Kind.ShouldBe(TraceEventKind.Queued);
        last.Source.ShouldBe("broker");
        last.Summary.ShouldStartWith("ordering-catalog-events: 0 messages (drained).");
        // A lone internal span is no handover, so the marker reports the queue without claiming one.
        last.Summary.ShouldContain("No outbox write appears in this timeline");
    }

    [Fact]
    public async Task A_Loki_that_does_not_answer_is_a_state_with_no_events()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? Json(Datasources)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") });

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull();
        view.Events.ShouldBeEmpty();
        view.TraceIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trace_Tempo_cannot_serve_still_leaves_its_Loki_lines_on_the_timeline()
    {
        string good = TraceHex(1);
        string bad = TraceHex(2);
        ScriptedHandler handler = Grafana(
            LokiStreams(
                ("Catalog.Api", "Information", good, Now.AddSeconds(-30), "good line"),
                ("Ordering.Api", "Information", bad, Now.AddSeconds(-20), "line whose trace is gone")),
            traceIdHex => traceIdHex == bad
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("trace not found") }
                : Json(TempoBatch(good, "Catalog.Api", ("00f067aa0ba902b7", "good span", "SPAN_KIND_INTERNAL", Now.AddSeconds(-25), ""))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Reachable.ShouldBeTrue();
        view.Events.Select(e => e.Summary).ShouldContain("line whose trace is gone");
        view.Events.Select(e => e.Summary).ShouldContain("good span (5 ms)");
    }

    [Fact]
    public async Task More_distinct_traces_than_the_cap_are_truncated_and_only_the_cap_is_fetched()
    {
        (string, string?, string?, DateTimeOffset, string)[] lines = [.. Enumerable.Range(1, EventTraceService.MaxTraces + 3)
            .Select(n => ("Catalog.Api", (string?)"Information", (string?)TraceHex(n), Now.AddSeconds(-n), $"line {n}"))];

        ScriptedHandler handler = Grafana(LokiStreams(lines));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.TracesTruncated.ShouldBeTrue();
        view.TraceIds.Count.ShouldBe(EventTraceService.MaxTraces);
        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.Contains("/api/traces/", StringComparison.Ordinal))
            .ShouldBe(EventTraceService.MaxTraces);

        // Newest first: the smallest offset from Now is trace 1.
        view.TraceIds[0].ShouldBe(TraceHex(1));
    }

    [Fact]
    public async Task An_error_level_line_is_an_error_event()
    {
        ScriptedHandler handler = Grafana(LokiStreams(
            ("Catalog.Api", "Error", null, Now.AddSeconds(-30), "it went wrong"),
            ("Catalog.Api", "Information", null, Now.AddSeconds(-20), "it carried on")));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Events.Single(e => e.Summary == "it went wrong").Kind.ShouldBe(TraceEventKind.Error);
        view.Events.Single(e => e.Summary == "it carried on").Kind.ShouldBe(TraceEventKind.Log);
    }

    [Fact]
    public async Task Every_event_read_from_Grafana_carries_an_Explore_link_to_where_it_came_from()
    {
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "request start")),
            _ => Json(TempoBatch(trace, "Catalog.Api", ("00f067aa0ba902b7", "a span", "SPAN_KIND_INTERNAL", Now.AddSeconds(-25), ""))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        string line = view.Events.Single(e => e.Source == "loki").Link.ShouldNotBeNull();
        line.ShouldStartWith("http://localhost:3000/explore?schemaVersion=1&orgId=1&panes=");

        JsonNode lokiPane = Pane(line);
        lokiPane["datasource"]!.GetValue<string>().ShouldBe("loki");
        lokiPane["queries"]![0]!["expr"]!.GetValue<string>().ShouldBe(EventTraceService.LogQl("abc-123"));
        lokiPane["range"]!["from"]!.GetValue<string>().ShouldBe("now-15m");

        JsonNode tempoPane = Pane(view.Events.Single(e => e.Source == "tempo").Link.ShouldNotBeNull());
        tempoPane["datasource"]!.GetValue<string>().ShouldBe("tempo");
        tempoPane["queries"]![0]!["queryType"]!.GetValue<string>().ShouldBe("traceql");
        tempoPane["queries"]![0]!["query"]!.GetValue<string>().ShouldBe(trace);
    }

    /// <summary>The single Explore pane inside a deep link, decoded from the <c>panes</c> parameter (plan M8).</summary>
    private static JsonNode Pane(string link)
    {
        string encoded = link[(link.IndexOf("panes=", StringComparison.Ordinal) + "panes=".Length)..];

        return JsonNode.Parse(Uri.UnescapeDataString(encoded))!["t"]!;
    }

    [Fact]
    public async Task A_broker_that_does_not_answer_still_ends_the_timeline_honestly()
    {
        ScriptedHandler handler = Grafana(LokiStreams());

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        TraceEvent last = view.Events[^1];
        last.Kind.ShouldBe(TraceEventKind.Queued);
        last.Summary.ShouldContain("broker not reachable");
        // No outbox row in this timeline, so the marker must not speak of "the publish".
        last.Summary.ShouldContain("No outbox write appears in this timeline");
    }

    [Fact]
    public async Task The_view_echoes_the_correlation_id_and_the_window_it_was_built_for()
    {
        TraceView view = await Service(Grafana(LokiStreams())).BuildAsync("abc-123", TimeSpan.FromHours(2), Token);

        view.CorrelationId.ShouldBe("abc-123");
        view.Window.ShouldBe("2h");
    }
    [Fact]
    public async Task The_projection_marker_is_stamped_when_the_snapshot_was_taken_not_before_the_reads()
    {
        TheProjectionQueueIsDrained();
        ScriptedHandler handler = new(request =>
        {
            // Every Grafana read costs the clock a second, as a real round trip would.
            time.Advance(TimeSpan.FromSeconds(1));

            return request.RequestUri!.AbsolutePath == "/api/datasources" ? Json(Datasources) : Json(LokiStreams());
        });

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        TraceEvent last = view.Events[^1];
        last.Kind.ShouldBe(TraceEventKind.Queued);
        last.At.ShouldBeGreaterThan(Now);
    }
    [Fact]
    public async Task A_drained_queue_whose_error_queue_holds_messages_is_not_reported_as_simply_drained()
    {
        runner.On("docker", Exec + "list_queues", 0,
            "[",
            """{"name":"ordering-catalog-events","messages":0}""",
            """,{"name":"ordering-catalog-events_error","messages":1}""",
            "]");

        TraceView view = await Service(Grafana(LokiStreams())).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Events[^1].Summary.ShouldStartWith(
            "ordering-catalog-events: 0 messages (drained), 1 parked in ordering-catalog-events_error.");
    }

    [Fact]
    public async Task A_Tempo_that_will_not_answer_is_a_warning_on_the_view_not_a_silently_shorter_timeline()
    {
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "a line")),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Tempo is down") });

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Reachable.ShouldBeTrue();
        view.Warning.ShouldNotBeNull();
        view.Warning.ShouldContain("1 of 1 trace could not be read from Tempo");
        view.Warning.ShouldContain("503");
    }

    [Fact]
    public async Task A_timeline_whose_traces_all_came_back_carries_no_warning()
    {
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "a line")),
            _ => Json(TempoBatch(trace, "Catalog.Api", ("00f067aa0ba902b7", "a span", "SPAN_KIND_INTERNAL", Now.AddSeconds(-25), ""))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Warning.ShouldBeNull();
    }
    [Fact]
    public async Task More_log_lines_than_the_cap_are_said_out_loud_rather_than_quietly_dropped()
    {
        (string, string?, string?, DateTimeOffset, string)[] lines = [.. Enumerable.Range(1, EventTraceService.MaxLines + 1)
            .Select(n => ("Catalog.Api", (string?)"Information", (string?)null, Now.AddSeconds(-n), $"line {n}"))];

        ScriptedHandler handler = Grafana(LokiStreams(lines));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Warning.ShouldNotBeNull();
        view.Warning.ShouldContain($"More than {EventTraceService.MaxLines} log lines");
        // The cap is applied, not merely reported: the terminal marker is the only extra row.
        view.Events.Count.ShouldBe(EventTraceService.MaxLines + 1);
    }

    [Fact]
    public async Task Loki_is_asked_for_one_line_past_the_cap_so_the_boundary_can_be_seen()
    {
        ScriptedHandler handler = Grafana(LokiStreams());

        await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        handler.Requests
            .Select(r => r.Request.RequestUri!)
            .Single(uri => uri.AbsolutePath.EndsWith("/loki/api/v1/query_range", StringComparison.Ordinal))
            .Query.ShouldContain($"limit={EventTraceService.MaxLines + 1}");
    }
    [Fact]
    public async Task A_timeline_with_an_outbox_write_explains_where_the_trace_is_severed()
    {
        TheProjectionQueueIsDrained();
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "a line")),
            _ => Json(TempoBatch(trace, "Catalog.Api",
                ("00f067aa0ba902b7", "INSERT catalog", "SPAN_KIND_CLIENT", Now.AddSeconds(-25),
                    "db.system=mssql;db.statement=INSERT INTO dbo.OutboxMessages"))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Events.ShouldContain(e => e.Kind == TraceEventKind.Outbox);
        view.Events[^1].Summary.ShouldContain("The publish runs in a new trace");
    }

    [Fact]
    public async Task A_read_only_request_is_not_told_about_a_publish_that_never_happened()
    {
        TheProjectionQueueIsDrained();
        string trace = TraceHex(1);
        ScriptedHandler handler = Grafana(
            LokiStreams(("Catalog.Api", "Information", trace, Now.AddSeconds(-30), "GET /api/v1/products")),
            _ => Json(TempoBatch(trace, "Gateway.Api",
                ("00f067aa0ba902b7", "GET /api/v1/products", "SPAN_KIND_SERVER", Now.AddSeconds(-25), "http.route=/api/v1/products"))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Events.ShouldNotContain(e => e.Kind == TraceEventKind.Outbox);
        view.Events[^1].Summary.ShouldContain("No outbox write appears in this timeline");
        view.Events[^1].Summary.ShouldNotContain("The publish runs in a new trace");
    }
    [Fact]
    public async Task A_Grafana_without_Tempo_is_said_once_rather_than_re_resolved_for_every_trace()
    {
        string first = TraceHex(1);
        string second = TraceHex(2);
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? Json("""[{"uid":"loki","type":"loki"}]""")
            : Json(LokiStreams(
                ("Catalog.Api", "Information", first, Now.AddSeconds(-30), "one"),
                ("Ordering.Api", "Information", second, Now.AddSeconds(-20), "two"))));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Reachable.ShouldBeTrue();
        view.Warning.ShouldNotBeNull();
        view.Warning.ShouldContain("2 of 2 traces could not be read from Tempo");
        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.Contains("/api/traces/", StringComparison.Ordinal)).ShouldBe(0);
    }

    [Fact]
    public async Task A_Grafana_without_Prometheus_resolves_the_datasources_once_across_trace_reads()
    {
        string first = TraceHex(1);
        ScriptedHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;

            return path == "/api/datasources" ? Json("""[{"uid":"loki","type":"loki"},{"uid":"tempo","type":"tempo"}]""")
                : path.EndsWith("/loki/api/v1/query_range", StringComparison.Ordinal)
                    ? Json(LokiStreams(("Catalog.Api", "Information", first, Now.AddSeconds(-30), "one")))
                    : Json(TempoBatch(first, "Catalog.Api"));
        });
        EventTraceService service = Service(handler);

        await service.BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);
        await service.BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/api/datasources").ShouldBe(1);
    }

    [Fact]
    public async Task The_newest_lines_survive_the_cap_even_when_Loki_groups_them_by_stream()
    {
        // Loki answers stream by stream, so the oldest stream can arrive first. Taking the head of the
        // flattened list would keep those and drop the newest lines entirely.
        (string, string?, string?, DateTimeOffset, string)[] oldest = [.. Enumerable.Range(1, EventTraceService.MaxLines)
            .Select(n => ("Catalog.Api", (string?)"Information", (string?)null, Now.AddHours(-1).AddSeconds(-n), $"old {n}"))];
        (string, string?, string?, DateTimeOffset, string)[] newest =
            [("Gateway.Api", "Information", null, Now.AddSeconds(-5), "the newest line")];

        ScriptedHandler handler = Grafana(LokiStreams([.. oldest, .. newest]));

        TraceView view = await Service(handler).BuildAsync("abc-123", TimeSpan.FromMinutes(15), Token);

        view.Warning.ShouldNotBeNull();
        view.Events.Select(e => e.Summary).ShouldContain("the newest line");
    }
}
