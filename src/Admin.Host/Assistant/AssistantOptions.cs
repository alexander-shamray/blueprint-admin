using Admin.Assistant;
using Admin.Assistant.Anthropic;
using Admin.Host.Config;

namespace Admin.Host.Assistant;

/// <summary>
/// <c>Admin:Assistant</c> (AI assistant design §9): which provider the assistant uses and the core's limits. No option
/// holds a credential, and <see cref="AssistantRuntime"/> refuses to start on a key under this section named like one.
/// </summary>
public sealed class AssistantOptions
{
    public const string Off = "off";
    public const string Fake = "fake";
    public const string Anthropic = "anthropic";

    // The core owns the defaults; these options only let configuration move them.
    private static readonly AssistantLimits Defaults = new();

    /// <summary>Catalog, Inventory, Ordering, Payments and the BFF (§6.5, §9).</summary>
    public static readonly IReadOnlyList<string> DefaultPlatformReads = ["catalog", "inventory", "ordering", "payments", "bff"];

    /// <summary>
    /// <c>off</c>, <c>fake</c> or <c>anthropic</c>. Unset is off, except under FakePlatform, where it is the fake provider;
    /// an explicit <c>off</c> wins there too (§9.1). Changing it takes a host restart, like every other <c>Admin:</c> key.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// The model the Anthropic provider asks (§8.2); a cheaper one such as <c>claude-haiku-5-5</c> is this setting, the
    /// operator's to change. The defaults are the adapter's own constants, which the compiler copies in, so reading them
    /// loads nothing of the adapter.
    /// </summary>
    public string Model { get; set; } = AnthropicSettings.DefaultModel;

    /// <summary>The effort the Anthropic provider asks for (§8.2); <c>low</c> for chat. Refused at startup if the SDK has no such level.</summary>
    public string Effort { get; set; } = AnthropicSettings.DefaultEffort;

    /// <summary>
    /// The services whose GET operations become the assistant's platform-data tools (§6.5), by <see cref="PlatformService.Name"/>.
    /// Null is <see cref="DefaultPlatformReads"/>; a list names exactly the services it holds, and an empty one offers
    /// none. Privacy is left out of the default: its reads are about erasure subjects, and sending them to a provider is
    /// the operator's explicit choice. Not a list initializer, because the configuration binder appends to one.
    /// </summary>
    public string[]? PlatformReads { get; set; }

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
            Anthropic => Anthropic,
            _ => throw new InvalidOperationException($"Admin:Assistant:Provider is '{configured}'; it takes 'off', 'fake' or 'anthropic'."),
        };
    }

    /// <summary>
    /// The services in <see cref="PlatformReads"/>, or the defaults; an exception naming the key when one is no service
    /// whose operations the API catalogue holds (the OpenAPI services and the BFF's curated reads).
    /// </summary>
    public IReadOnlySet<string> PlatformReadServices()
    {
        string[] known = [.. PlatformServices.All.Where(s => s.OpenApi || s.Name == "bff").Select(s => s.Name)];
        HashSet<string> chosen = new(StringComparer.Ordinal);

        foreach (string raw in PlatformReads ?? [.. DefaultPlatformReads])
        {
            string name = raw.Trim().ToLowerInvariant();

            if (!known.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Admin:Assistant:PlatformReads names '{raw}'; it takes {string.Join(", ", known)}.");
            }

            chosen.Add(name);
        }

        return chosen;
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
