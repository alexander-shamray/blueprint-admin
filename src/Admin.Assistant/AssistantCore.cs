using System.Text.Json.Nodes;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Assistant.Tools;

namespace Admin.Assistant;

/// <summary>
/// The core a host builds once (AI assistant design §4): its provider, its tool sources under its policy, its redactor,
/// its system text and its limits, and the conversations open against them. The host authenticates and passes the
/// <see cref="Caller"/>; the core keys everything by it. Every server tool is admitted here, at construction, so a host
/// whose source offers a tool the policy refuses fails to start rather than at a turn.
/// </summary>
public sealed class AssistantCore
{
    private readonly IReadOnlyList<IToolSource> sources;
    private readonly ToolPolicy policy;
    private readonly ConversationStore store;

    /// <exception cref="ToolRefusedException">A host source offers a tool the policy refuses.</exception>
    public AssistantCore(
        IAssistantProvider provider,
        IReadOnlyList<IToolSource> sources,
        ToolPolicy policy,
        IRedactor redactor,
        string hostInstructions,
        AssistantLimits limits,
        TimeProvider time,
        Action<TurnSummary>? turnEnded = null)
    {
        _ = ToolRegistry.Build(sources, [], policy);
        Provider = provider;
        this.sources = sources;
        this.policy = policy;
        Redactor = redactor;
        System = hostInstructions.Length == 0 ? Prompt.Core : $"{Prompt.Core}\n{hostInstructions}";
        Limits = limits;
        Time = time;
        TurnEnded = turnEnded;
        store = new ConversationStore(limits, time);
    }

    public IAssistantProvider Provider { get; }

    public IRedactor Redactor { get; }

    /// <summary>The fixed system text: the core's, then the host's. The catalogue follows it in each request.</summary>
    public string System { get; }

    public AssistantLimits Limits { get; }

    public TimeProvider Time { get; }

    internal Action<TurnSummary>? TurnEnded { get; }

    public async ValueTask<AssistantStatus> StatusAsync(CancellationToken cancellationToken)
    {
        ProviderStatus status = await Provider.StatusAsync(cancellationToken);

        return new AssistantStatus(status.Available, Provider.Id, status.Model, status.Available ? null : status.Reason);
    }

    /// <summary>A new conversation, or null when the caller's store is full of running turns.</summary>
    /// <exception cref="ToolRefusedException">The client offers a tool the policy refuses, or one whose name is taken.</exception>
    public AssistantSession? Open(Caller caller, OpenConversation request)
    {
        ToolRegistry registry = ToolRegistry.Build(sources, request.ClientTools, policy);

        return store.Add(caller, id => new AssistantSession(id, caller, request, registry, this));
    }

    /// <summary>
    /// The host's server tools as a client with no conversation sees them (AI assistant design §12, the MCP route): every
    /// source's, each admitted by the policy, and none of a client's, since there is no browser to run those.
    /// </summary>
    public IReadOnlyList<ToolDescriptor> ServerTools() => ToolRegistry.Build(sources, [], policy).All;

    /// <summary>
    /// One server tool, called outside a conversation, with what a turn's call has: the same time limit, the same redaction
    /// of what comes back and the same bound on its size (§9, §10). An unknown name is an error result, as in a turn.
    /// </summary>
    public async ValueTask<ToolOutcome> CallServerToolAsync(Caller caller, string name, JsonObject input, CancellationToken cancellationToken)
    {
        if (!ToolRegistry.Build(sources, [], policy).TryServer(name, out IToolSource? source) || source is null)
        {
            return new ToolOutcome(false, $"There is no tool named {name}.");
        }

        ToolOutcome outcome = await InvokeAsync(source, name, input, new ToolContext(caller, ExternalConversation, []), cancellationToken);

        return outcome with { Content = Bound(Redactor.Redact(outcome.Content, caller)) };
    }

    /// <summary>The conversation id a tool's context carries when it is called outside a conversation.</summary>
    public const string ExternalConversation = "external";

    /// <summary>The caller's conversation of that id; null for an unknown one and for another caller's alike.</summary>
    public AssistantSession? Find(Caller caller, string id) => store.Find(caller, id);

    /// <summary>A server tool call under the time limit; a timeout or a failure is the model's error result, never a throw.</summary>
    internal async Task<ToolOutcome> InvokeAsync(IToolSource source, string name, JsonObject input, ToolContext context, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(Limits.ServerToolTimeout, Time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await source.InvokeAsync(name, input, context, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolOutcome(false, $"{name} did not answer within {Limits.ServerToolTimeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ToolOutcome(false, $"{name} failed: {e.Message}");
        }
    }

    /// <summary>A tool result cut to <see cref="AssistantLimits.MaxToolResultChars"/>, with a note that it was.</summary>
    internal string Bound(string content) =>
        content.Length <= Limits.MaxToolResultChars
            ? content
            : string.Concat(content.AsSpan(0, Limits.MaxToolResultChars), $"\n[cut to {Limits.MaxToolResultChars} characters]");

    public bool Delete(Caller caller, string id) => store.Delete(caller, id);
}
