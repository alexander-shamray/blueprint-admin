using Admin.Host.Telemetry;
using Admin.Host.Trace;
using Shouldly;

namespace Admin.Host.Tests.Trace;

public sealed class SpanRecogniserTests
{
    /// <summary>Attributes are written as <c>key=value</c> pairs joined by <c>;</c> so a theory row stays serializable.</summary>
    private static TempoSpan Span(string name, string kind, string attributes, bool failed = false)
    {
        Dictionary<string, string> parsed = [];

        foreach (string pair in attributes.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            parsed[parts[0]] = parts.Length == 2 ? parts[1] : "";
        }

        return new TempoSpan(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1",
            "00f067aa0ba902b7",
            null,
            "Catalog.Api",
            name,
            kind,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(5),
            parsed,
            failed);
    }

    public static TheoryData<string, string, string, TraceEventKind> Rules => new()
    {
        { "GET /api/v1/products", "SPAN_KIND_SERVER", "http.route=/api/v1/products", TraceEventKind.HttpIn },
        { "catalog-events send", "SPAN_KIND_PRODUCER", "messaging.system=rabbitmq;messaging.operation=publish", TraceEventKind.Publish },
        { "ordering-catalog-events receive", "SPAN_KIND_CONSUMER", "messaging.operation=receive", TraceEventKind.Consume },
        { "ProductCreated process", "SPAN_KIND_CONSUMER", "messaging.operation=process", TraceEventKind.Consume },
        { "ordering-catalog-events", "SPAN_KIND_CONSUMER", "messaging.system=rabbitmq", TraceEventKind.Consume },
        { "INSERT catalog", "SPAN_KIND_CLIENT", "db.system=mssql;db.statement=INSERT INTO dbo.OutboxMessages", TraceEventKind.Outbox },
        { "catalog.dbo.OutboxMessages", "SPAN_KIND_CLIENT", "db.system=mssql", TraceEventKind.Outbox },
        { "INSERT catalog", "SPAN_KIND_CLIENT", "db.system.name=mssql;db.collection.name=OutboxMessages", TraceEventKind.Outbox },
        { "SELECT catalog", "SPAN_KIND_CLIENT", "db.system=mssql;db.statement=SELECT * FROM dbo.Products", TraceEventKind.Span },
        // A span that said which table it touched is taken at its word, whatever it happens to be called.
        { "catalog.dbo.OutboxMessages", "SPAN_KIND_CLIENT", "db.system=mssql;db.statement=SELECT * FROM dbo.Products", TraceEventKind.Span },
        { "GET /health", "SPAN_KIND_SERVER", "http.request.method=GET", TraceEventKind.Span },
        { "something nobody has instrumented yet", "SPAN_KIND_INTERNAL", "", TraceEventKind.Span },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void A_span_is_recognised_by_its_attributes_not_its_name(string name, string kind, string attributes, TraceEventKind expected) =>
        SpanRecogniser.Kind(Span(name, kind, attributes)).ShouldBe(expected);

    [Fact]
    public void A_failed_span_is_an_error_even_when_a_later_rule_would_also_match()
    {
        TempoSpan failed = Span("GET /api/v1/products", "SPAN_KIND_SERVER", "http.route=/api/v1/products", failed: true);

        SpanRecogniser.Kind(failed).ShouldBe(TraceEventKind.Error);
        SpanRecogniser.Table[0].Kind.ShouldBe(TraceEventKind.Error);
    }

    [Fact]
    public void The_first_matching_rule_wins_so_a_publish_is_never_read_as_a_bare_consumer_span()
    {
        TempoSpan publish = Span("catalog-events send", "SPAN_KIND_CONSUMER", "messaging.system=rabbitmq;messaging.operation=publish");

        SpanRecogniser.Kind(publish).ShouldBe(TraceEventKind.Publish);
    }

    [Fact]
    public void An_unrecognised_span_is_shown_as_a_plain_span_rather_than_throwing()
    {
        TempoSpan unknown = Span("", "", "");

        Should.NotThrow(() => SpanRecogniser.Kind(unknown)).ShouldBe(TraceEventKind.Span);
    }

    [Fact]
    public void Every_rule_says_why_it_exists_so_a_renamed_span_is_a_one_line_change()
    {
        SpanRecogniser.Table.ShouldAllBe(rule => rule.Why.Length > 20);
        SpanRecogniser.Table.Length.ShouldBe(6);
    }
}
