using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Assistant;
using Common.Assistant;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;

namespace Admin.Host.Fakes;

/// <summary>
/// The assistant FakePlatform plays (AI assistant design §8.3): no credential and no network. It answers the caller's
/// latest message by <see cref="FakeAssistantScripts"/>, and a tool round's result by saying what it opened or what the
/// read returned, so e2e can ask where the logs are and see <c>open_screen</c> carried out, and ask about the stack and
/// see <c>stack_status</c> read. Its usage is a count of characters, so a turn's usage is never zero.
/// </summary>
public sealed class FakeAssistantProvider : IAssistantProvider
{
    public const string Model = "scripted";

    private const string StackStatus = "stack_status";

    public string Id => AssistantOptions.Fake;

    public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ProviderStatus(true, Model, null));

    public async IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        string question = Prompt.CallerText(request.History.OfType<UserMessage>().LastOrDefault()?.Content ?? "");
        bool russian = FakeAssistantScripts.IsRussian(question);
        long input = request.History.Sum(m => m.ToString().Length) / 4;

        if (request.History is [.., AssistantMessage { Calls: [ToolCall call, ..] }, ToolResultsMessage { Results: [ToolResult result, ..] }])
        {
            string reply = call.Name switch
            {
                StackStatus => StackReply(result, russian),
                "get_stock" or "get_payment" => PlatformReply(call.Name, result, russian),
                "open_page" or "propose_widget" => WidgetReply(request, russian),
                _ => OpenReply(request, call, result, russian),
            };

            yield return new TextDelta(reply);
            yield return new UsageReport(new Usage(input, reply.Length / 4, 0));
            yield return new RoundEnd(ProviderStop.Done);
            yield break;
        }

        // An id and a word for stock or payment: the platform read, before "status" is taken for the stack's.
        if (FakeAssistantScripts.PlatformAsk(question) is (string tool, string id)
            && request.Tools.FirstOrDefault(t => t.Name == tool)?.InputSchema["required"]?[0]?.GetValue<string>() is { } parameter)
        {
            string fetching = FakeAssistantScripts.Fetching(russian);

            yield return new TextDelta(fetching);
            yield return new ToolCall($"fake-{Guid.NewGuid():N}", tool, new JsonObject { [parameter] = id });
            yield return new UsageReport(new Usage(input, fetching.Length / 4, 0));
            yield return new RoundEnd(ProviderStop.Done);
            yield break;
        }

        // A message about a widget by name (§6.4): find it on its page and outline it, or offer to add it.
        if (FakeAssistantScripts.WidgetAsk(question, request.Catalogue) is var (widget, add) && widget is not null
            && request.Tools.Any(t => t.Name == "open_page") && request.Tools.Any(t => t.Name == "highlight_widget") && request.Tools.Any(t => t.Name == "propose_widget"))
        {
            string? page = add ? null : FakeAssistantScripts.PageOf(widget, request.Catalogue);
            string said = FakeAssistantScripts.Locating(widget.Label, russian);

            yield return new TextDelta(said);

            if (page is null)
            {
                yield return new ToolCall($"fake-{Guid.NewGuid():N}", "propose_widget", new JsonObject { ["type"] = widget.Id });
            }
            else
            {
                yield return new ToolCall($"fake-{Guid.NewGuid():N}", "open_page", new JsonObject { ["page"] = page });
                // No page: the widget is on the page open_page is taking the console to, as a model that trusts the
                // navigation would ask, so the client has to wait for it.
                yield return new ToolCall($"fake-{Guid.NewGuid():N}", "highlight_widget", new JsonObject { ["widget"] = widget.Id });
            }

            yield return new UsageReport(new Usage(input, said.Length / 4, 0));
            yield return new RoundEnd(ProviderStop.Done);
            yield break;
        }

        if (FakeAssistantScripts.AsksStatus(question) && request.Tools.Any(t => t.Name == StackStatus))
        {
            string reading = FakeAssistantScripts.Reading(russian);

            yield return new TextDelta(reading);
            yield return new ToolCall($"fake-{Guid.NewGuid():N}", StackStatus, new JsonObject());
            yield return new UsageReport(new Usage(input, reading.Length / 4, 0));
            yield return new RoundEnd(ProviderStop.Done);
            yield break;
        }

        if (FakeAssistantScripts.ScreenNamedIn(question) is { } screen && request.Tools.Any(t => t.Name == "open_screen"))
        {
            string opening = FakeAssistantScripts.Opening(Label(request, screen), russian);

            yield return new TextDelta(opening);
            yield return new ToolCall($"fake-{Guid.NewGuid():N}", "open_screen", new JsonObject { ["path"] = screen });
            yield return new UsageReport(new Usage(input, opening.Length / 4, 0));
            yield return new RoundEnd(ProviderStop.Done);
            yield break;
        }

        string answer = FakeAssistantScripts.WhatICanDo(request.Catalogue.Select(c => c.Label), russian);

        yield return new TextDelta(answer);
        yield return new UsageReport(new Usage(input, answer.Length / 4, 0));
        yield return new RoundEnd(ProviderStop.Done);
    }

    private static string Label(TurnRequest request, string? screen) =>
        request.Catalogue.FirstOrDefault(c => c.Id == screen)?.Label ?? screen ?? "?";

    private static string OpenReply(TurnRequest request, ToolCall call, ToolResult result, bool russian)
    {
        string label = Label(request, call.Input["path"]?.GetValue<string>());

        return result.Ok ? FakeAssistantScripts.Opened(label, russian) : FakeAssistantScripts.NotOpened(label, result.Content, russian);
    }

    // The round's results, read together: a widget found on its page, an offer made, or the first thing that went wrong.
    private static string WidgetReply(TurnRequest request, bool russian)
    {
        AssistantMessage calls = request.History.OfType<AssistantMessage>().Last();
        ToolResultsMessage results = request.History.OfType<ToolResultsMessage>().Last();

        if (results.Results.FirstOrDefault(r => !r.Ok) is { } failed)
        {
            return FakeAssistantScripts.WidgetNotShown(failed.Content, russian);
        }

        ToolCall last = calls.Calls[^1];
        string? id = (last.Input["widget"] ?? last.Input["type"])?.GetValue<string>();
        string label = request.Catalogue.FirstOrDefault(c => c.Id == id)?.Label ?? id ?? "?";

        string? page = calls.Calls.FirstOrDefault(c => c.Name == "open_page")?.Input["page"]?.GetValue<string>();

        return last.Name == "highlight_widget"
            ? FakeAssistantScripts.WidgetFound(label, page ?? "?", russian)
            : FakeAssistantScripts.WidgetOffered(label, russian);
    }

    // Read from the result as the model would read it: the status the platform answered, and for a 200 the body's fields.
    private static string PlatformReply(string tool, ToolResult result, bool russian)
    {
        JsonObject? read;

        try
        {
            read = result.Ok ? JsonNode.Parse(result.Content) as JsonObject : null;
        }
        catch (JsonException)
        {
            read = null;
        }

        if (read?["status"] is not JsonValue statusValue || !statusValue.TryGetValue(out int status))
        {
            return FakeAssistantScripts.PlatformNotRead(result.Content, russian);
        }

        try
        {
            JsonNode? body = status == 200 && read["body"]?.GetValue<string>() is { } text ? JsonNode.Parse(text) : null;

            return (tool, body) switch
            {
                ("get_stock", { } b) => FakeAssistantScripts.StockRead(b["available"]?.GetValue<long>() ?? 0, b["reserved"]?.GetValue<long>() ?? 0, russian),
                ("get_payment", { } b) => FakeAssistantScripts.PaymentRead(b["intent"]?["status"]?.GetValue<string>() ?? "?", russian),
                _ => FakeAssistantScripts.PlatformAnswered(status, russian),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // A body that is not the JSON the script expects is still the platform's answer: the status is what is said.
            return FakeAssistantScripts.PlatformAnswered(status, russian);
        }
    }

    // Read from the result as the model would read it: the stack view's backend services, by state.
    private static string StackReply(ToolResult result, bool russian)
    {
        if (!result.Ok || JsonNode.Parse(result.Content)?["backend"]?["services"] is not JsonArray services)
        {
            return FakeAssistantScripts.NotRead(result.Content, russian);
        }

        int running = services.Count(s => s?["state"]?.GetValue<string>() == "running");

        return FakeAssistantScripts.StackRead(services.Count, running, russian);
    }
}
