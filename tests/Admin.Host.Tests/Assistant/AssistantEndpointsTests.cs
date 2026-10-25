using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Assistant;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Host.Assistant;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("how many are in stock for product 0199a1b2-0000-7000-8000-00000000000a?", "10 in stock, 0 reserved.")]
    [InlineData("what is the payment status of order 0199a1b2-0000-7000-8000-000000000002?", "The payment is Authorised.")]
    [InlineData("сколько на складе товара 0199a1b2-0000-7000-8000-00000000000a?", "В наличии: 10, в резерве: 0.")]
    public async Task A_question_about_stock_or_a_payment_is_answered_from_a_platform_read_the_conversation_opened_with(string question, string answer)
    {
        string id = await Opened();

        (await client.PostAsJsonAsync($"/api/assistant/conversations/{id}/turns", new { text = question }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Ended(id);

        // The view's JSON writes Cyrillic as \uXXXX escapes.
        string view = System.Text.RegularExpressions.Regex.Unescape(await client.GetStringAsync($"/api/assistant/conversations/{id}", Ct));
        view.ShouldContain(answer);
        // The read is in the operator's transcript, marked as the assistant's.
        JsonNode transcript = JsonNode.Parse(await client.GetStringAsync("/api/transcript", Ct))!;
        transcript["entries"]!.AsArray().ShouldContain(e => e!["assistant"]!.GetValue<bool>() && e["command"]!.GetValue<string>().Contains("0199a1b2", StringComparison.Ordinal));
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
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData("", true)]
    public void Off_never_builds_a_provider_a_redactor_or_the_tools_and_on_builds_each_once(string provider, bool on)
    {
        int providers = 0;
        int anthropics = 0;
        int redactors = 0;
        int tools = 0;
        AdminOptions options = new() { FakePlatform = true, Assistant = { Provider = provider } };

        AssistantRuntime runtime = new(
            options,
            new ConfigurationBuilder().Build(),
            () =>
            {
                providers++;
                return new FakeAssistantProvider();
            },
            () =>
            {
                anthropics++;
                return new FakeAssistantProvider();
            },
            () =>
            {
                redactors++;
                return new Unredacted();
            },
            () =>
            {
                tools++;
                return [];
            },
            TimeProvider.System,
            NullLogger<AssistantRuntime>.Instance);

        (runtime.Core is not null).ShouldBe(on);
        providers.ShouldBe(on ? 1 : 0);
        redactors.ShouldBe(on ? 1 : 0);
        tools.ShouldBe(on ? 1 : 0);

        // Whatever the provider is, the Anthropic one is not built unless it is the one named.
        anthropics.ShouldBe(0);
    }

    [Fact]
    public void The_anthropic_provider_is_built_once_when_it_is_named_and_the_fake_is_not_built_beside_it()
    {
        int fakes = 0;
        int anthropics = 0;
        AdminOptions options = new() { Assistant = { Provider = "Anthropic", Model = "claude-haiku-5-5" } };

        AssistantRuntime runtime = new(
            options,
            new ConfigurationBuilder().Build(),
            () =>
            {
                fakes++;
                return new FakeAssistantProvider();
            },
            () =>
            {
                anthropics++;
                return new FakeAssistantProvider();
            },
            () => new Unredacted(),
            () => [],
            TimeProvider.System,
            NullLogger<AssistantRuntime>.Instance);

        runtime.ProviderId.ShouldBe("anthropic");
        runtime.Core.ShouldNotBeNull();
        anthropics.ShouldBe(1);
        fakes.ShouldBe(0);
    }

    [Fact]
    public void A_provider_that_is_none_of_the_three_is_refused_naming_the_key()
    {
        AdminOptions options = new() { Assistant = { Provider = "openai" } };

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(() => AssistantOptions.ProviderOf(options));

        refused.Message.ShouldContain("Admin:Assistant:Provider");
        refused.Message.ShouldContain("'anthropic'");
    }

    [Theory]
    [InlineData("", "low", "Admin:Assistant:Model")]
    [InlineData("claude-opus-5-5", "extreme", "Admin:Assistant:Effort")]
    public void The_anthropic_provider_refuses_a_model_or_an_effort_it_cannot_use_at_startup_naming_the_key(string model, string effort, string key)
    {
        AssistantOptions options = new() { Model = model, Effort = effort };

        Should.Throw<InvalidOperationException>(() => AnthropicProviderFactory.Create(options, TimeProvider.System, NullLogger.Instance)).Message.ShouldContain(key);
    }

    [Fact]
    public void The_anthropic_defaults_are_the_ones_the_design_names()
    {
        AssistantOptions options = new();

        options.Model.ShouldBe("claude-opus-5-5");
        options.Effort.ShouldBe("low");
    }

    [Fact]
    public void An_option_named_for_a_credential_is_still_refused_with_the_anthropic_provider_on()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Assistant:ApiKey"] = "sk-ant-nope" }).Build();
        AdminOptions options = new() { Assistant = { Provider = "anthropic" } };

        Should.Throw<InvalidOperationException>(() =>
            new AssistantRuntime(options, configuration, () => new FakeAssistantProvider(), () => new FakeAssistantProvider(), () => new Unredacted(), () => [], TimeProvider.System, NullLogger<AssistantRuntime>.Instance))
            .Message.ShouldContain("Admin:Assistant:ApiKey");
    }

    private sealed class Unredacted : IRedactor
    {
        public string Redact(string text, Caller caller) => text;
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
