using System.Net.Http.Headers;
using System.Text;
using Admin.Host.Config;
using Admin.Host.Identity;
using Microsoft.Extensions.Options;

namespace Admin.Host.Api;

/// <summary>
/// Sends one request to the gateway, Catalog, Ordering or the BFF as a chosen identity with a
/// correlation id, and returns the answer without rewriting status or body (spec §5.7, §8).
/// </summary>
public sealed class RequestProxy(HttpClient http, TokenService tokens, IOptions<AdminOptions> options, TimeProvider time)
{
    /// <summary>Owner: blueprint-backend <c>Common.Web.CorrelationIdExtensions.Header</c>.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    public const int MaxBodyBytes = 1_048_576;

    /// <summary>Owner: <c>Common.Web.CorrelationIdExtensions.MaxSuppliedLength</c>.</summary>
    private const int MaxCorrelationIdLength = 128;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    // Set by HttpClient from the URL and the body, never by the caller.
    private static readonly HashSet<string> Dropped = new(["Host", "Content-Length", "Transfer-Encoding", "Connection"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The backend's adoption rule, <c>CorrelationIdExtensions.IsAdoptable</c>: 1-128 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsAdoptable(string id) =>
        id.Length is >= 1 and <= MaxCorrelationIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    public string? Validate(ProxyRequest request)
    {
        if (!Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return $"Method '{request.Method}' is not one of {string.Join(", ", Methods)}.";
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{request.Url}' is not an absolute http(s) URL.";
        }

        string origin = uri.GetLeftPart(UriPartial.Authority);

        if (!Surfaces().Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return $"{origin} is not one of the configured API surfaces ({string.Join(", ", Surfaces())}).";
        }

        if (request.CorrelationId is { Length: > 0 } id && !IsAdoptable(id))
        {
            return "The correlation id must be 1 to 128 ASCII letters, digits, '-' or '_'; the platform would replace any other value.";
        }

        IReadOnlyDictionary<string, string> headers = request.Headers ?? new Dictionary<string, string>();

        // HttpHeaders drops a name that is not an RFC 9110 token without saying so, which would send a different request.
        if (headers.Keys.FirstOrDefault(k => k.Length == 0 || !k.All(IsTokenChar)) is { } badName)
        {
            return $"'{badName}' is not a valid header name: use letters, digits and !#$%&'*+-.^_`|~ only.";
        }

        if (headers.Keys.Any(k => k.Equals(CorrelationHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Send the correlation id in correlationId, not as an {CorrelationHeader} header.";
        }

        // The body binds headers case-sensitively, so Content-Type and content-type can both arrive.
        if (headers.Keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return $"Header '{duplicate.Key}' is given more than once ({string.Join(", ", duplicate)}); header names ignore case.";
        }

        foreach (KeyValuePair<string, string> contentType in headers.Where(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            if (!MediaTypeHeaderValue.TryParse(contentType.Value, out MediaTypeHeaderValue? mediaType))
            {
                return $"Content-Type '{contentType.Value}' is not a media type.";
            }

            // The body is sent as UTF-8 bytes; another declared charset would make the upstream misread them.
            if (mediaType.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return $"Content-Type '{contentType.Value}' names charset {charset}; the body is sent as UTF-8.";
            }
        }

        if (request.Identity is { Username: { Length: > 0 } username, Password: null }
            && !RealmUsers.Of(options.Value).Any(u => u.Username == username))
        {
            return $"'{username}' is not a configured realm user; send a password to use it.";
        }

        return null;
    }

    public async Task<ProxyResult> SendAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        string correlationId = request.CorrelationId is { Length: > 0 } given ? given : Guid.NewGuid().ToString("N");
        TokenOutcome? token = await tokens.ForAsync(request.Identity, cancellationToken);

        // Started after the token: a cold Keycloak grant is not part of the platform's answer time.
        long started = time.GetTimestamp();

        switch (token)
        {
            case TokenRejected rejected:
                return new ProxyTokenRejected(rejected.Status, rejected.Body, correlationId);
            case UnknownUser unknown:
                return new ProxyTokenRejected(400, $"'{unknown.Username}' is not a configured realm user.", correlationId);
            case KeycloakUnreachable unreachable:
                return new ProxyUnreached($"Keycloak did not answer: {unreachable.Error}", Elapsed(started), correlationId);
            default:
                using (HttpRequestMessage message = Build(request, token as TokenIssued, correlationId))
                {
                    return await SendAsync(message, started, correlationId, cancellationToken);
                }
        }
    }

    private static HttpRequestMessage Build(ProxyRequest request, TokenIssued? token, string correlationId)
    {
        HttpRequestMessage message = new(new HttpMethod(request.Method), request.Url);

        if (request.Body is { Length: > 0 } body)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        foreach ((string name, string value) in request.Headers ?? new Dictionary<string, string>())
        {
            if (Dropped.Contains(name))
            {
                continue;
            }

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Content is not null)
                {
                    message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                }

                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // A caller's Authorization header survives only when the identity is anonymous: that is how a pasted token is sent.
        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        message.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        return message;
    }

    private async Task<ProxyResult> SendAsync(HttpRequestMessage message, long started, string correlationId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(SendTimeout, time);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        HttpResponseMessage response;

        // Until the headers arrive the upstream has not answered: the proxy's own shape (spec §9).
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (HttpRequestException e)
        {
            return new ProxyUnreached(Describe(e), Elapsed(started), correlationId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyUnreached($"No answer within {SendTimeout.TotalSeconds:0} s.", Elapsed(started), correlationId);
        }

        using (response)
        {
            // From here the upstream has answered; a body that breaks off is still its answer.
            (string body, bool truncated, string? bodyError) = await ReadBodyAsync(response.Content, timeout.Token, cancellationToken);
            Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
            {
                headers[name] = [.. values];
            }

            return new ProxyResponded((int)response.StatusCode, headers, body, truncated, bodyError, Elapsed(started), correlationId);
        }
    }

    /// <summary>The body up to <see cref="MaxBodyBytes"/>, whether it was longer, and why it stopped early if it did.</summary>
    private static async Task<(string Body, bool Truncated, string? Error)> ReadBodyAsync(HttpContent content, CancellationToken timeout, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        string? error = null;

        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(timeout);
            int count;

            while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), timeout)) > 0)
            {
                read += count;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            error = Describe(e);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"The body did not finish within {SendTimeout.TotalSeconds:0} s.";
        }

        bool truncated = read > MaxBodyBytes;
        int length = Math.Min(read, MaxBodyBytes);

        // Not flushed after a cut: a character whose bytes the cut split is left out rather than shown as U+FFFD.
        Decoder decoder = EncodingOf(content).GetDecoder();
        char[] chars = new char[decoder.GetCharCount(buffer, 0, length, flush: !truncated)];
        decoder.GetChars(buffer, 0, length, chars, 0, flush: !truncated);

        return (new string(chars), truncated, error);
    }

    /// <summary>The charset the response declares, when .NET knows it; UTF-8 otherwise.</summary>
    private static Encoding EncodingOf(HttpContent content)
    {
        try
        {
            return content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>The message and each inner message it does not already say; HttpClient's outer messages are generic.</summary>
    private static string Describe(Exception e)
    {
        StringBuilder text = new(e.Message);

        for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (!text.ToString().Contains(inner.Message, StringComparison.Ordinal))
            {
                text.Append(' ').Append(inner.Message);
            }
        }

        return text.ToString();
    }

    private long Elapsed(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;

    private string[] Surfaces()
    {
        AdminOptions o = options.Value;

        return
        [
            .. new[] { o.GatewayUrl, o.CatalogUrl, o.OrderingUrl, o.BffUrl }
                .Select(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
                .OfType<string>(),
        ];
    }
}
