namespace Admin.Host.Tests.TestSupport;

/// <summary>An HttpMessageHandler that answers from a script and records what it was sent, bodies included.</summary>
public sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        HttpResponseMessage response = await respond(request, cancellationToken);
        response.RequestMessage ??= request;

        return response;
    }
}
