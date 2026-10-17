using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Api;

namespace Admin.Host.Fakes;

/// <summary>
/// The mail sink in FakePlatform mode, answering the three reads <c>Mail/MailSinkClient.cs</c> makes from fixtures
/// recorded off a live sink (<see cref="FixtureRecordings"/>): the list, and each listed message and its headers keyed
/// by the message's id. An id the recording does not hold answers 404 in the sink's own words, as the real one does.
/// <para>
/// Beside the recording sit the messages <see cref="FakeOrders.Mail"/> holds for the Scenario's orders, so a run's mail can be
/// counted; they are listed with the recorded ones and answer the same three reads.
/// </para>
/// </summary>
internal static class FakeMailSink
{
    private const string Messages = "/api/v1/messages";

    private const string Message = "/api/v1/message/";

    private const string Headers = "/headers";

    public static HttpResponseMessage Send(HttpRequestMessage request, FakeOrders orders)
    {
        string path = request.RequestUri!.AbsolutePath;

        return request.Method != HttpMethod.Get ? Text(HttpStatusCode.MethodNotAllowed, "405 method not allowed")
            : path == Messages ? FakeHttp.Json(HttpStatusCode.OK, List(orders.Mail()))
            : HeadersIdOf(path) is { } headers ? Keyed("mailpit-headers.json", headers, orders, HeadersOf)
            : MessageIdOf(path) is { } message ? Keyed("mailpit-message.json", message, orders, MessageOf)
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

    private static HttpResponseMessage Keyed(string fixture, string id, FakeOrders orders, Func<FakeMail, string> sent)
    {
        using JsonDocument recording = JsonDocument.Parse(Fixture(fixture));

        return recording.RootElement.TryGetProperty(id, out JsonElement answer) ? FakeHttp.Json(HttpStatusCode.OK, answer.GetRawText())
            : orders.Mail().FirstOrDefault(m => m.Id == id) is { } mail ? FakeHttp.Json(HttpStatusCode.OK, sent(mail))
            : Text(HttpStatusCode.NotFound, "404 page not found");
    }

    /// <summary>The recorded list with the Scenario's messages added, in the sink's shape; the client sorts them by <c>Created</c>.</summary>
    private static string List(IReadOnlyList<FakeMail> mail)
    {
        JsonObject list = JsonNode.Parse(Fixture("mailpit-messages.json"))!.AsObject();
        JsonArray messages = list["messages"]!.AsArray();

        foreach (FakeMail m in mail)
        {
            messages.Add(new JsonObject
            {
                ["ID"] = m.Id,
                ["MessageID"] = m.MessageId,
                ["Read"] = false,
                ["From"] = Sender(),
                ["To"] = new JsonArray(Recipient()),
                ["Subject"] = m.Subject,
                ["Created"] = m.Created,
                ["Snippet"] = m.Subject,
            });
        }

        list["total"] = (int)list["total"]! + mail.Count;

        return list.ToJsonString();
    }

    private static string HeadersOf(FakeMail m) =>
        new JsonObject
        {
            ["Message-Id"] = new JsonArray($"<{m.MessageId}>"),
            [CorrelationId.Header] = new JsonArray(m.CorrelationId),
        }.ToJsonString();

    private static string MessageOf(FakeMail m) =>
        new JsonObject
        {
            ["ID"] = m.Id,
            ["MessageID"] = m.MessageId,
            ["From"] = Sender(),
            ["To"] = new JsonArray(Recipient()),
            ["Cc"] = new JsonArray(),
            ["Subject"] = m.Subject,
            ["Date"] = m.Created,
            ["Text"] = m.Subject,
            ["HTML"] = "",
        }.ToJsonString();

    private static JsonObject Sender() => new() { ["Name"] = "Commerce", ["Address"] = "no-reply@commerce.test" };

    private static JsonObject Recipient() => new() { ["Name"] = "", ["Address"] = "demo@example.invalid" };

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body + "\n", Encoding.UTF8, "text/plain") };

    private static string Fixture(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}
