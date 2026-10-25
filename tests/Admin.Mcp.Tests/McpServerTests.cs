using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;

namespace Admin.Mcp.Tests;

/// <summary>The MCP route's server (AI assistant design §12) against a console that is a scripted HTTP handler.</summary>
public sealed class McpServerTests
{
    private const string ListedTools = """
        [
          {"name":"stack_status","title":"Stack status","description":"The stack.","inputSchema":{"type":"object","properties":{}},
           "annotations":{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":false},"side":"server"}
        ]
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonNode> Ask(Console console, string message)
    {
        McpServer server = new(console.Client());

        return (await server.HandleAsync(message, Ct))!;
    }

    private static string Request(string method, string? parameters = null, int id = 1) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{(parameters is null ? "" : $",\"params\":{parameters}")}}}""";

    [Fact]
    public async Task Initialize_answers_with_the_protocol_the_client_asked_for_and_a_tools_only_server()
    {
        JsonNode reply = await Ask(new Console(), Request("initialize", """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"1"}}"""));

        reply["id"]!.GetValue<int>().ShouldBe(1);
        reply["result"]!["protocolVersion"]!.GetValue<string>().ShouldBe("2024-11-05");
        reply["result"]!["capabilities"]!["tools"].ShouldNotBeNull();
        reply["result"]!["capabilities"]!["resources"].ShouldBeNull();
        reply["result"]!["serverInfo"]!["name"]!.GetValue<string>().ShouldBe("blueprint-admin");
        reply["result"]!["instructions"]!.GetValue<string>().ShouldBe(McpServer.Instructions);
    }

    [Fact]
    public async Task Initialize_offers_its_newest_protocol_to_a_client_that_asks_for_one_it_does_not_know()
    {
        JsonNode reply = await Ask(new Console(), Request("initialize", """{"protocolVersion":"2099-01-01"}"""));

        reply["result"]!["protocolVersion"]!.GetValue<string>().ShouldBe(McpServer.Protocols[0]);
    }

