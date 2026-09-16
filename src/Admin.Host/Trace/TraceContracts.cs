namespace Admin.Host.Trace;

/// <summary>
/// What one row of the timeline is. The order of the members is the tie-break the timeline sorts by
/// within one instant, so an <see cref="HttpIn"/> precedes the <see cref="Log"/> lines it scopes and
/// the terminal <see cref="Queued"/> marker never interleaves with them.
/// </summary>
public enum TraceEventKind
{
    HttpIn,
    Log,
    Span,
    Outbox,
    Publish,
    Consume,
    Queued,
    Error,
}

/// <summary>
/// One row of the timeline. <paramref name="Source"/> names where it was read from (<c>loki</c>,
/// <c>tempo</c> or <c>broker</c>), <paramref name="Link"/> is a Grafana Explore deep link (plan M8)
/// or null when there is nothing to open.
/// </summary>
public sealed record TraceEvent(
    DateTimeOffset At,
    string Source,
    string Service,
    TraceEventKind Kind,
    string Summary,
    string? TraceId,
    string? Link);

/// <summary>
/// One correlation id's timeline. A Grafana that does not answer is a state, not an exception
/// (spec §9), so <paramref name="Reachable"/> and <paramref name="Error"/> carry the failure the way
/// <c>QueuesView</c> does. <paramref name="TracesTruncated"/> says the id appeared in more distinct
/// traces than <c>EventTraceService.MaxTraces</c> allowed this build to fetch.
/// <para>
/// <paramref name="Warning"/> is a partial failure: the timeline is real but incomplete, because one
/// or more traces would not come back from Tempo. Without it a Tempo outage and a correlation id
/// whose spans have aged out look the same on screen — a timeline of log lines and nothing else.
/// </para>
/// </summary>
public sealed record TraceView(
    string CorrelationId,
    string Window,
    bool Reachable,
    string? Error,
    IReadOnlyList<string> TraceIds,
    bool TracesTruncated,
    string? Warning,
    IReadOnlyList<TraceEvent> Events);
