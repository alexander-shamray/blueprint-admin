using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Fakes;

/// <summary>
/// Grafana's datasource list and its Loki, Tempo and Prometheus proxies in FakePlatform mode, each answered from
/// a fixture whose source <see cref="FixtureRecordings"/> names. The Loki and Tempo answers mirror the shapes
/// measured on <c>grafana/otel-lgtm</c> 13.1.1 (plan M5 and M6):
/// Loki answers a <c>streams</c> envelope whose line body is the formatted message and whose
/// <c>CorrelationId</c> is structured metadata, and Tempo answers OTLP-JSON with base64 ids.
/// <para>
/// The recording is one publish of a product: a first attempt that failed with 503, then a retry
/// that succeeded and wrote an outbox row. The outbox carries the trace on, so the retry's trace goes on
/// to the dispatcher's delivery, the <c>ProductPublished</c> send and Ordering's consume of it on the
/// projection queue: those four spans are a live publish's, recorded 2026-10-18 and moved under the
/// fixture's trace id and times.
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
    public static HttpResponseMessage Loki(HttpRequestMessage request, FakeOrders orders)
    {
        if (FlaggedOrderIn(request.RequestUri!.Query) is { } flaggedOrder)
        {
            return FakeHttp.Json(HttpStatusCode.OK, ReviewFlags(flaggedOrder, orders));
        }

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
    /// The review-flag query's answer (run-locally.md, "flagged for review"): one stream per flag the fake order has, whose line is
    /// the one Ordering logs, or an empty result for an order with none. The console builds the query in
    /// <c>ReviewFlagService.LogQl</c>.
    /// </summary>
    private static string ReviewFlags(string orderId, FakeOrders orders)
    {
        string streams = string.Join(",", orders.ReviewFlags(orderId).Select(flag =>
        {
            long nanoseconds = flag.At.ToUnixTimeMilliseconds() * 1_000_000L;
            string line = $"Order {orderId} flagged for review: {flag.Reason}.";

            return $$$"""{"stream":{"service_name":"Ordering.Api","severity_text":"Information"},"values":[["{{{nanoseconds}}}","{{{line}}}"]]}""";
        }));

        return $$$"""{"status":"success","data":{"resultType":"streams","result":[{{{streams}}}]}}""";
    }

    /// <summary>The order id inside the review-flag query, or null for any other query.</summary>
    private static string? FlaggedOrderIn(string query)
    {
        string decoded = Uri.UnescapeDataString(query);
        const string marker = "flagged for review\" |= \"";
        int start = decoded.IndexOf(marker, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        int from = start + marker.Length;
        int end = decoded.IndexOf('"', from);

        return end < 0 ? null : decoded[from..end];
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
    /// run-locally.md says to reuse a commandId to retry an intent.
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
    /// While a fake load run's traffic is inside the strip's reads (<see cref="FakeStack.Loaded"/>) the answers are
    /// the ones recorded under a live load run instead (<see cref="LoadRunRecordings.GoldenSignals"/>), whose
    /// absent 5xx, 401 and 422 series answer the empty vector Prometheus gave. While a fake stress run's k6 runs users
    /// (<see cref="FakeStack.StressUsers"/>) the load run's answers are scaled by those users over the load run's, so the
    /// series rise with the steps; that scaling is the fake's own, since no stress run has been recorded (<see cref="StressRunRecordings"/>).
    /// </summary>
    public static HttpResponseMessage Prometheus(HttpRequestMessage request, FakeStack stack)
    {
        int users = stack.StressUsers;
        using JsonDocument recording = JsonDocument.Parse(users > 0 || stack.Loaded ? LoadRunRecordings.GoldenSignals : Fixture("grafana-prometheus-golden-signals.json"));

        if (GoldenSignalOf(request.RequestUri!) is not { } field)
        {
            return FakeHttp.Json(HttpStatusCode.OK, """{"status":"success","data":{"resultType":"vector","result":[]}}""");
        }

        string answer = recording.RootElement.GetProperty(field).GetRawText();

        return FakeHttp.Json(HttpStatusCode.OK, users > 0 ? Scaled(answer, (double)users / LoadRunRecordings.Users) : answer);
    }

    /// <summary>A vector answer with each sample's value multiplied by <paramref name="factor"/>.</summary>
    internal static string Scaled(string answer, double factor)
    {
        JsonNode document = JsonNode.Parse(answer)!;

        foreach (JsonNode? series in document["data"]!["result"]!.AsArray())
        {
            JsonArray value = series!["value"]!.AsArray();
            double sample = double.Parse(value[1]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            value[1] = (sample * factor).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        return document.ToJsonString();
    }

    /// <summary>
    /// Prometheus's rules answer: one rule firing, one pending and the rest quiet, so the strip has each state to
    /// show. Loaded rules only; the waiting ones are <see cref="Telemetry.WaitingAlerts"/>, which no Prometheus lists.
    /// </summary>
    public static HttpResponseMessage Rules(FakeStack stack)
    {
        string recording = Fixture("grafana-prometheus-rules.json");

        return FakeHttp.Json(HttpStatusCode.OK, stack.CausedAlerts is { Count: > 0 } caused ? Caused(recording, caused) : recording);
    }

    /// <summary>
    /// The rules answer with each caused alert firing, as a rule is once its cause has held for its deadline: the recording is one
    /// instant and a game day changes it (<see cref="RunbookRecordings"/>). A rule the recording does not list stays unlisted.
    /// </summary>
    private static string Caused(string recording, IReadOnlySet<string> caused)
    {
        JsonNode document = JsonNode.Parse(recording)!;

        foreach (JsonNode? rule in document["data"]!["groups"]!.AsArray().SelectMany(g => g!["rules"]!.AsArray()))
        {
            if (rule!["name"]!.GetValue<string>() is { } name && caused.Contains(name))
            {
                rule["state"] = "firing";
                rule["alerts"] = new JsonArray(new JsonObject
                {
                    ["labels"] = new JsonObject { ["alertname"] = name, ["severity"] = rule["labels"]?["severity"]?.GetValue<string>() },
                    ["state"] = "firing",
                });
            }
        }

        return document.ToJsonString();
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
