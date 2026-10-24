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
        // `strict` makes the model's arguments validate against the schema, which asks for every object in it to be closed.
        JsonObject closed = (JsonObject)descriptor.InputSchema.DeepClone();
        closed.TryAdd("type", "object");
        Close(closed);

        Dictionary<string, JsonElement> schema = closed.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        return new BetaTool
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Strict = true,
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    // The bounds a strict schema may not carry, which the SDK's own schema helper moves into the description
    // (SchemaPropertyAttribute in the pinned Anthropic package): numeric limits and string lengths, and minItems above 1.
    // The tool still checks its input, so the model reads the bound and the host enforces it.
    private static readonly string[] UnsupportedBounds = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength"];

    // Closes every object schema in the tree, the root and those under `properties`, `items`, `$defs` and the combinators,
    // without overruling one that already names `additionalProperties`, and moves each schema's unsupported bounds into its
    // description. Only a string `type` marks a schema, so a property called `type` in a `properties` map, which holds a
    // schema, is not mistaken for one, and nor is a property called `minimum`.
    private static void Close(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject schema:
                if (schema["type"] is JsonValue type && type.TryGetValue(out string? name))
                {
                    if (name == "object")
                    {
                        schema.TryAdd("additionalProperties", false);
                    }

                    MoveBoundsToDescription(schema);
                }

                foreach (KeyValuePair<string, JsonNode?> entry in schema.ToList())
                {
                    Close(entry.Value);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Close(item);
                }

                break;
        }
    }

    private static void MoveBoundsToDescription(JsonObject schema)
    {
        JsonObject moved = [];

        foreach (string bound in UnsupportedBounds)
        {
            if (schema[bound] is JsonValue value)
            {
                schema.Remove(bound);
                moved[bound] = value;
            }
        }

        if (schema["minItems"] is JsonValue minItems && minItems.TryGetValue(out int least) && least > 1)
        {
            schema.Remove("minItems");
            moved["minItems"] = least;
        }

        if (moved.Count > 0)
        {
            string described = schema["description"] is JsonValue d && d.TryGetValue(out string? text) ? $"{text} " : "";
            schema["description"] = $"{described}{moved.ToJsonString()}";
        }
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
