using System.Net;
using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>
/// Grafana's datasource list and its Loki, Tempo and Prometheus proxies in FakePlatform mode. The
/// three fixture recordings mirror the shapes measured on <c>grafana/otel-lgtm</c> 13.1.1 (plan M4, M5 and M6):
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

        return FakeHttp.Json(
            HttpStatusCode.OK,
            Fixture("grafana-loki-query.json").Replace(RecordedCorrelationId, requested, StringComparison.Ordinal));
    }

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
    /// One recording per <see cref="Telemetry.GoldenSignals"/> query, chosen by which query was
    /// asked. The shape is Prometheus's documented <c>/api/v1/query</c> vector, not a measurement
    /// through this Grafana. Ordering.Api has no traffic, so its ratio and quantile are <c>NaN</c>,
    /// and Catalog.Api has no 5xx, so it has no error-ratio series at all.
    /// </summary>
    public static HttpResponseMessage Prometheus(HttpRequestMessage request)
    {
        string query = Uri.UnescapeDataString(request.RequestUri!.Query);

        // The error ratio's denominator is the request-rate query itself, so it is matched first.
        string series =
            query.Contains(Telemetry.GoldenSignals.LatencyP99, StringComparison.Ordinal)
                ? """
                  {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"0.048"]},
                  {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"0.09"]},
                  {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"NaN"]}
                  """
            : query.Contains(Telemetry.GoldenSignals.ErrorRatio, StringComparison.Ordinal)
                ? """
                  {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"0.05"]},
                  {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"NaN"]}
                  """
            : query.Contains(Telemetry.GoldenSignals.RequestRate, StringComparison.Ordinal)
                ? """
                  {"metric":{"service_name":"Catalog.Api"},"value":[1789532580,"1.2"]},
                  {"metric":{"service_name":"Gateway.Api"},"value":[1789532580,"2.4"]},
                  {"metric":{"service_name":"Ordering.Api"},"value":[1789532580,"0"]}
                  """
            : "";

        return FakeHttp.Json(HttpStatusCode.OK, $$$"""{"status":"success","data":{"resultType":"vector","result":[{{{series}}}]}}""");
    }

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
