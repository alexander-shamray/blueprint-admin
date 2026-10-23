using Admin.Assistant;
using Admin.Host.Config;

namespace Admin.Host.Assistant;

/// <summary>
/// <c>Admin:Assistant</c> (AI assistant design §9): which provider the assistant uses and the core's limits. No option
/// holds a credential, and <see cref="AssistantRuntime"/> refuses to start on a key under this section named like one.
/// Phase 2 adds the model and effort a real provider takes, and phase 3b the services whose reads become tools.
/// </summary>
public sealed class AssistantOptions
{
    public const string Off = "off";
    public const string Fake = "fake";

    // The core owns the defaults; these options only let configuration move them.
    private static readonly AssistantLimits Defaults = new();

    /// <summary>
    /// <c>off</c> or <c>fake</c>. Unset is off, except under FakePlatform, where it is the fake provider; an explicit
    /// <c>off</c> wins there too (§9.1). Changing it takes a host restart, like every other <c>Admin:</c> key.
    /// </summary>
    public string? Provider { get; set; }

    public int MaxSteps { get; set; } = Defaults.MaxSteps;

    public int MaxOutputTokens { get; set; } = Defaults.MaxOutputTokens;

    public long ConversationTokenBudget { get; set; } = Defaults.ConversationTokenBudget;

    public TimeSpan ClientToolTimeout { get; set; } = Defaults.ClientToolTimeout;

    public TimeSpan ServerToolTimeout { get; set; } = Defaults.ServerToolTimeout;

    public int MaxConversations { get; set; } = Defaults.MaxConversations;

    public int MaxCallers { get; set; } = Defaults.MaxCallers;

    public TimeSpan IdleTimeout { get; set; } = Defaults.IdleTimeout;

    /// <summary>The provider in effect, by §9's rule, or an exception naming the key when the value is not one.</summary>
    public static string ProviderOf(AdminOptions options)
    {
        string? configured = options.Assistant.Provider?.Trim();

        if (string.IsNullOrEmpty(configured))
        {
            return options.FakePlatform ? Fake : Off;
        }

        return configured.ToLowerInvariant() switch
        {
            Off => Off,
            Fake => Fake,
            _ => throw new InvalidOperationException(
                $"Admin:Assistant:Provider is '{configured}'; it takes 'off' or 'fake'. The Anthropic provider arrives with #429."),
        };
    }

    public AssistantLimits Limits()
    {
        Positive(MaxSteps, nameof(MaxSteps));
        Positive(MaxOutputTokens, nameof(MaxOutputTokens));
        Positive(ConversationTokenBudget, nameof(ConversationTokenBudget));
        Positive(MaxConversations, nameof(MaxConversations));
        Positive(MaxCallers, nameof(MaxCallers));
        Positive(ClientToolTimeout.Ticks, nameof(ClientToolTimeout));
        Positive(ServerToolTimeout.Ticks, nameof(ServerToolTimeout));
        Positive(IdleTimeout.Ticks, nameof(IdleTimeout));

        return new AssistantLimits
        {
            MaxSteps = MaxSteps,
            MaxOutputTokens = MaxOutputTokens,
            ConversationTokenBudget = ConversationTokenBudget,
            ClientToolTimeout = ClientToolTimeout,
            ServerToolTimeout = ServerToolTimeout,
            MaxConversations = MaxConversations,
            MaxCallers = MaxCallers,
            IdleTimeout = IdleTimeout,
        };
    }

    private static void Positive(long value, string key)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"Admin:Assistant:{key} must be positive.");
        }
    }
}
