using System.Text.Json.Nodes;
using Admin.Host.Assistant;
using Admin.Host.Fakes;
using Common.Assistant;
using Common.Assistant.Tools;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>A server tool called outside any conversation (AI assistant design §12): the MCP route's path to a tool.</summary>
public sealed class ServerToolCallsTests : IDisposable
{
    private static readonly Caller Operator = new("operator", "Operator");

    private readonly List<AssistantCore> cores = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => cores.ForEach(core => core.Dispose());

    private ServerToolCalls Calls(AssistantLimits? limits = null, IRedactor? redactor = null, params IToolSource[] sources)
    {
        AssistantCore core = new(new FakeAssistantProvider(), sources, ToolPolicy.ReadOnly, redactor ?? new PlantedRedactor(), "Host rules.", limits ?? new AssistantLimits(), TimeProvider.System);
        cores.Add(core);

        return new ServerToolCalls(core, sources, ToolPolicy.ReadOnly);
    }

    private static ScriptedTools Tools(string name, string content, bool ok = true) =>
        new((name, (_, _, _) => Task.FromResult(new ToolOutcome(ok, content))));

    [Fact]
    public void The_server_tools_are_the_sources_sorted_with_no_client_tool()
    {
        ServerToolCalls calls = Calls(sources: [Tools("b_tool", "x"), Tools("a_tool", "x")]);

        calls.Tools().Select(t => t.Name).ShouldBe(["a_tool", "b_tool"]);
        calls.Tools().ShouldAllBe(t => t.Side == ToolSide.Server);
    }

    [Fact]
    public async Task A_call_returns_the_tool_result_redacted_for_the_caller()
    {
        ScriptedTools tools = Tools("stack_status", "token hunter22 is up");
        ServerToolCalls calls = Calls(redactor: new PlantedRedactor("hunter22"), sources: tools);

        ToolOutcome? outcome = await calls.CallAsync(Operator, "stack_status", new JsonObject { ["a"] = 1 }, Ct);

        outcome.ShouldBe(new ToolOutcome(true, "token <redacted> is up"));
        tools.Calls.Single().Context.ConversationId.ShouldBe(ServerToolCalls.OutsideConversation);
        tools.Calls.Single().Input["a"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_result_is_cut_to_the_limit_the_conversations_have()
    {
        ServerToolCalls calls = Calls(new AssistantLimits { MaxToolResultChars = 10 }, sources: Tools("big", new string('x', 50)));

        ToolOutcome? outcome = await calls.CallAsync(Operator, "big", [], Ct);

        outcome!.Content.ShouldBe($"{new string('x', 10)}\n[cut to 10 characters]");
    }

    [Fact]
    public async Task A_failing_tool_is_an_error_result_and_never_an_exception()
    {
        ServerToolCalls calls = Calls(sources: new ScriptedTools(("broken", (_, _, _) => throw new InvalidOperationException("down"))));

        ToolOutcome? outcome = await calls.CallAsync(Operator, "broken", [], Ct);

        outcome.ShouldBe(new ToolOutcome(false, "broken failed: down"));
    }

    [Fact]
    public async Task A_tool_that_outlasts_the_timeout_is_an_error_result()
    {
        ServerToolCalls calls = Calls(
            new AssistantLimits { ServerToolTimeout = TimeSpan.FromMilliseconds(50) },
            sources: new ScriptedTools(("slow", async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);

                return new ToolOutcome(true, "never");
            }
        )));

        ToolOutcome? outcome = await calls.CallAsync(Operator, "slow", [], Ct);

        outcome!.Ok.ShouldBeFalse();
        outcome.Content.ShouldStartWith("slow did not answer within");
    }

    [Fact]
    public async Task An_unknown_name_is_null()
    {
        (await Calls(sources: Tools("known", "x")).CallAsync(Operator, "open_screen", [], Ct)).ShouldBeNull();
    }

    // A host source of read-only tools whose answers the test decides.
    private sealed class ScriptedTools(params (string Name, Func<JsonObject, ToolContext, CancellationToken, Task<ToolOutcome>> Run)[] tools) : IToolSource
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = [.. tools.Select(t => new ToolDescriptor(
            t.Name,
            t.Name,
            $"Test tool {t.Name}.",
            new JsonObject { ["type"] = "object" },
            new ToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false),
            ToolSide.Server))];

        public List<(string Name, JsonObject Input, ToolContext Context)> Calls { get; } = [];

        public async ValueTask<ToolOutcome> InvokeAsync(string name, JsonObject input, ToolContext context, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((name, input, context));
            }

            return await tools.Single(t => t.Name == name).Run(input, context, cancellationToken);
        }
    }

    // A redactor that replaces each planted secret, so a test can see it never left the host.
    private sealed class PlantedRedactor(params string[] secrets) : IRedactor
    {
        public string Redact(string text, Caller caller) =>
            secrets.Aggregate(text, (redacted, secret) => redacted.Replace(secret, "<redacted>", StringComparison.Ordinal));
    }
}
