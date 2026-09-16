namespace Admin.Host.Telemetry;

/// <summary>
/// Loki's, Tempo's and Prometheus's datasource uids inside Grafana, resolved by <c>type</c> from
/// <c>GET /api/datasources</c> and cached. They are stable in <c>grafana/otel-lgtm</c> but are not
/// provisioned by <c>blueprint-backend</c> (measured 2026-09-16, plan M4: it ships no
/// <c>provisioning/</c> at all), so they are never hard-coded.
/// </summary>
public sealed record DatasourceUids(string? Loki, string? Tempo, string? Prometheus);

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
