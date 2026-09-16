using Admin.Host.Telemetry;

namespace Admin.Host.Trace;

/// <summary>One row of <see cref="SpanRecogniser.Table"/>: why it exists, what it matches, what it decides.</summary>
public sealed record SpanRule(string Why, Func<TempoSpan, bool> Matches, TraceEventKind Kind);

/// <summary>
/// Decides what a Tempo span *is* from its attributes rather than its name, so that a renamed span
/// stays recognised (spec §5.9). The table is walked in order and the first match wins; a span that
/// matches nothing is a plain <see cref="TraceEventKind.Span"/> and is still shown, never dropped
/// and never an exception.
/// </summary>
public static class SpanRecogniser
{
    /// <summary>
    /// Owner of the literal: blueprint-backend <c>Common.Infrastructure.Outbox.OutboxMessage</c> and
    /// the <c>OutboxMessages</c> table its configuration maps to.
    /// </summary>
    private const string OutboxTable = "OutboxMessages";

    /// <summary>
    /// The keys a database span can name its statement or its table under. Both the current and the
    /// superseded OpenTelemetry semantic-convention spellings are checked, because the exporter's
    /// version is the backend's choice, not this console's.
    /// </summary>
    private static readonly string[] DatabaseTargetKeys =
        ["db.statement", "db.query.text", "db.collection.name", "db.sql.table"];

    public static readonly SpanRule[] Table =
    [
        new("A failed span is an error wherever it sits in the chain, so this rule precedes the rest.",
            span => span.Failed,
            TraceEventKind.Error),
        new("An inbound HTTP request: a server span that carries a route.",
            span => span.Kind == "SPAN_KIND_SERVER" && span.Attributes.ContainsKey("http.route"),
            TraceEventKind.HttpIn),
        new("A broker publish, named by the messaging semantic conventions.",
            span => Attribute(span, "messaging.operation") == "publish",
            TraceEventKind.Publish),
        new("A broker receive or process on the consuming side.",
            span => Attribute(span, "messaging.operation") is "receive" or "process",
            TraceEventKind.Consume),
        new("A consumer span on a broker that did not stamp messaging.operation.",
            span => span.Kind == "SPAN_KIND_CONSUMER" && span.Attributes.ContainsKey("messaging.system"),
            TraceEventKind.Consume),
        new("A database span that names the outbox table. The backend starts no outbox span of its own "
            + "(plan M2), so the write to that table is the only honest marker of the handover.",
            IsOutboxWrite,
            TraceEventKind.Outbox),
    ];

    public static TraceEventKind Kind(TempoSpan span)
    {
        foreach (SpanRule rule in Table)
        {
            if (rule.Matches(span))
            {
                return rule.Kind;
            }
        }

        return TraceEventKind.Span;
    }

    private static string? Attribute(TempoSpan span, string key) =>
        span.Attributes.TryGetValue(key, out string? value) ? value : null;

    private static bool IsOutboxWrite(TempoSpan span)
    {
        if (!span.Attributes.ContainsKey("db.system") && !span.Attributes.ContainsKey("db.system.name"))
        {
            return false;
        }

        bool stamped = false;

        foreach (string key in DatabaseTargetKeys)
        {
            if (Attribute(span, key) is not { } target)
            {
                continue;
            }

            stamped = true;

            if (target.Contains(OutboxTable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Last resort, and only when no target attribute was stamped at all: instrumentation that
        // does not record the statement names a database span after the table it touched. A span
        // that *did* say which table it touched is taken at its word, so a span named for the outbox
        // while its statement names another table is not mislabelled.
        return !stamped && span.Name.Contains(OutboxTable, StringComparison.OrdinalIgnoreCase);
    }
}
