using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Host.Assistant;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>
/// <c>/api/assistant</c> against the fake provider (AI assistant design §14, "Host"), and the off switch of §9.1 on
/// FakePlatform too.
/// </summary>
public sealed class AssistantEndpointsTests : IClassFixture<AdminHostFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerOptions.Web);

    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public AssistantEndpointsTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Open(params object[] clientTools) => new
    {
        client = new { name = "test", version = "1" },
        locale = "en-US",
        catalogue = new[] { new { id = "logs", label = "Logs", where = "/logs", help = "The platform's log lines." } },
        clientTools,
        hostOptions = new { },
    };

    private static object Tool(string name, bool readOnly = true) => new
    {
        name,
        title = name,
        description = "A test tool.",
        inputSchema = new { type = "object" },
        annotations = new { readOnlyHint = readOnly, destructiveHint = !readOnly, idempotentHint = true, openWorldHint = false },
        side = "client",
    };

    private async Task<string> Opened(params object[] clientTools)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/assistant/conversations", Open(clientTools), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<ConversationOpened>(Web, Ct))!.Id;
    }

    [Fact]
    public async Task Under_FakePlatform_with_no_provider_set_the_status_is_the_fake_provider()
    {
        AssistantStatus status = (await client.GetFromJsonAsync<AssistantStatus>("/api/assistant", Web, Ct))!;

        status.ShouldBe(new AssistantStatus(true, "fake", "scripted", null));
    }

    [Fact]
    public async Task Where_are_the_logs_opens_Logs_through_a_client_tool_and_the_reply_names_it_by_its_label()
    {
        string id = await Opened(Tool("open_screen"));

        using HttpRequestMessage events = new(HttpMethod.Get, $"/api/assistant/conversations/{id}/events");
        using HttpResponseMessage stream = await client.SendAsync(events, HttpCompletionOption.ResponseHeadersRead, Ct);
        stream.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        IAsyncEnumerator<SseItem<string>> reader = SseParser.Create(await stream.Content.ReadAsStreamAsync(Ct)).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);

        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = "where are the logs?" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        SseItem<string> call = await Until(reader, "client_tool_call");
        JsonNode data = JsonNode.Parse(call.Data)!;
        data["name"]!.GetValue<string>().ShouldBe("open_screen");
        data["input"]!["path"]!.GetValue<string>().ShouldBe("logs");

        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/tool-results", new { callId = data["callId"]!.GetValue<string>(), ok = true, content = "opened /logs" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        SseItem<string> ended = await Until(reader, "turn_ended");
        JsonNode.Parse(ended.Data)!["reason"]!.GetValue<string>().ShouldBe("done");

        JsonNode view = JsonNode.Parse(await client.GetStringAsync($"/api/assistant/conversations/{id}", Ct))!;
        view["running"]!.GetValue<bool>().ShouldBeFalse();
        view["lastEventId"]!.GetValue<long>().ShouldBe(long.Parse(ended.EventId!, System.Globalization.CultureInfo.InvariantCulture));
        string[] kinds = [.. view["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>())];
        kinds.ShouldBe(["operator", "reply", "tool", "reply", "end"]);
        view["items"]![3]!["text"]!.GetValue<string>().ShouldBe("Logs is open.");
        view.ToJsonString().ShouldNotContain("opened /logs");
    }

    [Fact]
    public async Task A_second_turn_while_one_runs_is_a_409()
    {
        string id = await Opened(Tool("open_screen"));

        // The fake asks the client to open Logs and waits for the answer, so the turn is still running.
        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = "open the logs" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using HttpResponseMessage second = await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = "and the stack" }, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/assistant/conversations/{id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_stream_resumes_after_Last_Event_ID()
    {
        string id = await Opened();
        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = "hello" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Ended(id);

        using HttpRequestMessage resume = new(HttpMethod.Get, $"/api/assistant/conversations/{id}/events");
        resume.Headers.Add("Last-Event-ID", "1");
        using HttpResponseMessage stream = await client.SendAsync(resume, HttpCompletionOption.ResponseHeadersRead, Ct);
        IAsyncEnumerator<SseItem<string>> reader = SseParser.Create(await stream.Content.ReadAsStreamAsync(Ct)).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);

        (await reader.MoveNextAsync()).ShouldBeTrue();
        reader.Current.EventId.ShouldBe("2");
        reader.Current.EventType.ShouldBe("turn_ended");
    }

    [Fact]
    public async Task An_unknown_conversation_is_a_404_on_every_route()
    {
        const string id = "0123456789abcdef0123456789abcdef";

        (await client.GetAsync($"/api/assistant/conversations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/assistant/conversations/{id}/events", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = "hi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/tool-results", new { callId = "c", ok = true, content = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/assistant/conversations/{id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/assistant/conversations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/confirmations", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_deleted_conversation_is_gone()
    {
        string id = await Opened();

        (await client.DeleteAsync($"/api/assistant/conversations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/assistant/conversations/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_client_tool_that_names_an_action_or_writes_is_refused_when_the_conversation_opens()
    {
        using HttpResponseMessage action = await client.PostAsJsonAsync("/api/assistant/conversations", Open(Tool("stack_up")), Ct);
        using HttpResponseMessage writes = await client.PostAsJsonAsync("/api/assistant/conversations", Open(Tool("note_page", readOnly: false)), Ct);

        action.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await action.Content.ReadAsStringAsync(Ct)).ShouldContain("stack_up");
        writes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_body_missing_its_parts_is_a_400()
    {
        using HttpResponseMessage noLocale = await client.PostAsJsonAsync(
            "/api/assistant/conversations", new { client = new { name = "t", version = "1" }, catalogue = Array.Empty<object>(), clientTools = Array.Empty<object>() }, Ct);
        string id = await Opened();
        using HttpResponseMessage emptyText = await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = " " }, Ct);

        noLocale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        emptyText.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_cross_site_request_is_refused_before_it_opens_anything()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/assistant/conversations") { Content = JsonContent.Create(Open()) };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_callers_words_reach_the_provider_redacted()
    {
        using WebApplicationFactory<Program> planted = factory.WithWebHostBuilder(b => b
            .UseSetting("Admin:SqlPassword", "Sql-Pa55-planted")
            .UseSetting("Admin:Users:0:Username", "operator1")
            .UseSetting("Admin:Users:0:Password", "realm-pa55-planted")
            .UseSetting("Admin:BffBrokerConnection", "amqp://bff-svc:broker-pa55-planted@127.0.0.1:5672"));
        HttpClient plantedClient = planted.CreateClient();
        using HttpResponseMessage opened = await plantedClient.PostAsJsonAsync("/api/assistant/conversations", Open(), Ct);
        string id = (await opened.Content.ReadFromJsonAsync<ConversationOpened>(Web, Ct))!.Id;

        await plantedClient.PostAsJsonAsync(
            $"/api/assistant/conversations/{id}/turns",
            new { text = "Sql-Pa55-planted realm-pa55-planted broker-pa55-planted eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln" },
            Ct);

        AssistantRuntime runtime = planted.Services.GetRequiredService<AssistantRuntime>();
        string sent = runtime.Core!.Find(AssistantRuntime.Operator, id)!.History.OfType<UserMessage>().Single().Content;

        sent.ShouldNotContain("pa55-planted");
        sent.ShouldNotContain("eyJ");
    }

    [Theory]
    [InlineData("Admin:Assistant:ApiKey")]
    [InlineData("Admin:Assistant:Anthropic:Token")]
    [InlineData("Admin:Assistant:Password")]
    public void A_key_under_the_assistant_named_like_a_secret_stops_the_host(string key)
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            factory.WithWebHostBuilder(b => b.UseSetting(key, "sk-not-a-real-key")).CreateClient());

        error.Message.ShouldContain(key);
    }

    [Fact]
    public void A_provider_that_is_not_one_stops_the_host_naming_the_key()
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            factory.WithWebHostBuilder(b => b.UseSetting("Admin:Assistant:Provider", "gpt")).CreateClient());

        error.Message.ShouldContain("Admin:Assistant:Provider");
    }

    [Fact]
    public async Task Off_on_FakePlatform_too_the_status_says_off_every_other_route_is_a_404_and_no_provider_exists()
    {
        using WebApplicationFactory<Program> off = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Assistant:Provider", "off"));
        HttpClient offClient = off.CreateClient();

        (await offClient.GetFromJsonAsync<AssistantStatus>("/api/assistant", Web, Ct)).ShouldBe(AssistantStatus.Off);
        (await offClient.PostAsJsonAsync("/api/assistant/conversations", Open(), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await offClient.GetAsync("/api/assistant/conversations/x", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await offClient.GetAsync("/api/assistant/conversations/x/events", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await offClient.PostAsJsonAsync("/api/assistant/conversations/x/turns", new { text = "hi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await offClient.DeleteAsync("/api/assistant/conversations/x", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        AssistantRuntime runtime = off.Services.GetRequiredService<AssistantRuntime>();
        runtime.ProviderId.ShouldBe("off");
        runtime.Core.ShouldBeNull();
        off.Services.GetService<IAssistantProvider>().ShouldBeNull();
    }

    private async Task Ended(string id)
    {
        for (int i = 0; i < 200; i++)
        {
            JsonNode view = JsonNode.Parse(await client.GetStringAsync($"/api/assistant/conversations/{id}", Ct))!;

            if (!view["running"]!.GetValue<bool>())
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException("The turn did not end.");
    }

    private static async Task<SseItem<string>> Until(IAsyncEnumerator<SseItem<string>> reader, string type)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (await reader.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (reader.Current.EventType == type)
            {
                return reader.Current;
            }
        }

        throw new InvalidOperationException($"The stream ended before a {type} event.");
    }
}
