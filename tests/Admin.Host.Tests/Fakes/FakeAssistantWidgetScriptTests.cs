using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Assistant.Tools;
using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>What FakePlatform's assistant does with a message about a widget (AI assistant design §6.4, phase 4).</summary>
public sealed class FakeAssistantWidgetScriptTests
{
    private static readonly CatalogueEntry[] Catalogue =
    [
        new("broker", "Broker", "/broker", "The broker page."),
        new("widget:queues", "Queues", "/broker", "Queues help."),
        new("widget:dead-letters", "Dead-letter workbench", "/broker", "Dead letters help."),
        new("widget:note", "Note", "on no built-in page: add it to a page with Add widget in the layout editor", "Note help."),
    ];

    private static ToolDescriptor Client(string name) =>
        new(name, name, "A tool.", new JsonObject { ["type"] = "object" }, new ToolAnnotations(true, false, true, false), ToolSide.Client);

    private static TurnRequest Asking(string text) =>
        new(
            [Client("open_page"), Client("highlight_widget"), Client("propose_widget")],
            "system",
            Catalogue,
            [new UserMessage(text)],
            1024);

    private static async Task<List<ProviderEvent>> Played(TurnRequest request)
    {
        List<ProviderEvent> events = [];

        await foreach (ProviderEvent e in new FakeAssistantProvider().TurnAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public void A_message_names_a_widget_only_when_it_says_widget_and_the_longest_label_wins()
    {
        FakeAssistantScripts.WidgetAsk("where is the Queues widget?", Catalogue)!.Value.Widget.Id.ShouldBe("widget:queues");
        FakeAssistantScripts.WidgetAsk("где виджет Dead-letter workbench", Catalogue)!.Value.Widget.Id.ShouldBe("widget:dead-letters");
        FakeAssistantScripts.WidgetAsk("where are the queues?", Catalogue).ShouldBeNull();
        FakeAssistantScripts.WidgetAsk("where is the Hologram widget?", Catalogue).ShouldBeNull();
    }

    [Theory]
    [InlineData("where is the Queues widget?", false)]
    [InlineData("please add a Note widget", true)]
    [InlineData("добавь виджет Note", true)]
    public void A_message_asks_to_add_a_widget_by_its_words(string text, bool add)
    {
        FakeAssistantScripts.WidgetAsk(text, Catalogue)!.Value.Add.ShouldBe(add);
    }

    [Fact]
    public void A_widget_is_on_the_page_whose_path_starts_its_place_and_a_widget_on_no_page_has_none()
    {
        FakeAssistantScripts.PageOf(Catalogue[1], Catalogue).ShouldBe("Broker");
        FakeAssistantScripts.PageOf(Catalogue[3], Catalogue).ShouldBeNull();
    }

    [Fact]
    public async Task Asked_where_a_widget_is_it_opens_the_page_and_outlines_the_widget_on_it()
    {
        List<ProviderEvent> events = await Played(Asking("where is the Queues widget?"));

        events.OfType<ToolCall>().Select(c => c.Name).ShouldBe(["open_page", "highlight_widget"]);
        events.OfType<ToolCall>().First().Input["page"]!.GetValue<string>().ShouldBe("Broker");
        events.OfType<ToolCall>().Last().Input["widget"]!.GetValue<string>().ShouldBe("widget:queues");
        // No page on the second call: the client waits for the first to land (workspace-tools.ts).
        events.OfType<ToolCall>().Last().Input.ContainsKey("page").ShouldBeFalse();
    }

    [Fact]
    public async Task Asked_to_add_a_widget_or_for_one_no_page_holds_it_proposes_it()
    {
        List<ProviderEvent> added = await Played(Asking("add a Queues widget"));
        List<ProviderEvent> homeless = await Played(Asking("where is the Note widget?"));

        added.OfType<ToolCall>().Select(c => c.Name).ShouldBe(["propose_widget"]);
        homeless.OfType<ToolCall>().Select(c => c.Name).ShouldBe(["propose_widget"]);
        homeless.OfType<ToolCall>().Single().Input["type"]!.GetValue<string>().ShouldBe("widget:note");
    }

    [Fact]
    public async Task The_replies_say_what_was_found_what_was_offered_or_what_went_wrong()
    {
        ToolCall open = new("c1", "open_page", new JsonObject { ["page"] = "Broker" });
        ToolCall highlight = new("c2", "highlight_widget", new JsonObject { ["widget"] = "widget:queues" });
        ToolCall propose = new("c3", "propose_widget", new JsonObject { ["type"] = "widget:note" });

        string found = Reply(await Played(Following(open, highlight, new ToolResult("c1", true, "ok"), new ToolResult("c2", true, "ok"))));
        string offered = Reply(await Played(Following(propose, null, new ToolResult("c3", true, "ok"), null)));
        string failed = Reply(await Played(Following(open, highlight, new ToolResult("c1", true, "ok"), new ToolResult("c2", false, "not on screen"))));

        found.ShouldBe("The Queues widget is on the Broker page; I outlined it.");
        offered.ShouldBe("Press the button under this reply to add the Note widget.");
        failed.ShouldBe("The widget could not be shown: not on screen");
    }

    private static TurnRequest Following(ToolCall first, ToolCall? second, ToolResult one, ToolResult? two) =>
        Asking("where is the Queues widget?") with
        {
            History =
            [
                new UserMessage("where is the Queues widget?"),
                new AssistantMessage("", second is null ? [first] : [first, second], []),
                new ToolResultsMessage(two is null ? [one] : [one, two]),
            ],
        };

    private static string Reply(List<ProviderEvent> events) => string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
}
