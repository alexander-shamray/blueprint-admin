using System.Globalization;
using System.Text.Json;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Telemetry;

/// <summary>
/// Reads Loki and Tempo through Grafana's datasource proxy (measured 2026-09-16, plan M7:
/// <c>POST /api/ds/query</c> answers Grafana data frames instead of Loki's and Tempo's own
/// documented shapes, so the proxy path is used everywhere here). Every failure is turned into a
/// <c>Reachable: false</c> state (spec §9); nothing is thrown to the endpoint.
/// </summary>
public sealed class GrafanaClient(HttpClient http, IOptions<AdminOptions> options)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Guarded so a failed resolve is never cached: a Grafana that was down at first use must be retried.</summary>
    private DatasourceUids? uidsCache;

    public async Task<DatasourceUids> UidsAsync(CancellationToken cancellationToken)
    {
        if (uidsCache is { } cached)
        {
            return cached;
        }

        AdminOptions o = options.Value;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new DatasourceUids(null, null, null);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new DatasourceUids(null, null, null);
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

            DatasourceUids result = new(loki, tempo, prometheus);

            if (result.Loki is not null)
            {
                uidsCache = result;
            }

            return result;
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new DatasourceUids(null, null, null);
        }
    }

    /// <summary>
    /// A Loki range query through the datasource proxy. <paramref name="from"/>/<paramref name="to"/>
    /// are converted to nanoseconds (measured 2026-09-16, plan M7): <c>from.ToUnixTimeMilliseconds() * 1_000_000L</c>.
    /// </summary>
    public async Task<LokiResult> QueryAsync(string logQl, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        DatasourceUids uids = await UidsAsync(cancellationToken);

        if (uids.Loki is null)
        {
            return new LokiResult(false, "Grafana has no Loki datasource.", []);
        }

        AdminOptions o = options.Value;
        long start = from.ToUnixTimeMilliseconds() * 1_000_000L;
        long end = to.ToUnixTimeMilliseconds() * 1_000_000L;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/{uids.Loki}/loki/api/v1/query_range" +
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
                    lines.Add(new LokiLine(DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000), service, level, message, traceId));
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
        DatasourceUids uids = await UidsAsync(cancellationToken);

        if (uids.Tempo is null)
        {
            return new TempoResult(false, "Grafana has no Tempo datasource.", []);
        }

        AdminOptions o = options.Value;
        string url = $"{o.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/{uids.Tempo}/api/traces/{Uri.EscapeDataString(traceIdHex)}";

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
            or IndexOutOfRangeException)
        && !cancellationToken.IsCancellationRequested;

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
            DateTimeOffset.FromUnixTimeMilliseconds(startNs / 1_000_000),
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
