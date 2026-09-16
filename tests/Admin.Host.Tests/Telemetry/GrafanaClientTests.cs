using System.Net;
using System.Text;
using Admin.Host.Config;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Telemetry;

public sealed class GrafanaClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Measured 2026-09-16: GET /api/datasources on grafana/otel-lgtm, trimmed to the fields read.
    private const string Datasources = """
        [{"id":3,"uid":"loki","name":"Loki","type":"loki"},
         {"id":1,"uid":"prometheus","name":"Prometheus","type":"prometheus"},
         {"id":2,"uid":"tempo","name":"Tempo","type":"tempo"}]
        """;

    // Measured 2026-09-16 (plan M5): one stream from a synthetic OTLP log record read back through
    // query_range, wrapped in the endpoint's status/data envelope.
    private const string LokiQueryRange = """
        {"status":"success","data":{"resultType":"streams","result":[
          {"stream":{"CorrelationId":"probe-corr-12345","RequestType":"GetProductsQuery",
            "detected_level":"info","scope_name":"Common.Web.CorrelationId","service_name":"Catalog.Api",
            "severity_number":"9","severity_text":"Information",
            "span_id":"00f067aa0ba902b7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736"},
           "values":[["1789532582000000000","probe: request start"]]}
        ]}}
        """;

    // Measured 2026-09-16 (plan M6): two batches shaped like GET /api/traces/{id} through the
    // datasource proxy. traceId qqqqqqqqqqqqqqqqqqqqoQ== is the plan's own example, base64 for hex
    // aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1. The span ids' base64 is computed from the hex asserted below.
    // The root span's attributes carry one of each OTLP tagged-union shape M6 names
    // (stringValue/intValue/boolValue/doubleValue; intValue is itself a JSON string).
    private const string TempoTrace = """
        {"batches":[
          {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"Gateway.Api"}}]},
           "scopeSpans":[{"scope":{"name":"Gateway.Api"},"spans":[
             {"traceId":"qqqqqqqqqqqqqqqqqqqqoQ==","spanId":"APBnqgupArc=",
              "name":"GET /api/products","kind":"SPAN_KIND_SERVER",
              "startTimeUnixNano":"1700000000000000000","endTimeUnixNano":"1700000000050000000",
              "attributes":[{"key":"http.method","value":{"stringValue":"GET"}},
                {"key":"http.status_code","value":{"intValue":"200"}},
                {"key":"http.retry","value":{"boolValue":true}},
                {"key":"duration_ms","value":{"doubleValue":12.5}}]}
           ]}]},
          {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"Catalog.Api"}}]},
           "scopeSpans":[{"scope":{"name":"Catalog.Api"},"spans":[
             {"traceId":"qqqqqqqqqqqqqqqqqqqqoQ==","spanId":"qrvM3e7/ABE=","parentSpanId":"APBnqgupArc=",
              "name":"SELECT products","kind":"SPAN_KIND_CLIENT",
              "startTimeUnixNano":"1700000000005000000","endTimeUnixNano":"1700000000045000000",
              "attributes":[{"key":"db.statement","value":{"stringValue":"SELECT * FROM products"}}],
              "status":{"code":"STATUS_CODE_ERROR"}}
           ]}]}
        ]}
        """;

    // A companion to TempoTrace with a second, malformed span alongside a good one: neither
    // traceId nor spanId decodes as base64.
    private const string TempoTraceWithAMalformedSpan = """
        {"batches":[
          {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"Gateway.Api"}}]},
           "scopeSpans":[{"scope":{"name":"Gateway.Api"},"spans":[
             {"traceId":"qqqqqqqqqqqqqqqqqqqqoQ==","spanId":"APBnqgupArc=",
              "name":"GET /api/products","kind":"SPAN_KIND_SERVER",
              "startTimeUnixNano":"1700000000000000000","endTimeUnixNano":"1700000000050000000",
              "attributes":[]},
             {"traceId":"not-valid-base64!!","spanId":"also-not-valid-base64!!",
              "name":"bad-span","kind":"SPAN_KIND_INTERNAL",
              "startTimeUnixNano":"1700000000000000000","endTimeUnixNano":"1700000000050000000",
              "attributes":[]}
           ]}]}
        ]}
        """;

    private static GrafanaClient Client(ScriptedHandler handler, AdminOptions? options = null) =>
        new(new HttpClient(handler), Options.Create(options ?? new AdminOptions()));

    private static HttpResponseMessage FakeJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

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
        int calls = 0;
        ScriptedHandler handler = new(_ => ++calls == 1
            ? throw new HttpRequestException("Connection refused")
            : FakeJson(Datasources));
        GrafanaClient client = Client(handler);

        DatasourceUids first = await client.UidsAsync(Token);
        DatasourceUids second = await client.UidsAsync(Token);

        first.Loki.ShouldBeNull();
        second.Loki.ShouldBe("loki");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_loki_range_query_is_sent_through_the_datasource_proxy_with_nanosecond_bounds()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""{"status":"success","data":{"resultType":"streams","result":[]}}"""));
        GrafanaClient client = Client(handler);
        DateTimeOffset from = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
        const string LogQl = "{service_name=~\".+\"} | CorrelationId = \"probe-corr-12345\"";

        await client.QueryAsync(LogQl, from, to, 500, Token);

        Uri uri = handler.Requests[1].Request.RequestUri!;
        uri.GetLeftPart(UriPartial.Path).ShouldBe("http://localhost:3000/api/datasources/proxy/uid/loki/loki/api/v1/query_range");
        uri.Query.ShouldBe($"?query={Uri.EscapeDataString(LogQl)}&start={from.ToUnixTimeMilliseconds() * 1_000_000L}&end={to.ToUnixTimeMilliseconds() * 1_000_000L}&limit=500");
    }

    [Fact]
    public async Task Loki_lines_carry_the_service_level_message_and_trace_id()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson(LokiQueryRange));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 100, Token);

        result.Reachable.ShouldBeTrue();
        LokiLine line = result.Lines.ShouldHaveSingleItem();
        line.Service.ShouldBe("Catalog.Api");
        line.Level.ShouldBe("Information");
        line.TraceId.ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        line.Message.ShouldBe("probe: request start");
    }

    [Fact]
    public async Task A_tempo_trace_decodes_base64_ids_to_hex_and_names_each_span_service()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson(TempoTrace));
        GrafanaClient client = Client(handler);

        TempoResult result = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        result.Reachable.ShouldBeTrue();
        result.Spans.Count.ShouldBe(2);
        TempoSpan root = result.Spans.Single(s => s.Name == "GET /api/products");
        TempoSpan child = result.Spans.Single(s => s.Name == "SELECT products");

        root.TraceId.ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1");
        root.SpanId.ShouldBe("00f067aa0ba902b7");
        root.ParentSpanId.ShouldBeNull();
        root.Service.ShouldBe("Gateway.Api");
        root.Failed.ShouldBeFalse();
        root.Attributes["http.method"].ShouldBe("GET");
        root.Attributes["http.status_code"].ShouldBe("200");
        root.Attributes["http.retry"].ShouldBe("true");
        root.Attributes["duration_ms"].ShouldBe("12.5");

        child.TraceId.ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1");
        child.SpanId.ShouldBe("aabbccddeeff0011");
        child.ParentSpanId.ShouldBe("00f067aa0ba902b7");
        child.Service.ShouldBe("Catalog.Api");
        child.Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_malformed_span_is_skipped_but_a_good_span_in_the_same_trace_still_comes_back()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson(TempoTraceWithAMalformedSpan));
        GrafanaClient client = Client(handler);

        TempoResult result = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        result.Reachable.ShouldBeTrue();
        TempoSpan good = result.Spans.ShouldHaveSingleItem();
        good.Name.ShouldBe("GET /api/products");
    }

    [Fact]
    public async Task A_malformed_200_from_datasources_leaves_uids_unresolved_instead_of_throwing()
    {
        ScriptedHandler handler = new(_ => FakeJson("""{"oops":true}"""));
        GrafanaClient client = Client(handler);

        DatasourceUids uids = await client.UidsAsync(Token);

        uids.Loki.ShouldBeNull();
        uids.Tempo.ShouldBeNull();
    }

    [Fact]
    public async Task A_malformed_200_from_loki_is_unreachable_instead_of_throwing()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""{"status":"success"}"""));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);

        result.Reachable.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_200_from_tempo_is_unreachable_instead_of_throwing()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""{"oops":true}"""));
        GrafanaClient client = Client(handler);

        TempoResult result = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        result.Reachable.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Spans.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_grafana_url_leaves_the_datasource_uids_unresolved()
    {
        ScriptedHandler handler = new(_ => FakeJson(Datasources));
        GrafanaClient client = Client(handler, new AdminOptions { GrafanaUrl = "localhost:3000" });

        DatasourceUids uids = await client.UidsAsync(Token);

        uids.Loki.ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_grafana_url_is_unreachable_for_queries_and_traces_once_uids_are_cached()
    {
        AdminOptions options = new();
        ScriptedHandler handler = new(_ => FakeJson(Datasources));
        GrafanaClient client = Client(handler, options);
        await client.UidsAsync(Token);
        options.GrafanaUrl = "localhost:3000";

        LokiResult loki = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);
        TempoResult tempo = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        loki.Reachable.ShouldBeFalse();
        tempo.Reachable.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(1);
    }
    [Fact]
    public async Task A_nanosecond_timestamp_too_large_to_parse_is_unreachable_instead_of_throwing()
    {
        // 30 digits: past long, so long.Parse throws OverflowException rather than FormatException.
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""
                {"status":"success","data":{"resultType":"streams","result":[
                  {"stream":{"service_name":"Catalog.Api"},
                   "values":[["999999999999999999999999999999","too far in the future"]]}
                ]}}
                """));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);

        result.Reachable.ShouldBeFalse();
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_values_entry_missing_its_line_is_unreachable_instead_of_throwing()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""
                {"status":"success","data":{"resultType":"streams","result":[
                  {"stream":{"service_name":"Catalog.Api"},"values":[["1789532582000000000"]]}
                ]}}
                """));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);

        result.Reachable.ShouldBeFalse();
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_span_timestamp_too_large_to_parse_is_unreachable_instead_of_throwing()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""
                {"batches":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
                  {"traceId":"qqqqqqqqqqqqqqqqqqqqoQ==","spanId":"APBnqgupArc=","name":"s","kind":"SPAN_KIND_INTERNAL",
                   "startTimeUnixNano":"999999999999999999999999999999","endTimeUnixNano":"999999999999999999999999999999","attributes":[]}
                ]}]}]}
                """));
        GrafanaClient client = Client(handler);

        TempoResult result = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        result.Reachable.ShouldBeFalse();
        result.Spans.ShouldBeEmpty();
    }
    [Fact]
    public async Task An_incomplete_datasource_list_is_not_cached_so_a_late_provisioned_Tempo_is_found()
    {
        int calls = 0;
        ScriptedHandler handler = new(_ =>
        {
            calls++;

            return FakeJson(calls == 1
                ? """[{"uid":"loki","type":"loki"},{"uid":"prometheus","type":"prometheus"}]"""
                : Datasources);
        });
        GrafanaClient client = Client(handler);

        DatasourceUids first = await client.UidsAsync(Token);
        DatasourceUids second = await client.UidsAsync(Token);

        first.Tempo.ShouldBeNull();
        second.Tempo.ShouldBe("tempo");
        calls.ShouldBe(2);
    }
    [Fact]
    public async Task A_Grafana_that_does_not_answer_is_reported_as_an_outage_not_as_a_missing_datasource()
    {
        ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down for maintenance"),
        });
        GrafanaClient client = Client(handler);

        LokiResult loki = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);
        TempoResult tempo = await client.TraceAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", Token);

        // Sending an operator to check configuration during an outage is the wrong answer.
        loki.Error.ShouldBe("Grafana answered 503 for its datasource list.");
        tempo.Error.ShouldBe("Grafana answered 503 for its datasource list.");
    }

    [Fact]
    public async Task A_Grafana_that_answers_without_Loki_still_says_the_datasource_is_missing()
    {
        ScriptedHandler handler = new(_ => FakeJson("""[{"uid":"tempo","type":"tempo"}]"""));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);

        result.Error.ShouldBe("Grafana has no Loki datasource.");
    }

    [Fact]
    public async Task Sub_millisecond_timestamps_survive_so_two_lines_in_one_millisecond_stay_ordered()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson(Datasources)
            : FakeJson("""
                {"status":"success","data":{"resultType":"streams","result":[
                  {"stream":{"service_name":"Catalog.Api"},
                   "values":[["1789532582000100000","first"],["1789532582000900000","second"]]}
                ]}}
                """));
        GrafanaClient client = Client(handler);

        LokiResult result = await client.QueryAsync("{service_name=~\".+\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10, Token);

        result.Lines[0].At.ShouldBeLessThan(result.Lines[1].At);
        (result.Lines[1].At - result.Lines[0].At).ShouldBe(TimeSpan.FromTicks(8000));
    }
}