    [Fact]
    public async Task Ping_is_answered_with_an_empty_result()
    {
        JsonNode reply = await Ask(new Console(), Request("ping"));

        reply["result"]!.AsObject().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Tools_are_listed_in_MCPs_shape_without_the_cores_own_side_field()
    {
        Console console = new Console().Json(ListedTools);

        JsonNode reply = await Ask(console, Request("tools/list"));

        JsonObject tool = reply["result"]!["tools"]!.AsArray().Single()!.AsObject();
        tool["name"]!.GetValue<string>().ShouldBe("stack_status");
        tool["inputSchema"]!["type"]!.GetValue<string>().ShouldBe("object");
        tool["annotations"]!["readOnlyHint"]!.GetValue<bool>().ShouldBeTrue();
        tool.ContainsKey("side").ShouldBeFalse();
        console.Requests.Single().Uri.AbsolutePath.ShouldBe("/api/assistant/tools");
    }

    [Fact]
    public async Task A_tool_call_posts_the_arguments_and_returns_the_text_the_host_redacted()
    {
        Console console = new Console().Json("""{"ok":true,"content":"{\"backend\":\"running\"}"}""");

        JsonNode reply = await Ask(console, Request("tools/call", """{"name":"stack_status","arguments":{"service":"ordering"}}"""));

        reply["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        reply["result"]!["content"]!.AsArray().Single()!["type"]!.GetValue<string>().ShouldBe("text");
        reply["result"]!["content"]!.AsArray().Single()!["text"]!.GetValue<string>().ShouldBe("""{"backend":"running"}""");
        console.Requests.Single().Uri.AbsolutePath.ShouldBe("/api/assistant/tools/stack_status");
        console.Requests.Single().Body!["service"]!.GetValue<string>().ShouldBe("ordering");
    }

    [Fact]
    public async Task A_tool_that_could_not_answer_is_a_result_with_isError_and_not_a_protocol_error()
    {
        Console console = new Console().Json("""{"ok":false,"content":"get_stock did not answer within 20 s."}""");

        JsonNode reply = await Ask(console, Request("tools/call", """{"name":"get_stock"}"""));

        reply["error"].ShouldBeNull();
        reply["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
        reply["result"]!["content"]!.AsArray().Single()!["text"]!.GetValue<string>().ShouldContain("did not answer");
        console.Requests.Single().Body!.AsObject().Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_tool_the_host_does_not_have_is_invalid_params()
    {
        Console console = new Console().Status(HttpStatusCode.NotFound, """{"title":"No such tool","status":404,"detail":"nope"}""");

        JsonNode reply = await Ask(console, Request("tools/call", """{"name":"nope"}"""));

        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        reply["error"]!["message"]!.GetValue<string>().ShouldBe("There is no tool named nope.");
    }

    [Fact]
    public async Task A_console_with_the_assistant_off_says_so_instead_of_naming_a_missing_tool()
    {
        Console console = new Console().Status(HttpStatusCode.NotFound, "");

        JsonNode reply = await Ask(console, Request("tools/list"));

        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32603);
        reply["error"]!["message"]!.GetValue<string>().ShouldContain("assistant off");
    }

    [Fact]
    public async Task A_call_with_no_name_is_invalid_params_and_reaches_no_host()
    {
        Console console = new();

        JsonNode reply = await Ask(console, Request("tools/call", """{"arguments":{}}"""));

        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        console.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_console_that_cannot_be_reached_is_an_internal_error_with_what_failed()
    {
        Console console = new Console().Fails();

        JsonNode reply = await Ask(console, Request("tools/list"));

        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32603);
        reply["error"]!["message"]!.GetValue<string>().ShouldContain("did not answer");
    }

    [Fact]
    public async Task A_method_it_does_not_implement_is_method_not_found()
    {
        foreach (string method in new[] { "resources/list", "prompts/list", "tools/subscribe" })
        {
            JsonNode reply = await Ask(new Console(), Request(method));

            reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
        }
    }

    [Fact]
    public async Task A_notification_and_a_response_the_client_sent_are_not_answered()
    {
        McpServer server = new(new Console().Client());

        (await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Ct)).ShouldBeNull();
        (await server.HandleAsync("""{"jsonrpc":"2.0","id":9,"result":{}}""", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Text_that_is_not_a_request_is_a_parse_or_request_error_with_no_id()
    {
        McpServer server = new(new Console().Client());

        (await server.HandleAsync("{not json", Ct))!["error"]!["code"]!.GetValue<int>().ShouldBe(-32700);
        (await server.HandleAsync("[1,2]", Ct))!["error"]!["code"]!.GetValue<int>().ShouldBe(-32600);
        (await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":7}""", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_loop_reads_a_line_at_a_time_answers_each_request_and_ends_with_the_input()
    {
        StringReader input = new(string.Join('\n', Request("ping", id: 1), "", """{"jsonrpc":"2.0","method":"notifications/initialized"}""", Request("ping", id: 2)) + "\n");
        StringWriter output = new();

        await new McpServer(new Console().Client()).RunAsync(input, output, Ct);

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(2);
        JsonNode.Parse(lines[0])!["id"]!.GetValue<int>().ShouldBe(1);
        JsonNode.Parse(lines[1])!["id"]!.GetValue<int>().ShouldBe(2);
    }
}

/// <summary>A console stand-in that answers each request with the next queued response and keeps what it was sent.</summary>
internal sealed class Console : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<(HttpMethod Method, Uri Uri, JsonNode? Body)> Requests { get; } = [];

    public HttpClient Client() => new(this) { BaseAddress = new Uri("http://localhost:5100/") };

    public Console Json(string body) => Status(HttpStatusCode.OK, body);

    public Console Status(HttpStatusCode status, string body)
    {
        responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        return this;
    }

    public Console Fails()
    {
        responses.Enqueue(() => throw new HttpRequestException("connection refused"));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body)));

        return responses.Dequeue()();
    }
}
