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

        if (headers.Keys.Any(k => k.Equals(CorrelationHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Send the correlation id in correlationId, not as an {CorrelationHeader} header.";
        }

        if (headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { Key: not null } contentType
            && !MediaTypeHeaderValue.TryParse(contentType.Value, out _))
        {
            return $"Content-Type '{contentType.Value}' is not a media type.";
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
        long started = time.GetTimestamp();

        switch (await tokens.ForAsync(request.Identity, cancellationToken))
        {
            case TokenRejected rejected:
                return new ProxyTokenRejected(rejected.Status, rejected.Body, correlationId);
            case UnknownUser unknown:
                return new ProxyTokenRejected(400, $"'{unknown.Username}' is not a configured realm user.", correlationId);
            case KeycloakUnreachable unreachable:
                return new ProxyUnreached($"Keycloak did not answer: {unreachable.Error}", Elapsed(started), correlationId);
            case var outcome:
                using (HttpRequestMessage message = Build(request, outcome as TokenIssued, correlationId))
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

        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        message.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        return message;
    }

    private async Task<ProxyResult> SendAsync(HttpRequestMessage message, long started, string correlationId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            (string body, bool truncated) = await ReadBodyAsync(response.Content, timeout.Token);
            Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
            {
                headers[name] = [.. values];
            }

            return new ProxyResponded((int)response.StatusCode, headers, body, truncated, Elapsed(started), correlationId);
        }
        catch (HttpRequestException e)
        {
            return new ProxyUnreached(e.Message, Elapsed(started), correlationId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyUnreached($"No answer within {SendTimeout.TotalSeconds:0} s.", Elapsed(started), correlationId);
        }
    }

    private static async Task<(string Body, bool Truncated)> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        int count;

        while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += count;
        }

        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxBodyBytes)), read > MaxBodyBytes);
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
