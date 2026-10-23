using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Assistant.Providers;
using Admin.Assistant.Tools;
using Anthropic.Models.Beta.Messages;

namespace Admin.Assistant.Anthropic;

/// <summary>
/// One provider round as a Messages request (AI assistant design §8.2). The request is in cache order: tools, then the
/// system text and the catalogue, then the history, with a breakpoint at the end of the first three and one at the end
/// of the history, so the next request reads the conversation so far from the cache. Nothing that varies comes before
/// either: the turn's context and the language rule are already inside the latest user message.
/// </summary>
internal static class RequestBuilder
{
    /// <summary>The beta that carries the server-side refusal fallback, which is on a beta endpoint.</summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    public static MessageCreateParams Build(TurnRequest request, AnthropicSettings settings)
    {
        return new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = request.MaxOutputTokens,
            Betas = [FallbackBeta],
            // The model's own server-defined fallback when a safety classifier declines; a refusal that survives it arrives
            // as the `refusal` stop reason, which the mapper reports as refused.
            Fallbacks = new BetaFallbacksParam(new Default()),
            // Adaptive thinking is the model's default, so the parameter is omitted. `auto` because the current models
            // refuse a forced tool choice.
            ToolChoice = new BetaToolChoiceAuto(),
            OutputConfig = new BetaOutputConfig { Effort = EffortOf(settings.Effort) },
            Tools = [.. request.Tools.Select(Tool)],
            System = new List<BetaTextBlockParam>
            {
                new() { Text = request.System },
                new() { Text = Prompt.Catalogue(request.Catalogue), CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = Messages(request.History),
        };
    }

    private static BetaToolUnion Tool(ToolDescriptor descriptor)
    {
        Dictionary<string, JsonElement> schema = descriptor.InputSchema.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        // `strict` makes the model's arguments validate against the schema, which asks for a closed object.
        schema.TryAdd("additionalProperties", JsonSerializer.SerializeToElement(false));

        return new BetaTool
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Strict = true,
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    // A block is built with the cache mark or without it, because the SDK's blocks cannot be changed once made; the
    // breakpoint at the end of the history goes on the last block of the last message, whatever kind it is.
    private delegate BetaContentBlockParam Block(BetaCacheControlEphemeral? mark);

    private static List<BetaMessageParam> Messages(IReadOnlyList<HistoryMessage> history)
    {
        List<BetaMessageParam> messages = [];

        for (int i = 0; i < history.Count; i++)
        {
            (Role role, List<Block> blocks)? message = history[i] switch
            {
                UserMessage user => (Role.User, [mark => TextBlock(user.Content, mark)]),
                AssistantMessage assistant => (Role.Assistant, Blocks(assistant)),
                ToolResultsMessage results => (Role.User, Results(results)),
                _ => null,
            };

            if (message is not { } built || built.blocks.Count == 0)
            {
                continue;
            }

            bool last = i == history.Count - 1;
            List<BetaContentBlockParam> content = [];

            for (int b = 0; b < built.blocks.Count; b++)
            {
                content.Add(built.blocks[b](last && b == built.blocks.Count - 1 ? new BetaCacheControlEphemeral() : null));
            }

            messages.Add(new BetaMessageParam { Role = built.role, Content = content });
        }

        return messages;
    }

    // The reasoning first, as it was returned, then the text, then the tool calls.
    private static List<Block> Blocks(AssistantMessage message)
    {
        List<Block> blocks = [.. message.Opaque.Select(OpaqueBlocks.ToParam).OfType<BetaContentBlockParam>().Select<BetaContentBlockParam, Block>(b => _ => b)];

        if (message.Text.Length > 0)
        {
            blocks.Add(mark => TextBlock(message.Text, mark));
        }

        foreach (ToolCall call in message.Calls)
        {
            blocks.Add(mark => ToolUse(call, mark));
        }

        return blocks;
    }

    private static List<Block> Results(ToolResultsMessage message) =>
        [.. message.Results.Select<ToolResult, Block>(r => mark => ToolResult(r, mark))];

    // The SDK writes a property it was given as null, and the cache mark is set on one block in a request, so a block
    // without it is built without the property.
    private static BetaTextBlockParam TextBlock(string text, BetaCacheControlEphemeral? mark) =>
        mark is null ? new BetaTextBlockParam { Text = text } : new BetaTextBlockParam { Text = text, CacheControl = mark };

    private static BetaToolUseBlockParam ToolUse(ToolCall call, BetaCacheControlEphemeral? mark)
    {
        Dictionary<string, JsonElement> input = call.Input.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        return mark is null
            ? new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input }
            : new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input, CacheControl = mark };
    }

    private static BetaToolResultBlockParam ToolResult(ToolResult result, BetaCacheControlEphemeral? mark) =>
        mark is null
            ? new BetaToolResultBlockParam { ToolUseID = result.CallId, Content = result.Content, IsError = !result.Ok }
            : new BetaToolResultBlockParam { ToolUseID = result.CallId, Content = result.Content, IsError = !result.Ok, CacheControl = mark };

    private static Effort EffortOf(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException($"The effort '{effort}' is not one of {string.Join(", ", AnthropicSettings.Efforts)}."),
    };
}
