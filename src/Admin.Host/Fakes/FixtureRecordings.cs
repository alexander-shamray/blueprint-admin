using Admin.Host.Config;
using Admin.Host.Jobs;

namespace Admin.Host.Fakes;

/// <summary>
/// A fixture written from an HTTP answer. <paramref name="KeyOf"/> is null for a fixture that is the whole body; a
/// fixture that holds several answers names the key each one goes under, and a request it gives no key is not
/// recorded.
/// </summary>
public sealed record HttpRecording(string Fixture, Func<AdminOptions, Uri, bool> Answers, Func<Uri, string?>? KeyOf = null);

/// <summary>A fixture written from a Compose command's standard output: the arguments after <c>compose -f &lt;file&gt;</c>.</summary>
public sealed record ProcessRecording(string Fixture, IReadOnlyList<string> ComposeArguments);

/// <summary>
/// Every file in <c>Fakes/fixtures</c>, and where it comes from: the upstream answer <c>Admin:Record</c> writes it
/// from, or why it is written by hand. FixtureGateTests fails on a file in neither list, so a new fixture arrives
/// with its source named. Only a successful answer is recorded: an error answer is a state a fake chooses, not one
/// a recording run happens to meet.
/// </summary>
public static class FixtureRecordings
{
    private const string OpenApi = "/openapi/v1.json";

    public static readonly HttpRecording[] Http =
    [
        new("openapi-catalog.json", (o, uri) => FakePlatformHandler.Is(uri, o.CatalogUrl) && uri.AbsolutePath == OpenApi),
        new("openapi-ordering.json", (o, uri) => FakePlatformHandler.Is(uri, o.OrderingUrl) && uri.AbsolutePath == OpenApi),
        new("openapi-inventory.json", (o, uri) => FakePlatformHandler.Is(uri, o.InventoryUrl) && uri.AbsolutePath == OpenApi),
        new("openapi-payments.json", (o, uri) => FakePlatformHandler.Is(uri, o.PaymentsUrl) && uri.AbsolutePath == OpenApi),
        new("openapi-privacy.json", (o, uri) => FakePlatformHandler.Is(uri, o.PrivacyUrl) && uri.AbsolutePath == OpenApi),
        new("grafana-datasources.json", (o, uri) => FakePlatformHandler.Is(uri, o.GrafanaUrl) && uri.AbsolutePath == "/api/datasources"),
        new(
            "grafana-prometheus-golden-signals.json",
            (o, uri) => FakePlatformHandler.Is(uri, o.GrafanaUrl) && uri.AbsolutePath.EndsWith("/api/v1/query", StringComparison.Ordinal),
            FakeGrafana.GoldenSignalOf),
        new("mailpit-messages.json", (o, uri) => FakePlatformHandler.Is(uri, o.MailSinkUrl) && uri.AbsolutePath == "/api/v1/messages"),
        new(
            "mailpit-message.json",
            (o, uri) => FakePlatformHandler.Is(uri, o.MailSinkUrl) && FakeMailSink.MessageIdOf(uri.AbsolutePath) is not null,
            uri => FakeMailSink.MessageIdOf(uri.AbsolutePath)),
        new(
            "mailpit-headers.json",
            (o, uri) => FakePlatformHandler.Is(uri, o.MailSinkUrl) && FakeMailSink.HeadersIdOf(uri.AbsolutePath) is not null,
            uri => FakeMailSink.HeadersIdOf(uri.AbsolutePath)),
    ];

    /// <summary>
    /// The reads ComposeService and BrokerService make, each in the form run-locally.md gives it.
    /// </summary>
    public static readonly ProcessRecording[] Process =
    [
        new("compose-ps.jsonl", ["ps", "-a", "--format", "json"]),
        new("rabbitmq-exchanges.json", ["exec", "-T", "rabbitmq", "rabbitmqctl", "list_exchanges", "name", "type", "--formatter", "json"]),
        new("rabbitmq-bindings.json", ["exec", "-T", "rabbitmq", "rabbitmqctl", "list_bindings", "source_name", "source_kind", "destination_name", "destination_kind", "routing_key", "--formatter", "json"]),
        new("rabbitmq-permissions.json", ["exec", "-T", "rabbitmq", "rabbitmqctl", "list_permissions", "--formatter", "json"]),
    ];

    /// <summary>Fixtures no recording run writes, each with the reason.</summary>
    public static readonly Dictionary<string, string> HandWritten = new(StringComparer.Ordinal)
    {
        ["grafana-loki-query.json"] =
            "one publish that failed with 503 and then succeeded on retry: a state a real stack is not easily put in, "
            + "and FakeGrafana rewrites its correlation id and pins its two trace ids",
        ["grafana-tempo-trace.json"] =
            "the trace of that same publish, under the trace id FakeGrafana.RecordedTraceId names, carried across the outbox "
            + "by four spans of a live publish recorded on 2026-10-18",
        ["grafana-prometheus-rules.json"] =
            "the shape /api/v1/rules answers, over rules named in platform-alerts.yaml, with one firing and one pending so "
            + "the alert strip has each state: a stack that is up and healthy lists every rule inactive. ContactReadRefused and "
            + "ErrorRateService are listed so the Runbooks screen's fake causes have a rule to fire (FakeGrafana.Rules)",
        ["rabbitmq-queues.json"] =
            "the list_queues answer recorded on 2026-10-04, plus the privacy queues PlatformQueues.Table gained since "
            + "(empty, as a live stack listed them on 2026-10-15), plus an ordering-catalog-events_error queue holding "
            + "one message, plus a bff-order-events_skipped queue holding one, so the Broker screen has one parked queue "
            + "of each kind to mark: a state a clean stack is not in",
    };

    public static HttpRecording? For(AdminOptions options, Uri uri) => Http.FirstOrDefault(r => r.Answers(options, uri));

    public static ProcessRecording? For(RepoPaths paths, ProcessSpec spec)
    {
        IReadOnlyList<string> args = spec.Arguments;

        bool compose = spec.FileName == "docker"
            && args.Count > 3
            && args[0] == "compose"
            && args[1] == "-f"
            && args[2] == paths.ComposeFile;

        return compose ? Process.FirstOrDefault(r => args.Skip(3).SequenceEqual(r.ComposeArguments, StringComparer.Ordinal)) : null;
    }
}
