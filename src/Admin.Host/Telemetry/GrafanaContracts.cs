namespace Admin.Host.Telemetry;

/// <summary>
/// Loki's, Tempo's and Prometheus's datasource uids inside Grafana, resolved by <c>type</c> from
/// <c>GET /api/datasources</c> and cached. They are stable in <c>grafana/otel-lgtm</c> but are not
/// provisioned by <c>blueprint-backend</c> (measured 2026-09-16, plan M4: it ships no
/// <c>provisioning/</c> at all), so they are never hard-coded.
/// </summary>
/// <param name="Error">
/// Why the uids could not be resolved, when that is a Grafana that did not answer rather than a
/// Grafana that answered without the datasource. The two send an operator to different places —
/// an outage, or a missing datasource — so they are not collapsed into one all-null result.
/// </param>
public sealed record DatasourceUids(string? Loki, string? Tempo, string? Prometheus, string? Error = null);

/// <summary>
/// One log line from a Loki range query. <paramref name="TraceId"/> is read from the
/// <c>trace_id</c> structured-metadata label (measured 2026-09-16, plan M5) and is null when the
/// line carries none.
/// </summary>
public sealed record LokiLine(DateTimeOffset At, string Service, string? Level, string Message, string? TraceId);

/// <summary>A Loki query outcome. A Grafana/Loki that does not answer is a state, not an exception (spec §9).</summary>
public sealed record LokiResult(bool Reachable, string? Error, IReadOnlyList<LokiLine> Lines);

/// <summary>
/// One span of a Tempo trace. <paramref name="TraceId"/>, <paramref name="SpanId"/> and
/// <paramref name="ParentSpanId"/> are already decoded from Tempo's base64 to lowercase hex
/// (measured 2026-09-16, plan M6). <paramref name="ParentSpanId"/> is null on a root span.
/// <paramref name="Kind"/> is Tempo's own string form (for example <c>SPAN_KIND_SERVER</c>).
/// </summary>
public sealed record TempoSpan(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Service,
    string Name,
    string Kind,
    DateTimeOffset At,
    TimeSpan Duration,
    IReadOnlyDictionary<string, string> Attributes,
    bool Failed);

/// <summary>A Tempo trace fetch outcome. A Grafana/Tempo that does not answer is a state, not an exception (spec §9).</summary>
public sealed record TempoResult(bool Reachable, string? Error, IReadOnlyList<TempoSpan> Spans);

/// <summary>
/// One series of a Prometheus instant vector grouped by <c>service_name</c>. <paramref name="Value"/>
/// is null where Prometheus answered a value that is not a finite number: <c>NaN</c> is what a ratio
/// or a quantile over no requests comes back as.
/// </summary>
public sealed record PrometheusSample(string Service, double? Value);

/// <summary>A Prometheus query outcome. A Grafana/Prometheus that does not answer is a state, not an exception (spec §9).</summary>
public sealed record PrometheusResult(bool Reachable, string? Error, IReadOnlyList<PrometheusSample> Samples);
