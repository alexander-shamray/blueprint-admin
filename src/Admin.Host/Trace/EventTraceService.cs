using System.Globalization;
using Admin.Host.Broker;
using Admin.Host.Config;
using Admin.Host.Telemetry;
using Microsoft.Extensions.Options;

namespace Admin.Host.Trace;

/// <summary>
/// Builds one correlation id's timeline from Loki's lines, Tempo's spans and the broker's projection
/// snapshot (spec §5.9, as corrected by plan M2). The timeline ends at the outbox on purpose: the
/// backend's outbox row carries no trace context, so the publish starts a new trace that this
/// correlation id cannot reach, and the terminal <see cref="TraceEventKind.Queued"/> event says so
/// in words rather than letting the silence read as "nothing happened".
/// </summary>
public sealed class EventTraceService(GrafanaClient grafana, BrokerService broker, IOptions<AdminOptions> options, TimeProvider time)
{
    /// <summary>A correlation id reused across many requests must not turn one screen into a hundred Tempo fetches.</summary>
    public const int MaxTraces = 10;

    public const int MaxLines = 1000;

    /// <summary>
    /// Measured 2026-09-16 (plan M5): CorrelationId is Loki structured metadata, the line body is the
    /// formatted message, and a <c>| json</c> stage stamps <c>__error__</c> on every record. Owner of
    /// the scope key: blueprint-backend <c>Common.Web.CorrelationIdExtensions</c> (the literal
    /// "CorrelationId"). The caller has already validated the id against
    /// <see cref="Admin.Host.Api.CorrelationId.IsAdoptable"/>, whose alphabet holds no quote, brace,
    /// backslash or pipe — that validation is what makes this interpolation safe.
    /// </summary>
    public static string LogQl(string correlationId) =>
        $$""""
        {service_name=~".+"} | CorrelationId = "{{correlationId}}"
        """";

    public async Task<TraceView> BuildAsync(string correlationId, TimeSpan window, CancellationToken cancellationToken)
    {
        string windowText = TraceWindow.Format(window);
        DateTimeOffset now = time.GetUtcNow();
        string logQl = LogQl(correlationId);

        // One over the cap, so the boundary is detectable: at exactly MaxLines there is no way to tell
        // a timeline that happened to end there from one Loki stopped counting.
        LokiResult loki = await grafana.QueryAsync(logQl, now - window, now, MaxLines + 1, cancellationToken);

        if (!loki.Reachable)
        {
            return new TraceView(correlationId, windowText, false, loki.Error, [], false, null, []);
        }

        // Sorted before the cap is applied: Loki answers stream by stream, not in time order, so
        // taking the head of the flattened list would drop the newest lines from a later stream while
        // keeping older ones — and with them, the trace ids only they carry.
        LokiLine[] newestFirst = [.. loki.Lines.OrderByDescending(line => line.At)];
        bool linesTruncated = newestFirst.Length > MaxLines;
        IReadOnlyList<LokiLine> lines = linesTruncated ? [.. newestFirst.Take(MaxLines)] : newestFirst;

        string[] allTraceIds = lines
            .Where(line => !string.IsNullOrEmpty(line.TraceId))
            .Select(line => line.TraceId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] traceIds = [.. allTraceIds.Take(MaxTraces)];
        bool truncated = allTraceIds.Length > MaxTraces;

        DatasourceUids uids = await grafana.UidsAsync(cancellationToken);

        // With no Tempo uid every fetch would fail identically — and because an incomplete datasource
        // list is deliberately not cached, each would re-resolve /api/datasources first, up to ten
        // times for one screen. The answer is already known here, so it is written out once per trace.
        TempoResult[] traces = uids.Tempo is null
            ? [.. traceIds.Select(_ => new TempoResult(false, uids.Error ?? "Grafana has no Tempo datasource.", []))]
            : await Task.WhenAll(traceIds.Select(id => grafana.TraceAsync(id, cancellationToken)));

        QueuesView queues = await broker.QueuesAsync(cancellationToken);

        string grafanaUrl = options.Value.GrafanaUrl;
        List<TraceEvent> events = [];

        foreach (LokiLine line in lines)
        {
            events.Add(new TraceEvent(
                line.At,
                "loki",
                line.Service,
                IsFailure(line.Level) ? TraceEventKind.Error : TraceEventKind.Log,
                line.Message,
                line.TraceId,
                uids.Loki is { } lokiUid ? ExploreLink.Loki(grafanaUrl, lokiUid, logQl, windowText) : null));
        }

        // A trace that failed to fetch contributes nothing; its Loki lines are already above.
        foreach (TempoSpan span in traces.Where(trace => trace.Reachable).SelectMany(trace => trace.Spans))
        {
            events.Add(new TraceEvent(
                span.At,
                "tempo",
                span.Service,
                SpanRecogniser.Kind(span),
                $"{span.Name} ({span.Duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)} ms)",
                span.TraceId,
                uids.Tempo is { } tempoUid ? ExploreLink.Tempo(grafanaUrl, tempoUid, span.TraceId, windowText) : null));
        }

        List<TraceEvent> ordered = [.. events.OrderBy(e => e.At).ThenBy(e => e.Kind)];

        // Stamped now, not at `now`: the broker read is a `docker compose exec` that can take tens of
        // seconds, so the snapshot describes the queue as of here, and a timestamp from before the
        // reads would sit earlier than spans the same request returned.
        DateTimeOffset snapshotAt = time.GetUtcNow();

        // Appended after the sort, never sorted into the middle: it is the end of what this id can see.
        bool handedToTheBroker = ordered.Any(e => e.Kind is TraceEventKind.Outbox or TraceEventKind.Publish);
        ordered.Add(new TraceEvent(snapshotAt, "broker", BrokerService.Service, TraceEventKind.Queued, QueuedSummary(queues, handedToTheBroker), null, null));

        return new TraceView(correlationId, windowText, true, null, traceIds, truncated, Warnings(traceIds, traces, linesTruncated, windowText), ordered);
    }

