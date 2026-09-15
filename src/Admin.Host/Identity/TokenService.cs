using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Identity;

/// <summary>Who a request is sent as: no username is anonymous; a username alone is looked up in <c>Admin:Users</c>.</summary>
public sealed record IdentityRequest(string? Username, string? Password);

public abstract record TokenOutcome;

public sealed record TokenIssued(string Username, string AccessToken, DateTimeOffset ExpiresAt, JsonElement Claims) : TokenOutcome;

/// <summary>Keycloak answered with a non-success status; its body is passed through untouched (spec §9).</summary>
public sealed record TokenRejected(int Status, string Body, string? ContentType) : TokenOutcome;

public sealed record KeycloakUnreachable(string Error) : TokenOutcome;

public sealed record UnknownUser(string Username) : TokenOutcome;

/// <summary>
/// The password grant against realm <c>Admin:Realm</c> for client <c>Admin:ClientId</c>, as
/// run-locally.md's "Mint a token" does. Tokens are reused until 30 seconds before expiry and then
/// re-minted, never refreshed (spec §2.4, §5.6).
/// </summary>
public sealed class TokenService(HttpClient http, IOptions<AdminOptions> options, TimeProvider time)
{
    internal static readonly TimeSpan ReuseMargin = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan GrantTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Keys the cache by password without keeping one: the host is no credential store, so a custom password must not outlive its request.</summary>
    private readonly byte[] passwordKey = RandomNumberGenerator.GetBytes(32);

    private readonly ConcurrentDictionary<(string Username, string PasswordDigest), TokenIssued> cache = new();

    public async Task<TokenOutcome?> ForAsync(IdentityRequest? identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identity?.Username))
        {
            return null;
        }

        string? password = identity.Password
            ?? RealmUsers.Of(options.Value).FirstOrDefault(u => u.Username == identity.Username)?.Password;

        return password is null
            ? new UnknownUser(identity.Username)
            : await GetAsync(identity.Username, password, cancellationToken);
    }

    public async Task<TokenOutcome> GetAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((username, Digest(password)), out TokenIssued? cached) && time.GetUtcNow() < cached.ExpiresAt - ReuseMargin)
        {
            return cached;
        }

        AdminOptions o = options.Value;
        string url = $"{o.KeycloakUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(o.Realm)}/protocol/openid-connect/token";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new KeycloakUnreachable($"Admin:KeycloakUrl '{o.KeycloakUrl}' is not an absolute http(s) URL.");
        }

        using FormUrlEncodedContent form = new(
        [
            new("grant_type", "password"),
            new("client_id", o.ClientId),
            new("username", username),
            new("password", password),
        ]);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GrantTimeout);
        DateTimeOffset requestedAt = time.GetUtcNow();

        try
        {
            using HttpResponseMessage response = await http.PostAsync(uri, form, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new TokenRejected((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
            }

            using JsonDocument document = JsonDocument.Parse(body);
            string accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new FormatException("access_token is null.");
            int expiresIn = document.RootElement.GetProperty("expires_in").GetInt32();
            TokenIssued issued = new(username, accessToken, requestedAt.AddSeconds(expiresIn), JwtPayload.Decode(accessToken));
            cache[(username, Digest(password))] = issued;

            return issued;
        }
        catch (HttpRequestException e)
        {
            return new KeycloakUnreachable(e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KeycloakUnreachable($"No answer within {GrantTimeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new KeycloakUnreachable($"Keycloak answered with a body that is not a token response: {e.Message}");
        }
    }

    private string Digest(string password) => Convert.ToHexString(HMACSHA256.HashData(passwordKey, Encoding.UTF8.GetBytes(password)));
}
