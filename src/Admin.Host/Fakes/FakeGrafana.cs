using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Admin.Host.Fakes;

/// <summary>
/// Grafana's datasource list and its Loki, Tempo and Prometheus proxies in FakePlatform mode, each answered from
/// a fixture whose source <see cref="FixtureRecordings"/> names. The Loki and Tempo answers mirror the shapes
/// measured on <c>grafana/otel-lgtm</c> 13.1.1 (plan M5 and M6):
/// Loki answers a <c>streams</c> envelope whose line body is the formatted message and whose
/// <c>CorrelationId</c> is structured metadata, and Tempo answers OTLP-JSON with base64 ids.
/// <para>
/// The recording is one publish of a product: a first attempt that failed with 503, then a retry
/// that succeeded and wrote an outbox row. It ends at that row on purpose — the backend's outbox
/// carries no trace context (plan M2), so there is no publish or consume span to record here, and a
/// recording that showed one would contradict the very thing the Trace screen exists to say.
/// </para>
/// </summary>
internal static class FakeGrafana
{
    /// <summary>The correlation id written into <c>grafana-loki-query.json</c>, rewritten to whatever the caller asked for.</summary>
    private const string RecordedCorrelationId = "demo-trace-0001";

    /// <summary>The one trace <c>grafana-tempo-trace.json</c> holds. Tempo answers 404 for any other id, as the real one does.</summary>
    public const string RecordedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>The second trace id the Loki recording carries, on the failed first attempt. Tempo has no recording for it.</summary>
    public const string RecordedRetryTraceId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    public static HttpResponseMessage Datasources() =>
        FakeHttp.Json(HttpStatusCode.OK, Fixture("grafana-datasources.json"));

    /// <summary>
    /// The recorded lines, whatever the query, with the recorded correlation id replaced by the one
    /// the caller filtered on so the screen shows the id the user typed. The rewrite is what lets one
    /// recording answer any id; everything else in it is the measurement.
    /// </summary>
    public static HttpResponseMessage Loki(HttpRequestMessage request)
    {
        string requested = CorrelationIdIn(request.RequestUri!.Query) ?? RecordedCorrelationId;
        string body = Fixture("grafana-loki-query.json").Replace(RecordedCorrelationId, requested, StringComparison.Ordinal);

        if (requested.StartsWith(MovingPrefix, StringComparison.Ordinal) && MovingFetches.AddOrUpdate(requested, 1, (_, n) => n + 1) > 1)
        {
            int result = body.IndexOf(ResultArray, StringComparison.Ordinal) + ResultArray.Length;
            body = body.Insert(result, LaterLine.Replace(RecordedCorrelationId, requested, StringComparison.Ordinal));
        }

        return FakeHttp.Json(HttpStatusCode.OK, body);
    }

    /// <summary>
    /// A correlation id with this prefix is a flow still moving: from its second fetch on, Loki also answers
    /// <see cref="LaterLine"/>, so the Trace screen has a second, different timeline to compare (spec §5.9). Every
    /// other id answers the recording unchanged, however often it is asked.
    /// </summary>
    public const string MovingPrefix = "fake-moving-";

    /// <summary>Fetches per moving id. Process-wide, as the fakes are; only ids under <see cref="MovingPrefix"/> touch it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> MovingFetches = new(StringComparer.Ordinal);

    private const string ResultArray = "\"result\": [";

    /// <summary>
    /// What lands after the first fetch: the same publish sent again under the same correlation id, as
    /// run-locally.md says to reuse a commandId to retry an intent. It is the request's own side of the outbox,
    /// the only side this correlation id reaches (spec §5.9).
    /// </summary>
    private const string LaterLine = """
        {"stream":{"CorrelationId":"demo-trace-0001","detected_level":"info","scope_name":"Microsoft.AspNetCore.Hosting.Diagnostics","service_name":"Gateway.Api","severity_number":"9","severity_text":"Information"},"values":[["1789532585000000000","Request finished POST /api/v1/products - 201 (the same commandId sent again)"]]},
        """;

    /// <summary>The recorded trace, or the 404 Tempo gives for a trace it does not hold.</summary>
    public static HttpResponseMessage Tempo(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        string traceId = path[(path.LastIndexOf('/') + 1)..];

        return traceId.Equals(RecordedTraceId, StringComparison.OrdinalIgnoreCase)
            ? FakeHttp.Json(HttpStatusCode.OK, Fixture("grafana-tempo-trace.json"))
            : FakeHttp.Json(HttpStatusCode.NotFound, """{"error":"trace not found"}""");
    }

    /// <summary>
    /// Each <see cref="Telemetry.GoldenSignals"/> query's answer, recorded through the live Grafana's
    /// Prometheus proxy on 2026-10-03 under steady traffic and keyed by the field that holds the query.
    /// A query is matched by its exact text, because the 401 and 422 queries differ from the request-rate
    /// one only inside the selector; any other query answers the empty vector Prometheus gives for no series.
    /// </summary>
    public static HttpResponseMessage Prometheus(HttpRequestMessage request)
    {
        using JsonDocument recording = JsonDocument.Parse(Fixture("grafana-prometheus-golden-signals.json"));

        return GoldenSignalOf(request.RequestUri!) is { } field
            ? FakeHttp.Json(HttpStatusCode.OK, recording.RootElement.GetProperty(field).GetRawText())
            : FakeHttp.Json(HttpStatusCode.OK, """{"status":"success","data":{"resultType":"vector","result":[]}}""");
    }

    /// <summary>
    /// The <see cref="Telemetry.GoldenSignals"/> field a Prometheus query's URL asks for, which is the key its answer
    /// has in the recording, or null for any other query. The recorder writes under the same key.
    /// </summary>
    public static string? GoldenSignalOf(Uri query)
    {
        const string prefix = "?query=";
        string raw = query.Query;
        string promQl = raw.StartsWith(prefix, StringComparison.Ordinal) ? Uri.UnescapeDataString(raw[prefix.Length..]) : "";

        return GoldenSignalFields.GetValueOrDefault(promQl);
    }

    /// <summary>Each query's text to the <see cref="Telemetry.GoldenSignals"/> field the recording is keyed by.</summary>
    private static readonly Dictionary<string, string> GoldenSignalFields = typeof(Telemetry.GoldenSignals)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name, StringComparer.Ordinal);

    /// <summary>
    /// The id inside a LogQL label filter, read back out of the query string. The console builds that
    /// query in <c>EventTraceService.LogQl</c>; this is the only place that has to unpick it, because a
    /// recording has no index to look the id up in.
    /// </summary>
    private static string? CorrelationIdIn(string query)
    {
        string decoded = Uri.UnescapeDataString(query);
        const string marker = "CorrelationId = \"";
        int start = decoded.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        int from = start + marker.Length;
        int end = decoded.IndexOf('"', from);

        return end < 0 ? null : decoded[from..end];
    }

    private static string Fixture(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}
