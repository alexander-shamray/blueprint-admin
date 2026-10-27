using System.Text.Json.Nodes;
using Common.Assistant;
using Common.Assistant.Tools;

namespace Admin.Host.Assistant;

/// <summary>
/// The host's server tools called outside any conversation (AI assistant design §12): what the MCP route lists and runs.
/// They are built through the same <see cref="ToolRegistry"/> and policy a conversation's tools are, and a call runs
/// under the core's timeout, redactor and size bound, as a conversation's server tool call does inside the core. It is
/// held here because <c>Common.Assistant</c> calls a tool only inside a conversation.
/// </summary>
public sealed class ServerToolCalls(AssistantCore core, IReadOnlyList<IToolSource> sources, ToolPolicy policy)
{
    /// <summary>The conversation id a tool call made outside any conversation carries in its <see cref="ToolContext"/>.</summary>
    public const string OutsideConversation = "none";

    /// <summary>The host's server tools as they stand now, with no client's: what the MCP route lists.</summary>
    /// <exception cref="ToolRefusedException">A host source offers a tool the policy refuses.</exception>
    public IReadOnlyList<ToolDescriptor> Tools() => ToolRegistry.Build(sources, [], policy).All;

    /// <summary>
    /// Runs one server tool outside a conversation. Null when no server tool has that name, which includes a client
    /// tool's.
    /// </summary>
    /// <exception cref="ToolRefusedException">A host source offers a tool the policy refuses.</exception>
    public async ValueTask<ToolOutcome?> CallAsync(Caller caller, string name, JsonObject input, CancellationToken cancellationToken)
    {
        if (!ToolRegistry.Build(sources, [], policy).TryServer(name, out IToolSource? source) || source is null)
        {
            return null;
        }

        ToolOutcome outcome = await RunAsync(source, name, input, new ToolContext(caller, OutsideConversation, []), cancellationToken);
        string content = core.Redactor.Redact(outcome.Content, caller);
        int max = core.Limits.MaxToolResultChars;

        // Redacted for the caller (§10), then cut to the bound a conversation's result is cut to.
        return outcome with
        {
            Content = content.Length <= max ? content : string.Concat(content.AsSpan(0, max), $"\n[cut to {max} characters]"),
        };
    }

    // One call under AssistantLimits.ServerToolTimeout; a failure is an error result, never an exception.
    private async Task<ToolOutcome> RunAsync(IToolSource source, string name, JsonObject input, ToolContext context, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(core.Limits.ServerToolTimeout, core.Time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await source.InvokeAsync(name, input, context, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolOutcome(false, $"{name} did not answer within {core.Limits.ServerToolTimeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ToolOutcome(false, $"{name} failed: {e.Message}");
        }
    }
}
