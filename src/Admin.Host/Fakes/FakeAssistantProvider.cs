using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Admin.Assistant;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Host.Assistant;

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
            string reply = call.Name == StackStatus ? StackReply(result, russian) : OpenReply(request, call, result, russian);

            yield return new TextDelta(reply);
            yield return new UsageReport(new Usage(input, reply.Length / 4, 0));
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
