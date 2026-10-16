using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Config;
using Microsoft.Extensions.Options;
using CorrelationId = Admin.Host.Api.CorrelationId;

namespace Admin.Host.Mail;

/// <summary>
/// Reads the mail sink (Mailpit, <c>mailpit</c> in blueprint-backend's <c>deploy/compose/services/notifications.yml</c>)
/// as run-locally.md reads it by hand: the list, a message's headers and the message itself. That is all this class
/// can send, and all of it is a <c>GET</c> on <see cref="AdminOptions.MailSinkUrl"/> to a path built here, so the sink
/// is not a surface of the proxy and the console never clears it or calls its chaos API (spec §8).
/// <para>
/// The paths and shapes are those of <see cref="Image"/>'s API. Opening a message marks it read in the sink, as
/// opening it in the sink's own UI does; the list and the headers do not. Every failure is turned into a
/// <c>Reachable: false</c> state (spec §9); nothing is thrown to the endpoint.
/// </para>
/// </summary>
public sealed partial class MailSinkClient(HttpClient http, IOptions<AdminOptions> options)
{
    /// <summary>The image tag whose API this client was checked against; a drift check holds the Compose unit to it.</summary>
    public const string Image = "axllent/mailpit:v1.31.3";

    /// <summary>The messages one list shows, newest first. The sink keeps more; the list says how many.</summary>
    public const int Limit = 50;

    /// <summary>Header reads in flight at once while a list is joined to its correlation ids.</summary>
    private const int HeaderReads = 8;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Sink = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The sink's ids are short UUIDs, letters and digits alone. An id is a path segment of a URL built here, so
    /// one with anything else in it is refused before it can add a segment or a query.
    /// </summary>
    public static bool IsMessageId(string id) => MessageId().IsMatch(id);

    public async Task<MailListView> ListAsync(CancellationToken cancellationToken)
    {
        string sinkUrl = options.Value.MailSinkUrl;

        if (Base() is not { } root)
        {
            return new MailListView(false, NotAUrl(), 0, [], sinkUrl);
        }

        try
        {
            (int status, string body) = await GetAsync(new Uri(root, $"api/v1/messages?limit={Limit}"), cancellationToken);

            if (status is < 200 or >= 300)
            {
                return new MailListView(false, $"The mail sink answered {status} for its message list.", 0, [], sinkUrl);
            }

            SinkList list = JsonSerializer.Deserialize<SinkList>(body, Sink) ?? throw new JsonException("The message list is null.");
            SinkSummary[] listed = list.Messages ?? [];
            string?[] correlations = new string?[listed.Length];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, listed.Length),
                new ParallelOptions { MaxDegreeOfParallelism = HeaderReads, CancellationToken = cancellationToken },
                async (i, ct) => correlations[i] = await CorrelationIdOfAsync(root, listed[i].Id ?? "", ct));

            MailSummary[] messages = [.. listed
                .Select((m, i) => new MailSummary(
                    m.Id ?? "",
                    Box(m.From),
                    Boxes(m.To),
                    m.Subject ?? "",
                    m.Created,
                    m.Snippet ?? "",
                    m.Read,
                    correlations[i]))
                .OrderByDescending(m => m.Created)];

            return new MailListView(true, null, list.Total, messages, sinkUrl);
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new MailListView(false, e.Message, 0, [], sinkUrl);
        }
    }

    /// <summary>One message and its correlation id. The caller has checked <paramref name="id"/> with <see cref="IsMessageId"/>.</summary>
    public async Task<MailMessageRead> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsMessageId(id))
        {
            throw new ArgumentException($"'{id}' is not a mail sink message id.", nameof(id));
        }

        if (Base() is not { } root)
        {
            return new MailMessageRead(false, NotAUrl(), null);
        }

        try
        {
            (int status, string body) = await GetAsync(new Uri(root, $"api/v1/message/{id}"), cancellationToken);

            if (status is < 200 or >= 300)
            {
                return new MailMessageRead(true, $"The mail sink answered {status} for message {id}.", null);
            }

            SinkMessage message = JsonSerializer.Deserialize<SinkMessage>(body, Sink) ?? throw new JsonException("The message is null.");

            return new MailMessageRead(
                true,
                null,
                new MailMessageView(
                    id,
                    Box(message.From),
                    Boxes(message.To),
                    Boxes(message.Cc),
                    message.Subject ?? "",
                    message.Date,
                    message.Text ?? "",
                    message.Html ?? "",
                    await CorrelationIdOfAsync(root, id, cancellationToken)));
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return new MailMessageRead(false, e.Message, null);
        }
    }

    /// <summary>
    /// The message's <c>X-Correlation-Id</c>, read from its headers, which do not mark it read; null when it has none,
    /// when the backend would not adopt the value, or when the read fails, since a list without one link still stands.
    /// </summary>
    private async Task<string?> CorrelationIdOfAsync(Uri root, string id, CancellationToken cancellationToken)
    {
        if (!IsMessageId(id))
        {
            return null;
        }

        try
        {
            (int status, string body) = await GetAsync(new Uri(root, $"api/v1/message/{id}/headers"), cancellationToken);

            if (status is < 200 or >= 300)
            {
                return null;
            }

            Dictionary<string, string[]>? headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(body, Sink);
            string? value = headers?
                .Where(h => string.Equals(h.Key, CorrelationId.Header, StringComparison.OrdinalIgnoreCase))
                .SelectMany(h => h.Value)
                .FirstOrDefault();

            return value is not null && CorrelationId.IsAdoptable(value) ? value : null;
        }
        catch (Exception e) when (IsUnreachable(e, cancellationToken))
        {
            return null;
        }
    }

    private async Task<(int Status, string Body)> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        using HttpResponseMessage response = await http.GetAsync(uri, timeout.Token);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <summary>The sink's root with a trailing slash, so the relative paths above land under it; null when it is no http(s) URL.</summary>
    private Uri? Base() =>
        Uri.TryCreate(options.Value.MailSinkUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    private string NotAUrl() => $"Admin:MailSinkUrl '{options.Value.MailSinkUrl}' is not an absolute http(s) URL.";

    private static Mailbox Box(SinkAddress? address) => new(address?.Name ?? "", address?.Address ?? "");

    private static Mailbox[] Boxes(SinkAddress[]? addresses) => [.. (addresses ?? []).Select(Box)];

    private static bool IsUnreachable(Exception e, CancellationToken cancellationToken) =>
        (e is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException or IOException)
        && !cancellationToken.IsCancellationRequested;

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")]
    private static partial Regex MessageId();

    private sealed record SinkAddress(string? Name, string? Address);

    private sealed record SinkSummary(string? Id, SinkAddress? From, SinkAddress[]? To, string? Subject, DateTimeOffset Created, string? Snippet, bool Read);

    private sealed record SinkList(int Total, SinkSummary[]? Messages);

    private sealed record SinkMessage(SinkAddress? From, SinkAddress[]? To, SinkAddress[]? Cc, string? Subject, DateTimeOffset Date, string? Text, string? Html);
}
