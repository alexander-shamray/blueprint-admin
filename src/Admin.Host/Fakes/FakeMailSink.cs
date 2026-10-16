using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Admin.Host.Fakes;

/// <summary>
/// The mail sink in FakePlatform mode, answering the three reads <c>Mail/MailSinkClient.cs</c> makes from fixtures
/// recorded off a live sink (<see cref="FixtureRecordings"/>): the list, and each listed message and its headers keyed
/// by the message's id. An id the recording does not hold answers 404 in the sink's own words, as the real one does.
/// </summary>
internal static class FakeMailSink
{
    private const string Messages = "/api/v1/messages";

    private const string Message = "/api/v1/message/";

    private const string Headers = "/headers";

    public static HttpResponseMessage Send(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;

        return request.Method != HttpMethod.Get ? Text(HttpStatusCode.MethodNotAllowed, "405 method not allowed")
            : path == Messages ? FakeHttp.Json(HttpStatusCode.OK, Fixture("mailpit-messages.json"))
            : HeadersIdOf(path) is { } headers ? Keyed("mailpit-headers.json", headers)
            : MessageIdOf(path) is { } message ? Keyed("mailpit-message.json", message)
            : Text(HttpStatusCode.NotFound, "404 page not found");
    }

    /// <summary>The id in <c>/api/v1/message/{id}</c>, or null for any other path. The recorder keys the message fixture by it.</summary>
    public static string? MessageIdOf(string path) =>
        path.StartsWith(Message, StringComparison.Ordinal) && path[Message.Length..] is { Length: > 0 } id && !id.Contains('/', StringComparison.Ordinal)
            ? id
            : null;

    /// <summary>The id in <c>/api/v1/message/{id}/headers</c>, or null for any other path. The recorder keys the headers fixture by it.</summary>
    public static string? HeadersIdOf(string path) =>
        path.EndsWith(Headers, StringComparison.Ordinal) ? MessageIdOf(path[..^Headers.Length]) : null;

    private static HttpResponseMessage Keyed(string fixture, string id)
    {
        using JsonDocument recording = JsonDocument.Parse(Fixture(fixture));

        return recording.RootElement.TryGetProperty(id, out JsonElement answer)
            ? FakeHttp.Json(HttpStatusCode.OK, answer.GetRawText())
            : Text(HttpStatusCode.NotFound, "404 page not found");
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body + "\n", Encoding.UTF8, "text/plain") };

    private static string Fixture(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}
