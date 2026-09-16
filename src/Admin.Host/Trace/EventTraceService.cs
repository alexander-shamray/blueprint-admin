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

        LokiResult loki = await grafana.QueryAsync(logQl, now - window, now, MaxLines, cancellationToken);

        if (!loki.Reachable)
        {
            return new TraceView(correlationId, windowText, false, loki.Error, [], false, []);
        }

        string[] allTraceIds = loki.Lines
            .Where(line => !string.IsNullOrEmpty(line.TraceId))
            .OrderByDescending(line => line.At)
            .Select(line => line.TraceId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] traceIds = [.. allTraceIds.Take(MaxTraces)];
        bool truncated = allTraceIds.Length > MaxTraces;

        DatasourceUids uids = await grafana.UidsAsync(cancellationToken);
        TempoResult[] traces = await Task.WhenAll(traceIds.Select(id => grafana.TraceAsync(id, cancellationToken)));
        QueuesView queues = await broker.QueuesAsync(cancellationToken);

        string grafanaUrl = options.Value.GrafanaUrl;
        List<TraceEvent> events = [];

        foreach (LokiLine line in loki.Lines)
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

        // Appended after the sort, never sorted into the middle: it is the end of what this id can see.
        ordered.Add(new TraceEvent(now, "broker", BrokerService.Service, TraceEventKind.Queued, QueuedSummary(queues), null, null));

        return new TraceView(correlationId, windowText, true, null, traceIds, truncated, ordered);
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
    private static string QueuedSummary(QueuesView queues)
    {
        ProjectionDrain projection = queues.Projection;

        string depth = !queues.Reachable ? $"broker not reachable ({queues.Error})"
            : !projection.Found ? "queue not declared"
            : projection.Messages is not { } messages ? "depth unknown"
            : projection.Drained ? "0 messages (drained)"
            : $"{messages} message{(messages == 1 ? "" : "s")} waiting";

        return $"{projection.Queue}: {depth}. The publish runs in a new trace — the outbox carries no "
            + "trace context, so the consume side is not joinable by this correlation id.";
    }
}
