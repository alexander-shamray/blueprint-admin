using System.Text.Json.Nodes;
using Admin.Assistant.Tools;
using Shouldly;

namespace Admin.Assistant.Tests;

/// <summary>A server tool called outside any conversation (AI assistant design §12): the MCP route's path through the core.</summary>
public sealed class ServerToolCallTests
{
    private static readonly Caller Operator = new("operator", "Operator");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AssistantCore Core(AssistantLimits? limits = null, IRedactor? redactor = null, params IToolSource[] sources) =>
        new(new ScriptedProvider(), sources, ToolPolicy.ReadOnly, redactor ?? new PlantedRedactor(), "Host rules.", limits ?? new AssistantLimits(), TimeProvider.System);

    private static ScriptedTools Tools(string name, string content, bool ok = true) =>
        new((name, (_, _, _) => Task.FromResult(new ToolOutcome(ok, content))));

    [Fact]
    public void The_server_tools_are_the_sources_sorted_with_no_client_tool()
    {
        AssistantCore core = Core(sources: [Tools("b_tool", "x"), Tools("a_tool", "x")]);

        core.ServerTools().Select(t => t.Name).ShouldBe(["a_tool", "b_tool"]);
        core.ServerTools().ShouldAllBe(t => t.Side == ToolSide.Server);
    }

    [Fact]
    public async Task A_call_returns_the_tool_result_redacted_for_the_caller()
    {
        ScriptedTools tools = Tools("stack_status", "token hunter22 is up");
        AssistantCore core = Core(redactor: new PlantedRedactor("hunter22"), sources: tools);

        ToolOutcome? outcome = await core.CallServerToolAsync(Operator, "stack_status", new JsonObject { ["a"] = 1 }, Ct);

        outcome.ShouldBe(new ToolOutcome(true, "token <redacted> is up"));
        tools.Calls.Single().Context.ConversationId.ShouldBe(AssistantCore.OutsideConversation);
        tools.Calls.Single().Input["a"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_result_is_cut_to_the_limit_the_conversations_have()
    {
        AssistantCore core = Core(new AssistantLimits { MaxToolResultChars = 10 }, sources: Tools("big", new string('x', 50)));

        ToolOutcome? outcome = await core.CallServerToolAsync(Operator, "big", [], Ct);

        outcome!.Content.ShouldBe($"{new string('x', 10)}\n[cut to 10 characters]");
    }

    [Fact]
    public async Task A_failing_tool_is_an_error_result_and_never_an_exception()
    {
        AssistantCore core = Core(sources: new ScriptedTools(("broken", (_, _, _) => throw new InvalidOperationException("down"))));

        ToolOutcome? outcome = await core.CallServerToolAsync(Operator, "broken", [], Ct);

        outcome.ShouldBe(new ToolOutcome(false, "broken failed: down"));
    }

    [Fact]
    public async Task A_tool_that_outlasts_the_timeout_is_an_error_result()
    {
        AssistantCore core = Core(
            new AssistantLimits { ServerToolTimeout = TimeSpan.FromMilliseconds(50) },
            sources: new ScriptedTools(("slow", async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return new ToolOutcome(true, "never");
            }
        )));

        ToolOutcome? outcome = await core.CallServerToolAsync(Operator, "slow", [], Ct);

        outcome!.Ok.ShouldBeFalse();
        outcome.Content.ShouldStartWith("slow did not answer within");
    }

    [Fact]
    public async Task An_unknown_name_is_null()
    {
        (await Core(sources: Tools("known", "x")).CallServerToolAsync(Operator, "open_screen", [], Ct)).ShouldBeNull();
    }
}
