using System.Text.Json.Nodes;
using Admin.Mcp;
using Shouldly;

namespace Admin.Host.Tests.Mcp;

/// <summary>The MCP server (AI assistant design §12) against a scripted host: what it answers, and what it never answers.</summary>
public sealed class McpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Tool(string name) => new()
    {
        ["name"] = name,
        ["title"] = name,
        ["description"] = "A test tool.",
        ["inputSchema"] = new JsonObject { ["type"] = "object" },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
        ["side"] = "server",
    };

    private static async Task<JsonNode?> Ask(ScriptedHost host, string request) => await new McpServer(host).HandleAsync(request, Ct);

    [Fact]
    public async Task Initialize_answers_with_the_clients_protocol_when_it_is_known_and_the_latest_when_not()
    {
        JsonNode? known = await Ask(new ScriptedHost(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        JsonNode? unknown = await Ask(new ScriptedHost(), """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        known!["result"]!["protocolVersion"]!.GetValue<string>().ShouldBe("2024-11-05");
        unknown!["result"]!["protocolVersion"]!.GetValue<string>().ShouldBe(McpServer.LatestProtocol);
        known["result"]!["capabilities"]!["tools"].ShouldNotBeNull();
        known["id"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_notification_is_never_answered()
    {
        (await Ask(new ScriptedHost(), """{"jsonrpc":"2.0","method":"notifications/initialized"}""")).ShouldBeNull();
        (await Ask(new ScriptedHost(), """{"jsonrpc":"2.0","method":"nothing/at/all"}""")).ShouldBeNull();
    }

    [Fact]
    public async Task Tools_list_passes_the_hosts_tools_in_MCPs_shape_without_the_cores_side_field()
    {
        ScriptedHost host = new() { Tools = [Tool("stack_status"), Tool("get_stock")] };

        JsonNode? reply = await Ask(host, """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""");

        JsonArray tools = reply!["result"]!["tools"]!.AsArray();
        tools.Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["stack_status", "get_stock"]);
        tools.ShouldAllBe(t => t!.AsObject().ContainsKey("inputSchema") && t.AsObject().ContainsKey("annotations") && !t.AsObject().ContainsKey("side"));
        reply["id"]!.GetValue<string>().ShouldBe("a");
    }

    [Fact]
    public async Task Tools_call_returns_the_result_as_text_and_marks_a_failed_tool_as_an_error()
    {
        ScriptedHost host = new() { Result = new ToolResult(true, "all up") };

        JsonNode? ok = await Ask(host, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"stack_status","arguments":{"x":1}}}""");
        host.Result = new ToolResult(false, "no such product");
        JsonNode? failed = await Ask(host, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_stock"}}""");

        ok!["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("all up");
        ok["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        host.Calls[0].Arguments["x"]!.GetValue<int>().ShouldBe(1);
        failed!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
        host.Calls[1].Arguments.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Tools_call_of_an_unknown_name_or_with_bad_params_is_a_protocol_error()
    {
        ScriptedHost host = new() { Result = null };

        JsonNode? unknown = await Ask(host, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"open_screen"}}""");
        JsonNode? noName = await Ask(host, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}""");
        JsonNode? badArguments = await Ask(host, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"a","arguments":[1]}}""");

        unknown!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        noName!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
        badArguments!["error"]!["code"]!.GetValue<int>().ShouldBe(-32602);
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_is_an_error_result_for_a_call_and_a_protocol_error_for_a_list()
    {
        ScriptedHost host = new() { Unavailable = "start the host" };

        JsonNode? call = await Ask(host, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"stack_status"}}""");
        JsonNode? list = await Ask(host, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        call!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
        call["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("start the host");
        list!["error"]!["message"]!.GetValue<string>().ShouldBe("start the host");
    }

    [Fact]
    public async Task A_host_that_throws_anything_ends_that_call_and_never_the_server()
    {
        ScriptedHost host = new() { Throws = new InvalidOperationException("boom") };

        JsonNode? call = await Ask(host, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"stack_status"}}""");
        JsonNode? list = await Ask(host, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        call!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
        call["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldContain("boom");
        list!["error"]!["code"]!.GetValue<int>().ShouldBe(-32603);
    }

    [Fact]
    public async Task The_catalogue_is_the_one_resource()
    {
        ScriptedHost host = new() { Catalogue = """[{"id":"stack"}]""" };

        JsonNode? list = await Ask(host, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");
        JsonNode? read = await Ask(host, $$$"""{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"{{{McpServer.CatalogueUri}}}"}}""");
        JsonNode? other = await Ask(host, """{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"file:///etc/passwd"}}""");

        list!["result"]!["resources"]![0]!["uri"]!.GetValue<string>().ShouldBe(McpServer.CatalogueUri);
        read!["result"]!["contents"]![0]!["text"]!.GetValue<string>().ShouldBe("""[{"id":"stack"}]""");
        other!["error"]!["code"]!.GetValue<int>().ShouldBe(-32002);
    }

    [Theory]
    [InlineData("not json", -32700)]
    [InlineData("[1,2]", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"prompts/list"}""", -32601)]
    public async Task A_message_that_is_not_a_request_this_server_has_is_refused_with_the_JSON_RPC_code(string request, int code)
    {
        JsonNode? reply = await Ask(new ScriptedHost(), request);

        reply!["error"]!["code"]!.GetValue<int>().ShouldBe(code);
    }

    [Fact]
    public async Task Run_reads_lines_until_the_input_ends_and_writes_one_line_per_reply()
    {
        using StringReader input = new("""
            {"jsonrpc":"2.0","id":1,"method":"ping"}

            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"ping"}
            """);
        using StringWriter output = new() { NewLine = "\n" };

        await new McpServer(new ScriptedHost()).RunAsync(input, output, Ct);

        output.ToString().TrimEnd().Split('\n').Length.ShouldBe(2);
    }

    private sealed class ScriptedHost : IAdminHost
    {
        public JsonObject[] Tools { get; init; } = [];

        public ToolResult? Result { get; set; } = new ToolResult(true, "");

        public string Catalogue { get; init; } = "[]";

        public string? Unavailable { get; init; }

        public Exception? Throws { get; init; }

        public List<(string Name, JsonObject Arguments)> Calls { get; } = [];

        public Task<JsonArray> ListToolsAsync(CancellationToken cancellationToken) =>
            Fails<JsonArray>() ?? Task.FromResult(new JsonArray([.. Tools.Select(t => (JsonNode?)t.DeepClone())]));

        public Task<ToolResult?> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls.Add((name, arguments));

            return Fails<ToolResult?>() ?? Task.FromResult(Result);
        }

        public Task<string> ReadCatalogueAsync(CancellationToken cancellationToken) =>
            Fails<string>() ?? Task.FromResult(Catalogue);

        private Task<T>? Fails<T>() =>
            Throws is not null ? throw Throws : Unavailable is not null ? throw new HostUnavailableException(Unavailable) : null;
    }
}
