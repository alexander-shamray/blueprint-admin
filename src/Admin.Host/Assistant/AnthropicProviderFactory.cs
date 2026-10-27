using Admin.Host.Config;
using Anthropic;
using Common.Assistant.Anthropic;
using Common.Assistant.Providers;

namespace Admin.Host.Assistant;

/// <summary>
/// Builds the Anthropic provider (AI assistant design §8.2, §9.1). It sits in its own type so that nothing of the vendor
/// SDK is touched until <see cref="AssistantRuntime"/> asks for it, which it does only when the provider is
/// <c>anthropic</c>: with the assistant off, or on the fake, this assembly's code is never loaded.
/// </summary>
internal static class AnthropicProviderFactory
{
    /// <summary>
    /// The client is the SDK's own, so the credential is whatever the SDK resolves: <c>ANTHROPIC_API_KEY</c>, or the
    /// profile <c>ant auth login</c> stores. The console reads none of it, keeps none and has no key that holds one. With
    /// none, the provider reports <c>no-credential</c> and the rest of the console is as it was. The package's provider
    /// reports a failed probe only as <c>unreachable</c>, with no hook that names why (blueprint-backend#1494).
    /// </summary>
    public static IAssistantProvider Create(AssistantOptions options, TimeProvider time) =>
        new AnthropicProvider(
            new AnthropicClient(),
            new AnthropicSettings(options.Model, options.Effort).Validated($"{AdminOptions.Section}:Assistant"),
            time);
}
