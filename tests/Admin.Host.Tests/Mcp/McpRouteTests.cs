using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Admin.Host.Assistant;
using Admin.Host.Tests.TestSupport;
using Admin.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Admin.Host.Tests.Mcp;

/// <summary>
/// The MCP route end to end (AI assistant design §12, §15 phase 5) on FakePlatform: a client lists the tools and answers
/// a stack question and a platform-data question, the server speaking to the host over HTTP as the stdio process does.
/// </summary>
public sealed class McpRouteTests : IClassFixture<AdminHostFactory>
{
    private const string StockedProduct = "0199a1b2-0000-7000-8000-00000000000a";

    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public McpRouteTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<JsonNode>> Session(HttpClient host, params string[] requests)
    {
        using StringReader input = new(string.Join('\n', requests));
        using StringWriter output = new() { NewLine = "\n" };

        await new McpServer(new HttpAdminHost(host)).RunAsync(input, output, Ct);

        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)];
    }

    [Fact]
    public async Task A_client_lists_the_tools_and_answers_a_stack_question_and_a_platform_data_question()
    {
        List<JsonNode> replies = await Session(
            client,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"stack_status","arguments":{}}}""",
            $$$$"""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_stock","arguments":{"productId":"{{{{StockedProduct}}}}"}}}""");

        replies.Count.ShouldBe(4);

        string[] names = [.. replies[1]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
        names.ShouldContain("stack_status");
        names.ShouldContain("get_stock");
        names.ShouldNotContain("open_screen");

        replies[2]["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        replies[2]["result"]!["content"]![0]!["text"]!.GetValue<string>().ShouldNotBeEmpty();

        replies[3]["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        JsonNode stock = JsonNode.Parse(replies[3]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        stock["status"]!.GetValue<int>().ShouldBe(200);
        stock["body"]!.GetValue<string>().ShouldContain("\"available\":10");
    }

    [Fact]
    public async Task The_host_lists_only_server_tools_each_read_only()
    {
        JsonArray tools = JsonNode.Parse(await client.GetStringAsync("/api/assistant/tools", Ct))!.AsArray();

        tools.ShouldNotBeEmpty();
        tools.ShouldAllBe(t => t!["side"]!.GetValue<string>() == "server" && t["annotations"]!["readOnlyHint"]!.GetValue<bool>() && !t["annotations"]!["destructiveHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_call_of_an_unknown_tool_is_a_404_problem_the_process_reads_as_unknown_and_not_as_an_assistant_that_is_off()
    {
        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/assistant/tools/open_screen", new { }, Ct);

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonNode.Parse(await missing.Content.ReadAsStringAsync(Ct))!["title"]!.GetValue<string>().ShouldBe(AssistantEndpoints.NoSuchTool);
        (await new HttpAdminHost(client).CallToolAsync("open_screen", [], Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_call_with_an_empty_arguments_object_runs_a_tool_that_needs_none()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/assistant/tools/stack_status", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["ok"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task With_the_assistant_off_the_tool_routes_are_the_catch_alls_404_and_the_server_says_so()
    {
        using WebApplicationFactory<Program> off = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Assistant:Provider", "off"));
        HttpClient offClient = off.CreateClient();

        (await offClient.GetAsync("/api/assistant/tools", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await offClient.PostAsJsonAsync("/api/assistant/tools/stack_status", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        List<JsonNode> replies = await Session(
            offClient,
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"stack_status"}}""");

        replies[0]["error"]!["message"]!.GetValue<string>().ShouldContain("assistant is off");
        replies[1]["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
    }
}
