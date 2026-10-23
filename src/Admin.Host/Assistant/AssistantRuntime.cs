using Admin.Assistant;
using Admin.Assistant.Providers;
using Admin.Assistant.Tools;
using Admin.Host.Config;
using Admin.Host.Fakes;

namespace Admin.Host.Assistant;

/// <summary>
/// The assistant as this host runs it (AI assistant design §9.1), decided once at startup. Off, no provider is built,
/// no credential is resolved and <see cref="Core"/> is null, so only the status route is mapped. On, the core is built
/// with the admin's caller, policy, redactor and instructions. A wrong provider value, a limit that is not positive or a
/// key under <c>Admin:Assistant</c> named like a secret fails startup with the key to fix.
/// </summary>
public sealed partial class AssistantRuntime
{
    /// <summary>The console has no login (spec §8), so it has exactly one caller (§5.2).</summary>
    public static readonly Caller Operator = new("operator", "Operator");

    /// <summary>The admin's own instructions, after the core's: what the catalogue is and how the console is reached.</summary>
    public const string Instructions = """
        The client is blueprint-admin, the local console a developer runs the blueprint platform from. Its catalogue
        entries are screens: open one with open_screen, passing its id as the path.
        """;

    // A word that marks a credential, as the workspace refuses one in a widget's config (widget-workspace spec §6.4).
    private static readonly string[] SecretWords = ["password", "secret", "token", "apikey", "api_key", "credential", "authorization"];

    // The options' own names that hold one of those words and are no credential.
    private static readonly string[] Declared = [nameof(AssistantOptions.MaxOutputTokens), nameof(AssistantOptions.ConversationTokenBudget)];

    private readonly ILogger<AssistantRuntime> logger;

    // The model the provider is configured with, for the turn log (§10); the host, not the core, knows it.
    private readonly string model;

    public AssistantRuntime(
        AdminOptions options,
        IConfiguration configuration,
        Func<IAssistantProvider> fake,
        Func<IRedactor> redactor,
        TimeProvider time,
        ILogger<AssistantRuntime> logger)
    {
        this.logger = logger;
        RefuseSecretNamedKeys(configuration.GetSection($"{AdminOptions.Section}:Assistant"));
        ProviderId = AssistantOptions.ProviderOf(options);
        AssistantLimits limits = options.Assistant.Limits();

        (Core, model) = ProviderId switch
        {
            AssistantOptions.Fake => (new AssistantCore(fake(), [], ToolPolicy.ReadOnly, redactor(), Instructions, limits, time, Logged), FakeAssistantProvider.Model),
            _ => ((AssistantCore?)null, ""),
        };
    }

    /// <summary><c>off</c> or the provider in use.</summary>
    public string ProviderId { get; }

    /// <summary>Null when the assistant is off: then nothing of it exists but the status route's answer.</summary>
    public AssistantCore? Core { get; }

    private static void RefuseSecretNamedKeys(IConfigurationSection section)
    {
        foreach (IConfigurationSection key in section.GetChildren())
        {
            if (!Declared.Contains(key.Key, StringComparer.OrdinalIgnoreCase)
                && SecretWords.Any(word => key.Key.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"{key.Path} is named like a secret; the console holds no provider credential, and no Admin:Assistant key may (AI assistant design §9).");
            }

            RefuseSecretNamedKeys(key);
        }
    }

    // Each turn's provider, model, usage and end reason, never its text (§10).
    private void Logged(TurnSummary turn) =>
        TurnEnded(logger, turn.Provider, model, turn.ConversationId, turn.Reason, turn.Usage.Input, turn.Usage.Output, turn.Usage.Cached);

    [LoggerMessage(Level = LogLevel.Information, Message = "Assistant turn ended: provider {Provider}, model {Model}, conversation {Conversation}, {Reason}; tokens in {Input}, out {Output}, cached {Cached}")]
    private static partial void TurnEnded(ILogger logger, string provider, string model, string conversation, string reason, long input, long output, long cached);
}