    /// <summary>
    /// What to say when some traces did not come back. A 404 is ordinary — Tempo's retention is
    /// shorter than Loki's, so an older trace is simply gone — but an outage or a missing datasource
    /// reaches here identically, and silently dropping both would present a log-only timeline as
    /// though it were complete.
    /// </summary>
    private static string? Warnings(string[] traceIds, TempoResult[] traces, bool linesTruncated, string windowText)
    {
        string[] both = [.. new[] { LineWarning(linesTruncated, windowText), TempoWarning(traceIds, traces) }.OfType<string>()];

        return both.Length == 0 ? null : string.Join(" ", both);
    }

    /// <summary>
    /// Said when Loki had more to give than the cap allows. Without it a noisy or reused correlation
    /// id reads as a complete timeline, and the trace ids past the cap — and so their spans — are gone
    /// with no sign that they existed.
    /// </summary>
    private static string? LineWarning(bool linesTruncated, string windowText) =>
        linesTruncated
            ? $"More than {MaxLines} log lines carry this correlation id in the last {windowText}; only the first {MaxLines} are shown, and traces mentioned only in the rest are missing."
            : null;

    private static string? TempoWarning(string[] traceIds, TempoResult[] traces)
    {
        string[] reasons = [.. traces.Where(t => !t.Reachable).Select(t => t.Error ?? "no reason given").Distinct(StringComparer.Ordinal)];

        if (reasons.Length == 0)
        {
            return null;
        }

        int failed = traces.Count(t => !t.Reachable);

        return $"{failed} of {traceIds.Length} trace{(traceIds.Length == 1 ? "" : "s")} could not be read from Tempo, "
            + $"so spans for {(failed == 1 ? "it" : "them")} are missing: {string.Join("; ", reasons)}";
    }

    /// <summary>Owner of the level spellings: the OTLP <c>severity_text</c> Loki carries as a label (plan M5).</summary>
    private static bool IsFailure(string? level) =>
        level is not null && (level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || level.Equals("Critical", StringComparison.OrdinalIgnoreCase)
            || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The terminal marker's words. It states the projection queue and its depth, then why the
    /// timeline stops here rather than continuing into the consume side (plan M2).
    /// </summary>
    private static string QueuedSummary(QueuesView queues, bool handedToTheBroker)
    {
        ProjectionDrain projection = queues.Projection;

        string depth = !queues.Reachable ? $"broker not reachable ({queues.Error})"
            : !projection.Found ? "queue not declared"
            : projection.Messages is not { } messages ? "depth unknown"
            : projection.Drained ? "0 messages (drained)"
            : $"{messages} message{(messages == 1 ? "" : "s")} waiting";

        // A drained queue whose error queue holds messages is not a projection that succeeded, and
        // spec §5.9 step 3 promises a message parked in an _error queue shows here rather than as
        // silence. Reporting only the main queue would call that case "drained".
        string parked = queues.Queues
            .FirstOrDefault(q => q.Name == projection.Queue + BrokerService.ErrorSuffix) is { Messages: > 0 } errorQueue
            ? $", {errorQueue.Messages} parked in {errorQueue.Name}"
            : "";

        // Only claimed when the timeline actually shows a handover. A read-only request, or one whose
        // lines have aged out, never wrote an outbox row, and telling its operator about "the publish"
        // would invent an event this console has no evidence for.
        string why = handedToTheBroker
            ? "The publish runs in a new trace — the outbox carries no trace context, so the consume "
                + "side is not joinable by this correlation id."
            : "No outbox write appears in this timeline, so nothing here was handed to the broker; the "
                + "queue is shown because a handover would not be joinable by this correlation id either.";

        return $"{projection.Queue}: {depth}{parked}. {why}";
    }
}
