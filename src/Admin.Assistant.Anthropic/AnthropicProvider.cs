using System.Runtime.CompilerServices;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Admin.Assistant.Anthropic;

/// <summary>
/// The Claude models behind <see cref="IAssistantProvider"/> (AI assistant design §8.2). It takes the SDK client its host
/// built and never builds one itself, so where a credential comes from is the host's: the admin host leaves it to the
/// SDK's own resolution (<c>ANTHROPIC_API_KEY</c>, or the profile <c>ant auth login</c> stores), and a deployed host can
/// build the client from its own secret store. Nothing here reads, keeps or reports a credential.
/// </summary>
public sealed class AnthropicProvider : IAssistantProvider
{
    public const string ProviderId = "anthropic";

    // A probe that found a credential is trusted for a while; one that did not is asked again sooner, so a login made
    // after the host started is noticed without a restart.
    private static readonly TimeSpan Trusted = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Doubted = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly AnthropicClient client;
    private readonly AnthropicSettings settings;
    private readonly TimeProvider time;
    private readonly Action<string> warn;
    private readonly Lock gate = new();

    private ProviderStatus? known;
    private DateTimeOffset knownUntil;
    private Task<ProviderStatus>? inFlight;

    /// <param name="warn">
    /// Told the exception type, and only the type, of a probe that failed for a reason other than a refused credential:
    /// <c>unreachable</c> is the protocol's one word for it, so this is where an operator sees that the model id was
    /// rejected or the account was rate limited. Never a message, which can carry what the API echoed back.
    /// </param>
    public AnthropicProvider(AnthropicClient client, AnthropicSettings settings, TimeProvider time, Action<string>? warn = null)
    {
        this.warn = warn ?? (_ => { });
        this.client = client;
        this.settings = settings;
        this.time = time;
    }

    public string Id => ProviderId;

    /// <summary>
    /// Whether the model can be reached with the credential the SDK resolved: one authenticated read of the model's
    /// record, which costs no tokens. A rejected credential, or none, is <c>no-credential</c>; anything else that keeps
    /// the read from answering is <c>unreachable</c>.
    /// </summary>
    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken)
    {
        Task<ProviderStatus> asked;

        lock (gate)
        {
            if (known is not null && time.GetUtcNow() < knownUntil)
            {
                return ValueTask.FromResult(known);
            }

            // Callers that arrive while a probe is out share it, and none of them can cancel it for the others. A finished
            // probe is never shared: a probe that completes before this line returns would otherwise be remembered.
            if (inFlight is null || inFlight.IsCompleted)
            {
                inFlight = RefreshAsync();
            }

            asked = inFlight;
        }

        return new ValueTask<ProviderStatus>(asked.WaitAsync(cancellationToken));
    }

    private async Task<ProviderStatus> RefreshAsync()
    {
        ProviderStatus status = await ProbeAsync();

        lock (gate)
        {
            known = status;
            knownUntil = time.GetUtcNow() + (status.Available ? Trusted : Doubted);
        }

        return status;
    }

    public async IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MessageCreateParams parameters = RequestBuilder.Build(request, settings);
        StreamMapper mapper = new();

        await foreach (BetaRawMessageStreamEvent streamEvent in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
        {
            foreach (ProviderEvent value in mapper.On(streamEvent))
            {
                yield return value;
            }
        }

        // A stream that ends without `message_stop` was cut off: a half reply must not be taken for a finished one, and the
        // core ends a turn it is told failed.
        if (!mapper.Stopped)
        {
            throw new IOException("The provider's stream ended before the reply was complete.");
        }
    }

    private async Task<ProviderStatus> ProbeAsync()
    {
        using CancellationTokenSource timeout = new(ProbeTimeout);

        try
        {
            await client.Models.Retrieve(settings.Model, cancellationToken: timeout.Token);

            return new ProviderStatus(true, settings.Model, null);
        }
        catch (Exception e) when (e is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            return new ProviderStatus(false, settings.Model, StatusReason.NoCredential);
        }
        catch (Exception e)
        {
            // A connection that fails, a timeout, a 5xx: whatever kept the read from answering. The protocol has no word for
            // a model id the API does not know, so the type is what tells that 404 from a refused connection.
            warn($"The Anthropic status probe for model '{settings.Model}' failed with {e.GetType().Name}.");

            return new ProviderStatus(false, settings.Model, StatusReason.Unreachable);
        }
    }
}
