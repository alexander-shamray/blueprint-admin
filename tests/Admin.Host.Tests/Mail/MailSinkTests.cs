using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Mail;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Mail;

public sealed class MailSinkTests : IClassFixture<AdminHostFactory>
{
    /// <summary>The order the recorded messages were all sent for, which is the correlation id each one carries.</summary>
    private const string RecordedOrder = "01a13f79-2194-77a5-bbad-eaf2d69eacd6";

    private const string Delivered = "4214Ji17w29YtX8IUEwuq5";

    private readonly HttpClient client;

    public MailSinkTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task The_list_is_the_recorded_sink_newest_first_each_message_joined_to_its_correlation_id()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/mail/messages", TestContext.Current.CancellationToken);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        view.GetProperty("total").GetInt32().ShouldBe(4);
        view.GetProperty("sinkUrl").GetString().ShouldBe(new AdminOptions().MailSinkUrl);

        JsonElement[] messages = [.. view.GetProperty("messages").EnumerateArray()];
        messages.Length.ShouldBe(4);
        messages[0].GetProperty("id").GetString().ShouldBe(Delivered);
        messages.Select(m => m.GetProperty("created").GetDateTimeOffset()).ShouldBeInOrder(SortDirection.Descending);
        messages.ShouldAllBe(m => m.GetProperty("correlationId").GetString() == RecordedOrder);
        messages[0].GetProperty("to")[0].GetProperty("address").GetString().ShouldBe("demo@example.invalid");
    }

    [Fact]
    public async Task An_opened_message_carries_its_body_and_its_correlation_id()
    {
        JsonElement read = await client.GetFromJsonAsync<JsonElement>($"/api/mail/messages/{Delivered}", TestContext.Current.CancellationToken);

        read.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement message = read.GetProperty("message");
        message.GetProperty("text").GetString().ShouldNotBeNull().ShouldContain($"Order: {RecordedOrder}");
        message.GetProperty("html").GetString().ShouldNotBeNull().ShouldBeEmpty("the backend's mail is plain text");
        message.GetProperty("correlationId").GetString().ShouldBe(RecordedOrder);
    }

    [Fact]
    public async Task A_message_the_sink_does_not_hold_is_its_404_and_no_message()
    {
        JsonElement read = await client.GetFromJsonAsync<JsonElement>("/api/mail/messages/NotARecordedId", TestContext.Current.CancellationToken);

        read.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        read.GetProperty("error").GetString().ShouldBe("The mail sink answered 404 for message NotARecordedId.");
        read.GetProperty("message").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("..%2Fchaos")]
    [InlineData("abc%3Flimit%3D1")]
    [InlineData("abc-def")]
    public async Task An_id_that_could_reach_another_path_is_refused_before_the_sink_is_asked(string id)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/mail/messages/{id}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_request_is_a_get_on_the_sink_origin_to_one_of_the_three_reads()
    {
        ScriptedHandler sink = new(Answer);
        MailSinkClient mail = Client(sink);

        await mail.ListAsync(TestContext.Current.CancellationToken);
        await mail.ReadAsync("abc123", TestContext.Current.CancellationToken);

        sink.Requests.ShouldAllBe(r => r.Request.Method == HttpMethod.Get && r.Body == null);
        sink.Requests.Select(r => r.Request.RequestUri!.PathAndQuery).ShouldBe(
            ["/api/v1/messages?limit=50", "/api/v1/message/abc123/headers", "/api/v1/message/abc123", "/api/v1/message/abc123/headers"],
            ignoreOrder: true);
        sink.Requests.ShouldAllBe(r => r.Request.RequestUri!.GetLeftPart(UriPartial.Authority) == "http://localhost:8025");
    }

    [Fact]
    public async Task A_sink_that_does_not_answer_is_a_state_with_the_reason()
    {
        MailSinkClient mail = Client(new ScriptedHandler(_ => throw new HttpRequestException("connection refused")));

        MailListView list = await mail.ListAsync(TestContext.Current.CancellationToken);
        MailMessageRead read = await mail.ReadAsync("abc123", TestContext.Current.CancellationToken);

        list.Reachable.ShouldBeFalse();
        list.Error.ShouldBe("connection refused");
        list.Messages.ShouldBeEmpty();
        read.Reachable.ShouldBeFalse();
        read.Message.ShouldBeNull();
    }

    [Fact]
    public async Task A_correlation_header_the_backend_would_not_adopt_is_no_link()
    {
        MailSinkClient mail = Client(new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/headers", StringComparison.Ordinal)
                ? Json("""{"X-Correlation-Id":["not adoptable; has spaces"]}""")
                : Answer(request)));

        MailListView list = await mail.ListAsync(TestContext.Current.CancellationToken);

        list.Messages.Single().CorrelationId.ShouldBeNull();
    }

    [Fact]
    public async Task A_message_whose_headers_cannot_be_read_is_still_listed()
    {
        MailSinkClient mail = Client(new ScriptedHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/headers", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Answer(request)));

        MailListView list = await mail.ListAsync(TestContext.Current.CancellationToken);

        list.Reachable.ShouldBeTrue();
        list.Messages.Single().Id.ShouldBe("abc123");
        list.Messages.Single().CorrelationId.ShouldBeNull();
    }

    [Theory]
    [InlineData("/api/v1/message/abc", "abc", null)]
    [InlineData("/api/v1/message/abc/headers", null, "abc")]
    [InlineData("/api/v1/message/abc/raw", null, null)]
    [InlineData("/api/v1/messages", null, null)]
    public void The_fake_and_the_recorder_key_a_message_by_the_same_id(string path, string? message, string? headers)
    {
        FakeMailSink.MessageIdOf(path).ShouldBe(message);
        FakeMailSink.HeadersIdOf(path).ShouldBe(headers);
    }

    [Fact]
    public void The_fake_refuses_anything_but_a_read()
    {
        using HttpRequestMessage clear = new(HttpMethod.Delete, "http://localhost:8025/api/v1/messages");

        FakeMailSink.Send(clear, new FakeOrders(TimeProvider.System)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    private static MailSinkClient Client(ScriptedHandler handler) => new(new HttpClient(handler), Options.Create(new AdminOptions()));

    /// <summary>A sink holding one message, abc123, whose correlation id is <see cref="RecordedOrder"/>.</summary>
    private static HttpResponseMessage Answer(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/messages" => Json("""{"total":1,"messages":[{"ID":"abc123","From":{"Name":"Commerce","Address":"no-reply@commerce.test"},"To":[{"Name":"","Address":"demo@example.invalid"}],"Subject":"Hello","Created":"2026-10-15T12:11:07.205Z","Snippet":"Hello","Read":false}]}"""),
            "/api/v1/message/abc123/headers" => Json($$"""{"X-Correlation-Id":["{{RecordedOrder}}"]}"""),
            "/api/v1/message/abc123" => Json("""{"ID":"abc123","From":{"Name":"Commerce","Address":"no-reply@commerce.test"},"To":[],"Cc":null,"Subject":"Hello","Date":"2026-10-15T12:11:07Z","Text":"Hello","HTML":""}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
