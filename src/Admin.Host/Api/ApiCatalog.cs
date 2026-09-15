using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Identity;
using Microsoft.Extensions.Options;

namespace Admin.Host.Api;

/// <summary>
/// The API screen's operation tree (spec §5.7): Catalog's and Ordering's OpenAPI documents, which
/// need a token (the services' fallback authorization policy), rebased onto the gateway, then the
/// curated operations. Cached until reloaded; a service that stops answering keeps its last
/// operations, marked unavailable, rather than vanishing from the tree.
/// </summary>
public sealed class ApiCatalog(HttpClient http, TokenService tokens, IOptions<AdminOptions> options) : IDisposable
{
    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<ApiOperation>> lastGood = [];
    private ApiCatalogView? current;

    public void Dispose() => gate.Dispose();

    public async Task<ApiCatalogView> GetAsync(CancellationToken cancellationToken) =>
        current ?? await ReloadAsync(cancellationToken);

    public async Task<ApiCatalogView> ReloadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            AdminOptions o = options.Value;
            (string Name, string BaseUrl)[] services = [("catalog", o.CatalogUrl), ("ordering", o.OrderingUrl)];

            // One grant for both documents: two concurrent loads would each miss the empty cache and mint twice.
            (string? bearer, string? tokenError) = await BearerAsync(o, cancellationToken);
            (ApiSource Source, IReadOnlyList<ApiOperation>? Operations)[] loads =
                await Task.WhenAll(services.Select(s => LoadAsync(s.Name, s.BaseUrl, o, bearer, tokenError, cancellationToken)));
            List<ApiOperation> operations = [];

            foreach ((ApiSource source, IReadOnlyList<ApiOperation>? loaded) in loads)
            {
                if (loaded is not null)
                {
                    lastGood[source.Name] = loaded;
                }

                if (lastGood.TryGetValue(source.Name, out IReadOnlyList<ApiOperation>? known))
                {
                    operations.AddRange(source.Available ? known : known.Select(op => op with { Available = false }));
                }
            }

            operations.AddRange(CuratedOperations.All(o));
            current = new ApiCatalogView([.. loads.Select(l => l.Source)], operations);

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A token for the realm user named demo, else the first configured user; or why there is none.</summary>
    private async Task<(string? Bearer, string? Error)> BearerAsync(AdminOptions o, CancellationToken cancellationToken)
    {
        IReadOnlyList<RealmUser> users = RealmUsers.Of(o);
        RealmUser? user = users.FirstOrDefault(u => u.Username == "demo") ?? (users.Count > 0 ? users[0] : null);

        if (user is null)
        {
            return (null, "No realm user is configured to fetch the document with.");
        }

        return await tokens.GetAsync(user.Username, user.Password, cancellationToken) switch
        {
            TokenIssued issued => (issued.AccessToken, null),
            TokenRejected rejected => (null, $"The token request for {user.Username} answered {rejected.Status}."),
            KeycloakUnreachable unreachable => (null, $"Keycloak did not answer: {unreachable.Error}"),
            _ => (null, $"No token for {user.Username}."),
        };
    }

    private async Task<(ApiSource, IReadOnlyList<ApiOperation>?)> LoadAsync(
        string name, string baseUrl, AdminOptions o, string? bearer, string? tokenError, CancellationToken cancellationToken)
    {
        string documentUrl = baseUrl.TrimEnd('/') + "/openapi/v1.json";
        (ApiSource, IReadOnlyList<ApiOperation>?) Unavailable(string error) => (new ApiSource(name, documentUrl, false, error), null);

        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Unavailable($"{documentUrl} is not an absolute http(s) URL.");
        }

        if (bearer is null)
        {
            return Unavailable(tokenError ?? "No token.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DocumentTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Authorization = new("Bearer", bearer);
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"{documentUrl} answered {(int)response.StatusCode}.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            return (new ApiSource(name, documentUrl, true, null), OpenApiReader.Read(name, document.RootElement, o.GatewayUrl));
        }
        catch (HttpRequestException e)
        {
            return Unavailable(e.Message);
        }
        catch (JsonException e)
        {
            return Unavailable($"{documentUrl} is not JSON: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            // JSON of the wrong shape (a path item that is a string, parameters that are not an array).
            return Unavailable($"{documentUrl} is not a readable OpenAPI document: {e.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable($"{documentUrl} did not answer within {DocumentTimeout.TotalSeconds:0} s.");
        }
    }
}
