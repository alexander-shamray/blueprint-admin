using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Admin.Host.Security;
using Admin.Host.Tests.TestSupport;
using Admin.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>
/// The MCP route (AI assistant design §12) against FakePlatform: the host's two tool routes, and the stdio server of
/// <c>src/Admin.Mcp</c> answering a stack question and a platform-data question through them, as an agent would.
/// </summary>
public sealed class McpRouteTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private const string StockedProduct = "0199a1b2-0000-7000-8000-00000000000a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonNode> Ask(McpServer server, string method, string? parameters = null, int id = 1) =>
        (await server.HandleAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{(parameters is null ? "" : $",\"params\":{parameters}")}}}""", Ct))!;

    [Fact]
    public void The_MCP_process_accepts_exactly_the_host_names_the_console_answers()
    {
        ConsoleUrl.Hosts.ShouldBe(LoopbackOriginGuard.LoopbackHosts);
    }

    [Fact]
    public async Task The_host_lists_its_server_tools_and_not_the_clients()
    {
        HttpClient client = factory.CreateClient();

        JsonArray tools = (await client.GetFromJsonAsync<JsonArray>("/api/assistant/tools", Ct))!;

        string[] names = [.. tools.Select(t => t!["name"]!.GetValue<string>())];
        names.ShouldContain("stack_status");
        names.ShouldContain("get_stock");
        names.ShouldNotContain("open_screen");
        tools.ShouldAllBe(t => t!["side"]!.GetValue<string>() == "server");
        names.ShouldBe([.. names.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task A_tool_is_called_by_name_with_a_json_object_and_answers_ok_and_content()
    {
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/assistant/tools/stack_status", new JsonObject(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode outcome = (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        outcome["ok"]!.GetValue<bool>().ShouldBeTrue();
        JsonNode.Parse(outcome["content"]!.GetValue<string>()).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_call_whose_body_is_not_a_json_object_is_refused_before_any_tool_runs()
    {
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/assistant/tools/stack_status", new JsonArray(1, 2), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_tool_is_a_404_problem_naming_it_and_a_client_tool_name_is_unknown_too()
    {
        HttpClient client = factory.CreateClient();

        foreach (string name in new[] { "no_such_tool", "open_screen" })
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/assistant/tools/{name}", new JsonObject(), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!["detail"]!.GetValue<string>().ShouldBe(name);
        }
    }

    [Fact]
    public async Task With_the_assistant_off_neither_route_is_mapped()
    {
        using WebApplicationFactory<Program> off = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Assistant:Provider", "off"));
        HttpClient client = off.CreateClient();

        (await client.GetAsync("/api/assistant/tools", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/assistant/tools/stack_status", new JsonObject(), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_agent_lists_the_tools_and_answers_a_stack_question_and_a_platform_data_question()
    {
        McpServer server = new(factory.CreateClient());

        JsonNode initialised = await Ask(server, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"agent","version":"1"}}""");
        initialised["result"]!["serverInfo"]!["name"]!.GetValue<string>().ShouldBe("blueprint-admin");

        JsonNode listed = await Ask(server, "tools/list", id: 2);
        string[] names = [.. listed["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
        names.ShouldContain("stack_status");
        names.ShouldContain("get_stock");

        // A stack question: what the Stack screen shows now.
        JsonNode stack = await Ask(server, "tools/call", """{"name":"stack_status","arguments":{}}""", 3);
        stack["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        string stackText = stack["result"]!["content"]!.AsArray().Single()!["text"]!.GetValue<string>();
        JsonNode.Parse(stackText).ShouldNotBeNull();

        // A platform-data question: the platform's live data through the gateway, as a realm user.
        JsonNode stock = await Ask(server, "tools/call", $$$"""{"name":"get_stock","arguments":{"productId":"{{{StockedProduct}}}"}}""", 4);
        stock["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        stock["result"]!["content"]!.AsArray().Single()!["text"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }
}
