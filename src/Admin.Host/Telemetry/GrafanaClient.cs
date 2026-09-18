using System.Globalization;
using System.Text.Json;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Telemetry;

/// <summary>
/// Reads Loki, Tempo and Prometheus through Grafana's datasource proxy (measured 2026-09-16, plan M7:
/// <c>POST /api/ds/query</c> answers Grafana data frames instead of Loki's and Tempo's own
/// documented shapes, so the proxy path is used everywhere here). Every failure is turned into a
/// <c>Reachable: false</c> state (spec §9); nothing is thrown to the endpoint.
/// </summary>
public sealed class GrafanaClient(HttpClient http, IOptions<AdminOptions> options)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    // Cached per datasource, and only once resolved: a Grafana that was down at first use, or one
    // that has not provisioned a datasource yet, must be asked again for what it did not answer —
    // without that absence making every read of the datasources it did answer re-resolve too.
    private string? lokiUid;
    private string? tempoUid;
    private string? prometheusUid;

    public async Task<DatasourceUids> UidsAsync(CancellationToken cancellationToken)
    {
        if (lokiUid is not null && tempoUid is not null && prometheusUid is not null)
        {
            return new DatasourceUids(lokiUid, tempoUid, prometheusUid);
        }

        AdminOptions o = options.Value;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Cached($"Admin:GrafanaUrl '{o.GrafanaUrl}' is not an absolute http(s) URL.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Cached($"Grafana answered {(int)response.StatusCode} for its datasource list.");
            }

            using JsonDocument document = JsonDocument.Parse(body);

            string? loki = null;
            string? tempo = null;
            string? prometheus = null;

            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string? type = entry.GetProperty("type").GetString();
                string? uid = entry.GetProperty("uid").GetString();

                switch (type)
                {
                    case "loki":
                        loki = uid;
                        break;
                    case "tempo":
                        tempo = uid;
                        break;
                    case "prometheus":
                        prometheus = uid;
                        break;
                }
            }

            lokiUid ??= loki;
            tempoUid ??= tempo;
            prometheusUid ??= prometheus;

            return Cached(null);
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return Cached(e.Message);
        }
    }

    private DatasourceUids Cached(string? error) => new(lokiUid, tempoUid, prometheusUid, error);

    /// <summary>
    /// One datasource's uid: the cached one when it has resolved, so a missing sibling costs this
    /// read nothing, otherwise a fresh resolve and the reason it did not answer.
    /// </summary>
    private async Task<(string? Uid, string? Error)> UidAsync(Func<DatasourceUids, string?> pick, CancellationToken cancellationToken)
    {
        if (pick(Cached(null)) is { } cached)
        {
            return (cached, null);
        }

        DatasourceUids resolved = await UidsAsync(cancellationToken);

        return (pick(resolved), resolved.Error);
    }

    /// <summary>
    /// A Loki range query through the datasource proxy. <paramref name="from"/>/<paramref name="to"/>
    /// are converted to nanoseconds (measured 2026-09-16, plan M7): <c>from.ToUnixTimeMilliseconds() * 1_000_000L</c>.
    /// </summary>
    public async Task<LokiResult> QueryAsync(string logQl, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        (string? loki, string? error) = await UidAsync(u => u.Loki, cancellationToken);

        if (loki is null)
        {
            return new LokiResult(false, error ?? "Grafana has no Loki datasource.", []);
        }

        AdminOptions o = options.Value;
        long start = from.ToUnixTimeMilliseconds() * 1_000_000L;
        long end = to.ToUnixTimeMilliseconds() * 1_000_000L;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/{loki}/loki/api/v1/query_range" +
            $"?query={Uri.EscapeDataString(logQl)}&start={start}&end={end}&limit={limit}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new LokiResult(false, $"Admin:GrafanaUrl '{o.GrafanaUrl}' is not an absolute http(s) URL.", []);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new LokiResult(false, $"Loki answered {(int)response.StatusCode}: {body}", []);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            List<LokiLine> lines = [];

            foreach (JsonElement stream in document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
            {
                JsonElement labels = stream.GetProperty("stream");
                string service = labels.TryGetProperty("service_name", out JsonElement serviceElement) ? serviceElement.GetString() ?? "" : "";
                string? level = labels.TryGetProperty("severity_text", out JsonElement levelElement) ? levelElement.GetString() : null;
                string? traceId = labels.TryGetProperty("trace_id", out JsonElement traceIdElement) ? traceIdElement.GetString() : null;

                foreach (JsonElement value in stream.GetProperty("values").EnumerateArray())
                {
                    long ns = long.Parse(value[0].GetString()!, CultureInfo.InvariantCulture);
                    string message = value[1].GetString() ?? "";
                    lines.Add(new LokiLine(At(ns), service, level, message, traceId));
                }
            }

            return new LokiResult(true, null, lines);
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new LokiResult(false, e.Message, []);
        }
    }

    /// <summary>
    /// A Tempo trace fetch through the datasource proxy. Ids in the response are base64 (measured
    /// 2026-09-16, plan M6) and are decoded to lowercase hex here; a malformed id is skipped, not fatal.
    /// </summary>
    public async Task<TempoResult> TraceAsync(string traceIdHex, CancellationToken cancellationToken)
    {
        (string? tempo, string? error) = await UidAsync(u => u.Tempo, cancellationToken);

        if (tempo is null)
        {
            return new TempoResult(false, error ?? "Grafana has no Tempo datasource.", []);
        }

        AdminOptions o = options.Value;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/{tempo}/api/traces/{Uri.EscapeDataString(traceIdHex)}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new TempoResult(false, $"Admin:GrafanaUrl '{o.GrafanaUrl}' is not an absolute http(s) URL.", []);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new TempoResult(false, $"Tempo answered {(int)response.StatusCode}: {body}", []);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            List<TempoSpan> spans = [];

            foreach (JsonElement batch in document.RootElement.GetProperty("batches").EnumerateArray())
            {
                string service = ServiceNameOf(batch);

                foreach (JsonElement scopeSpan in batch.GetProperty("scopeSpans").EnumerateArray())
                {
                    foreach (JsonElement span in scopeSpan.GetProperty("spans").EnumerateArray())
                    {
                        TempoSpan? parsed = ParseSpan(span, service);

                        if (parsed is not null)
                        {
                            spans.Add(parsed);
                        }
                    }
                }
            }

            return new TempoResult(true, null, spans);
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new TempoResult(false, e.Message, []);
        }
    }

    /// <summary>
    /// A Prometheus instant query through the datasource proxy, evaluated now. The answer is
    /// Prometheus's own <c>/api/v1/query</c> envelope; each series is read for its
    /// <c>service_name</c> label, which is what every <see cref="GoldenSignals"/> query groups by.
    /// </summary>
    public async Task<PrometheusResult> InstantQueryAsync(string promQl, CancellationToken cancellationToken)
    {
        (string? prometheus, string? error) = await UidAsync(u => u.Prometheus, cancellationToken);

        if (prometheus is null)
        {
            return new PrometheusResult(false, error ?? "Grafana has no Prometheus datasource.", []);
        }

        AdminOptions o = options.Value;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/{prometheus}/api/v1/query" +
            $"?query={Uri.EscapeDataString(promQl)}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new PrometheusResult(false, $"Admin:GrafanaUrl '{o.GrafanaUrl}' is not an absolute http(s) URL.", []);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new PrometheusResult(false, $"Prometheus answered {(int)response.StatusCode}: {body}", []);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            List<PrometheusSample> samples = [];

            foreach (JsonElement series in document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
            {
                JsonElement metric = series.GetProperty("metric");
                string service = metric.TryGetProperty("service_name", out JsonElement serviceElement) ? serviceElement.GetString() ?? "" : "";
                string? text = series.GetProperty("value")[1].GetString();
                double? value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
                    ? parsed
                    : null;

                samples.Add(new PrometheusSample(service, value));
            }

            return new PrometheusResult(true, null, samples);
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new PrometheusResult(false, e.Message, []);
        }
    }

    /// <summary>
    /// Every failure this client can turn into a <c>Reachable: false</c> state rather than throw to
    /// the endpoint: the network/timeout set <see cref="Admin.Host.Identity.TokenService"/> uses,
    /// widened for a well-formed-JSON-but-wrong-shape 200 (a Grafana version that renames a field, or
    /// an error body on a success status) the way <c>TokenService.GetAsync</c>'s last catch clause does.
    /// <para>
    /// The shape failures are not only missing or mistyped properties: a nanosecond value too large
    /// for <see cref="long"/> or for <see cref="DateTimeOffset"/> throws <see cref="OverflowException"/>
    /// or <see cref="ArgumentOutOfRangeException"/>, and a <c>values</c> entry shorter than
    /// <c>[timestamp, line]</c> throws <see cref="IndexOutOfRangeException"/>. A malformed answer is
    /// an unreachable Grafana (spec §9), never a 500 from this console.
    /// </para>
    /// </summary>
    private static bool IsUnreachable(Exception e, CancellationToken cancellationToken) =>
        (e is HttpRequestException or JsonException or OperationCanceledException or KeyNotFoundException
            or InvalidOperationException or FormatException or ArgumentException or OverflowException
            or IndexOutOfRangeException or IOException)
        && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// A Unix nanosecond timestamp as a <see cref="DateTimeOffset"/>, keeping the 100-nanosecond tick
    /// precision the type has. Going through milliseconds would collapse distinct events inside one
    /// millisecond to the same instant, and the timeline's tie-break is by kind, not by time — so a
    /// span and the line it scopes could come back in the wrong order.
    /// </summary>
    private static DateTimeOffset At(long unixNanoseconds) => DateTimeOffset.UnixEpoch.AddTicks(unixNanoseconds / 100);

    /// <summary>Converts raw bytes (for example a W3C trace or span id) to lowercase hex.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Decodes a Tempo base64 id (plan M6) to lowercase hex; null or malformed input yields null rather than throwing.</summary>
    internal static string? FromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Hex(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static TempoSpan? ParseSpan(JsonElement span, string service)
    {
        string? traceId = FromBase64(span.GetProperty("traceId").GetString());
        string? spanId = FromBase64(span.GetProperty("spanId").GetString());

        if (traceId is null || spanId is null)
        {
            return null;
        }

        string? parentSpanId = span.TryGetProperty("parentSpanId", out JsonElement parentElement)
            ? FromBase64(parentElement.GetString())
            : null;

        long startNs = long.Parse(span.GetProperty("startTimeUnixNano").GetString()!, CultureInfo.InvariantCulture);
        long endNs = long.Parse(span.GetProperty("endTimeUnixNano").GetString()!, CultureInfo.InvariantCulture);

        Dictionary<string, string> attributes = [];

        if (span.TryGetProperty("attributes", out JsonElement attributesElement))
        {
            foreach (JsonElement attribute in attributesElement.EnumerateArray())
            {
                attributes[attribute.GetProperty("key").GetString() ?? ""] = AttributeValue(attribute.GetProperty("value"));
            }
        }

        bool failed = span.TryGetProperty("status", out JsonElement statusElement) &&
            statusElement.TryGetProperty("code", out JsonElement codeElement) &&
            codeElement.GetString() == "STATUS_CODE_ERROR";

        return new TempoSpan(
            traceId,
            spanId,
            parentSpanId,
            service,
            span.GetProperty("name").GetString() ?? "",
            span.GetProperty("kind").GetString() ?? "",
            At(startNs),
            TimeSpan.FromTicks((endNs - startNs) / 100),
            attributes,
            failed);
    }

    private static string ServiceNameOf(JsonElement batch)
    {
        if (batch.TryGetProperty("resource", out JsonElement resource) &&
            resource.TryGetProperty("attributes", out JsonElement attributes))
        {
            foreach (JsonElement attribute in attributes.EnumerateArray())
            {
                if (attribute.GetProperty("key").GetString() == "service.name")
                {
                    return AttributeValue(attribute.GetProperty("value"));
                }
            }
        }

        return "";
    }

    /// <summary>Flattens a Tempo/OTLP attribute value's tagged union (plan M6) to a string.</summary>
    private static string AttributeValue(JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out JsonElement stringValue))
        {
            return stringValue.GetString() ?? "";
        }

        if (value.TryGetProperty("intValue", out JsonElement intValue))
        {
            return intValue.GetString() ?? "";
        }

        if (value.TryGetProperty("boolValue", out JsonElement boolValue))
        {
            return boolValue.GetBoolean() ? "true" : "false";
        }

        if (value.TryGetProperty("doubleValue", out JsonElement doubleValue))
        {
            return doubleValue.GetDouble().ToString(CultureInfo.InvariantCulture);
        }

        return "";
    }
}
