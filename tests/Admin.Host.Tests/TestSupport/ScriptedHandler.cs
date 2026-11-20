namespace Admin.Host.Tests.TestSupport;

/// <summary>An HttpMessageHandler that answers from a script and records what it was sent, bodies included.</summary>
public sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly List<(HttpRequestMessage Request, string? Body)> requests = [];

    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>
    /// A snapshot of what was sent, in arrival order. Recorded under a lock, since the services under test fan out
    /// through one client (ApiCatalog's Task.WhenAll, MailSinkClient's Parallel.ForEachAsync).
    /// </summary>
    public IReadOnlyList<(HttpRequestMessage Request, string? Body)> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (gate)
        {
            requests.Add((request, body));
        }

        HttpResponseMessage response = await respond(request, cancellationToken);
        response.RequestMessage ??= request;

        return response;
    }
}
