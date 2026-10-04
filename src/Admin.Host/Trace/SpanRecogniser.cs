using System.Globalization;
using Admin.Host.Telemetry;

namespace Admin.Host.Trace;

/// <summary>
/// One row of <see cref="SpanRecogniser.Table"/>: why it exists, what it matches, what it decides, and the words the
/// timeline puts before the span's own name when the row names a step of the platform rather than a kind of span.
/// </summary>
public sealed record SpanRule(string Why, Func<TempoSpan, bool> Matches, TraceEventKind Kind, string? Label = null);

/// <summary>
/// Decides what a Tempo span *is* from its attributes rather than its name, so that a renamed span
/// stays recognised (spec §5.9). The table is walked in order and the first match wins; a span that
/// matches nothing is a plain <see cref="TraceEventKind.Span"/> and is still shown, never dropped
/// and never an exception.
/// </summary>
public static class SpanRecogniser
{
    /// <summary>
    /// Owner of the literal: blueprint-backend <c>Common.Infrastructure.Outbox.OutboxTable</c>, the
    /// table <c>OutboxMessage</c> maps to.
    /// </summary>
    internal const string OutboxTable = "OutboxMessages";

    /// <summary>
    /// The keys a database span can name its statement or its table under. Both the current and the
    /// superseded OpenTelemetry semantic-convention spellings are checked, because the exporter's
    /// version is the backend's choice, not this console's.
    /// </summary>
    private static readonly string[] DatabaseTargetKeys =
        ["db.statement", "db.query.text", "db.collection.name", "db.sql.table"];

    /// <summary>
    /// The URNs MassTransit stamps in <c>messaging.masstransit.message_types</c>, <c>urn:message:</c> then the
    /// contract's namespace and name. Owner of each contract: blueprint-backend
    /// <c>Common.Contracts.Inventory.V1.ReserveStock</c> and <c>Common.Contracts.Payments.V1.AuthorisePayment</c>.
    /// </summary>
    internal const string ReserveStockUrn = "urn:message:Common.Contracts.Inventory.V1:ReserveStock";

    internal const string AuthorisePaymentUrn = "urn:message:Common.Contracts.Payments.V1:AuthorisePayment";

    /// <summary>Owner: blueprint-backend <c>Common.Contracts.Ordering.V1.OrderConfirmed</c>, which Shipping despatches on.</summary>
    internal const string OrderConfirmedUrn = "urn:message:Common.Contracts.Ordering.V1:OrderConfirmed";

    /// <summary>
    /// Shipping's <c>service.name</c>: blueprint-backend's <c>AddCommonWebDefaults</c> names a service after its
    /// application, and Shipping's host is the <c>Shipping.Worker</c> project. Notifications consumes the same event,
    /// so the service is what tells the despatch apart.
    /// </summary>
    internal const string ShippingService = "Shipping.Worker";

    /// <summary>
    /// The MassTransit version whose span tags these rows read: blueprint-backend <c>Directory.Packages.props</c>.
    /// MassTransit 8.5.3's <c>LogContextActivityExtensions</c> tags every send and publish
    /// <c>messaging.operation=send</c>, every consumer and saga step <c>process</c>, and a saga step with
    /// <c>messaging.masstransit.saga_id</c> and its <c>begin_state</c> and <c>end_state</c>. A bump re-reads them.
    /// </summary>
    internal const string MassTransitVersion = "8.5.3";

    public static readonly SpanRule[] Table =
    [
        new("A failed span is an error wherever it sits in the chain, so this rule precedes the rest.",
            span => span.Failed,
            TraceEventKind.Error),
        new("An inbound HTTP request: a server span that carries a route.",
            span => span.Kind == "SPAN_KIND_SERVER" && span.Attributes.ContainsKey("http.route"),
            TraceEventKind.HttpIn),
        new("A handover to the broker. The semantic conventions say publish; MassTransit says send for a publish "
            + "and a command alike (MassTransitVersion), and both are the message leaving the service.",
            span => Attribute(span, "messaging.operation") is "publish" or "send",
            TraceEventKind.Publish),
        new("A step of blueprint-backend's OrderFulfilmentSaga: MassTransit stamps a saga id on a state "
            + "machine's process span and on no other.",
            span => Attribute(span, "messaging.operation") == "process" && span.Attributes.ContainsKey("messaging.masstransit.saga_id"),
            TraceEventKind.Saga,
            "Ordering's fulfilment saga"),
        new("Inventory reserving an order's stock: the process span of the consumer of ReserveStock.",
            span => Processes(span, ReserveStockUrn),
            TraceEventKind.Consume,
            "Inventory reserves stock"),
        new("Payments authorising an order's payment: the process span of the consumer of AuthorisePayment.",
            span => Processes(span, AuthorisePaymentUrn),
            TraceEventKind.Consume,
            "Payments authorises the payment"),
        new("Shipping despatching a confirmed order: Shipping's process span of OrderConfirmed.",
            span => span.Service == ShippingService && Processes(span, OrderConfirmedUrn),
            TraceEventKind.Consume,
            "Shipping despatches the order"),
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

    public static TraceEventKind Kind(TempoSpan span) => Recognise(span)?.Kind ?? TraceEventKind.Span;

    /// <summary>The first row that matches, or null for a span the table does not know.</summary>
    public static SpanRule? Recognise(TempoSpan span) => Table.FirstOrDefault(rule => rule.Matches(span));

    /// <summary>
    /// The timeline's words for a span: the row's label, if any, before the span's own name; a saga step adds the
    /// state it moved between, which is what says how far the order got.
    /// </summary>
    public static string Describe(TempoSpan span)
    {
        string duration = $"({span.Duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)} ms)";
        SpanRule? rule = Recognise(span);
        string transition = rule?.Kind == TraceEventKind.Saga
            && Attribute(span, "messaging.masstransit.begin_state") is { } from
            && Attribute(span, "messaging.masstransit.end_state") is { } to
                ? $", from {from} to {to}"
                : "";

        return rule?.Label is { } label
            ? $"{label}{transition}: {span.Name} {duration}"
            : $"{span.Name} {duration}";
    }

    /// <summary>
    /// The broker queue a span names: the endpoint a receive read from, or the destination a send went to. A
    /// destination may be an exchange; the caller keeps only names it knows as queues.
    /// </summary>
    public static string? Queue(TempoSpan span) =>
        span.Attributes.ContainsKey("messaging.system") || span.Attributes.ContainsKey("messaging.operation")
            ? Attribute(span, "messaging.destination.name")
            : null;

    private static string? Attribute(TempoSpan span, string key) =>
        span.Attributes.TryGetValue(key, out string? value) ? value : null;

    /// <summary>A consumer's process span for one contract; message_types lists every type the message supports.</summary>
    private static bool Processes(TempoSpan span, string urn) =>
        Attribute(span, "messaging.operation") == "process"
        && Attribute(span, "messaging.masstransit.message_types") is { } types
        && types.Split(',').Contains(urn, StringComparer.Ordinal);

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
