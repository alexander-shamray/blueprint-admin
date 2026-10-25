using System.Text.Json.Nodes;
using Admin.Assistant.Tools;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Assistant.Tests;

/// <summary>The server tools called outside a conversation (AI assistant design §12, the MCP route), against a scripted source.</summary>
public sealed class ExternalToolTests
{
    private static readonly Caller Operator = new("operator", "Operator");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AssistantCore Core(ScriptedTools tools, IRedactor? redactor = null, AssistantLimits? limits = null, TimeProvider? time = null) =>
        new(new ScriptedProvider(), [tools], ToolPolicy.ReadOnly, redactor ?? new PlantedRedactor(), "Host rules.", limits ?? new AssistantLimits(), time ?? TimeProvider.System);

    private static (string, Func<JsonObject, ToolContext, CancellationToken, Task<ToolOutcome>>) Answers(string name, string content, bool ok = true) =>
        (name, (_, _, _) => Task.FromResult(new ToolOutcome(ok, content)));

    [Fact]
    public void The_server_tools_are_every_sources_sorted_by_name_and_none_of_a_clients()
    {
        AssistantCore core = Core(new ScriptedTools(Answers("stack_status", "{}"), Answers("get_stock", "{}")));

        core.ServerTools().Select(t => t.Name).ShouldBe(["get_stock", "stack_status"]);
        core.ServerTools().ShouldAllBe(t => t.Side == ToolSide.Server);
    }

    [Fact]
    public async Task A_call_runs_the_tool_for_the_caller_and_returns_what_it_said()
    {
        ScriptedTools tools = new(Answers("stack_status", "all running"));

        ToolOutcome outcome = await Core(tools).CallServerToolAsync(Operator, "stack_status", new JsonObject { ["service"] = "ordering" }, Ct);

        outcome.ShouldBe(new ToolOutcome(true, "all running"));
        (string name, JsonObject input, ToolContext context) = tools.Calls.Single();
        name.ShouldBe("stack_status");
        input["service"]!.GetValue<string>().ShouldBe("ordering");
        context.Caller.ShouldBe(Operator);
        context.ConversationId.ShouldBe(AssistantCore.ExternalConversation);
        context.HostOptions.Count.ShouldBe(0);
    }

    [Fact]
    public async Task What_comes_back_is_redacted_and_bounded_as_in_a_turn()
    {
        ScriptedTools tools = new(Answers("stack_status", "the password is hunter2 " + new string('x', 100)));
        AssistantCore core = Core(tools, new PlantedRedactor("hunter2"), new AssistantLimits { MaxToolResultChars = 40 });

        ToolOutcome outcome = await core.CallServerToolAsync(Operator, "stack_status", [], Ct);

        outcome.Content.ShouldNotContain("hunter2");
        outcome.Content.ShouldStartWith("the password is <redacted> ");
        outcome.Content.ShouldEndWith("[cut to 40 characters]");
    }

    [Fact]
    public async Task A_tool_that_says_it_failed_is_an_error_result_not_an_exception()
    {
        AssistantCore core = Core(new ScriptedTools(Answers("get_stock", "The platform refused.", ok: false)));

        (await core.CallServerToolAsync(Operator, "get_stock", [], Ct)).ShouldBe(new ToolOutcome(false, "The platform refused."));
    }

    [Fact]
    public async Task A_tool_that_throws_is_an_error_result_naming_it()
    {
        AssistantCore core = Core(new ScriptedTools(("stack_status", (_, _, _) => throw new InvalidOperationException("compose is down"))));

        ToolOutcome outcome = await core.CallServerToolAsync(Operator, "stack_status", [], Ct);

        outcome.ShouldBe(new ToolOutcome(false, "stack_status failed: compose is down"));
    }

    [Fact]
    public async Task A_tool_that_runs_past_the_server_limit_is_an_error_result()
    {
        FakeTimeProvider time = new();
        AssistantCore core = Core(
            new ScriptedTools(("stack_status", async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return new ToolOutcome(true, "never");
            }
        )),
            time: time,
            limits: new AssistantLimits { ServerToolTimeout = TimeSpan.FromSeconds(5) });

        Task<ToolOutcome> call = core.CallServerToolAsync(Operator, "stack_status", [], Ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(6));

        (await call).ShouldBe(new ToolOutcome(false, "stack_status did not answer within 5 s."));
    }

    [Fact]
    public async Task A_name_no_source_has_is_an_error_result_and_never_a_client_tool()
    {
        AssistantCore core = Core(new ScriptedTools(Answers("stack_status", "{}")));

        (await core.CallServerToolAsync(Operator, "open_screen", [], Ct)).ShouldBe(new ToolOutcome(false, "There is no tool named open_screen."));
    }
}
