using System.Text.RegularExpressions;
using Admin.Assistant.Protocol;

namespace Admin.Host.Fakes;

/// <summary>
/// What the fake assistant answers (AI assistant design §8.3, the script format is the builder's, §16). A message asking
/// how the stack is doing is answered from the host's <c>stack_status</c> read. A message naming a screen, in English or
/// in Russian, is answered by opening it with <c>open_screen</c>. Any other message is answered by what the assistant can
/// do. The words are matched case-insensitively anywhere in the caller's text, so "where are the logs?" and "где логи?"
/// both open Logs, and "is the stack running?" reads the stack. The screen's name in a reply is its catalogue label, as
/// the client sent it.
/// </summary>
public static partial class FakeAssistantScripts
{
    /// <summary>The words that ask how the platform is doing, which <c>stack_status</c> answers.</summary>
    public static readonly string[] StatusWords = ["status", "running", "healthy", "состояни", "работает", "запущен"];

    /// <summary>Whether a message asks how the platform is doing, before which screen it names.</summary>
    public static bool AsksStatus(string text) => StatusWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The platform reads the script knows (§6.5): a message that names an id and asks for the product's stock or the
    /// order's payment. The tool is the one the host derives from the operation of that name.
    /// </summary>
    public static readonly IReadOnlyList<(string Tool, string[] Words)> PlatformReads =
    [
        ("get_stock", ["stock", "остат", "наличи", "склад"]),
        ("get_payment", ["payment", "оплат", "платеж", "платёж"]),
    ];

    /// <summary>The read a message asks for and the id it names, or null when it names no id or asks for none.</summary>
    public static (string Tool, string Id)? PlatformAsk(string text)
    {
        Match id = IdShape().Match(text);

        if (!id.Success)
        {
            return null;
        }

        string tool = PlatformReads.FirstOrDefault(r => r.Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))).Tool;

        return tool is null ? null : (tool, id.Value);
    }

    /// <summary>Words that make a message about a widget (§6.4); without one the widget's name alone is not enough, since "logs" names a screen too.</summary>
    public static readonly string[] WidgetWords = ["widget", "виджет"];

    /// <summary>Words that ask for a widget to be added rather than found.</summary>
    public static readonly string[] AddWords = ["add", "propose", "добав", "предлож"];

    /// <summary>The catalogue's id prefix for a widget type (the SPA's <c>WIDGET_ID_PREFIX</c>).</summary>
    public const string WidgetIdPrefix = "widget:";

    /// <summary>
    /// The widget a message names, by the catalogue's widget entries (the longest label that appears in it wins), and
    /// whether it asks to add one. Null unless the message says "widget" and names one.
    /// </summary>
    public static (CatalogueEntry Widget, bool Add)? WidgetAsk(string text, IReadOnlyList<CatalogueEntry> catalogue)
    {
        if (!WidgetWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        CatalogueEntry? named = catalogue
            .Where(e => e.Id.StartsWith(WidgetIdPrefix, StringComparison.Ordinal) && text.Contains(e.Label, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Label.Length)
            .FirstOrDefault();

        return named is null ? null : (named, AddWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The label of the screen whose path starts a widget's <c>where</c>, or null for a widget no built-in page holds.</summary>
    public static string? PageOf(CatalogueEntry widget, IReadOnlyList<CatalogueEntry> catalogue)
    {
        string first = widget.Where.Split(',')[0].Trim();

        return first.StartsWith('/') ? catalogue.FirstOrDefault(e => e.Id == first[1..])?.Label : null;
    }

    public static string Locating(string label, bool russian) => russian ? $"Ищу виджет «{label}». " : $"Looking for the {label} widget. ";

    public static string WidgetFound(string label, string page, bool russian) =>
        russian ? $"Виджет «{label}» на странице «{page}»; я его выделил." : $"The {label} widget is on the {page} page; I outlined it.";

    public static string WidgetOffered(string label, bool russian) =>
        russian ? $"Нажмите кнопку под ответом, чтобы добавить виджет «{label}»." : $"Press the button under this reply to add the {label} widget.";

    public static string WidgetNotShown(string why, bool russian) =>
        russian ? $"Не получилось показать виджет: {why}" : $"The widget could not be shown: {why}";

    public static string Fetching(bool russian) => russian ? "Запрашиваю платформу. " : "Asking the platform. ";

    public static string StockRead(long available, long reserved, bool russian) =>
        russian ? $"В наличии: {available}, в резерве: {reserved}." : $"{available} in stock, {reserved} reserved.";

    public static string PaymentRead(string status, bool russian) =>
        russian ? $"Статус оплаты: {status}." : $"The payment is {status}.";

    public static string PlatformAnswered(int status, bool russian) =>
        russian ? $"Платформа ответила {status}." : $"The platform answered {status}.";

    public static string PlatformNotRead(string why, bool russian) =>
        russian ? $"Не удалось прочитать данные платформы: {why}" : $"The platform could not be read: {why}";

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex IdShape();

    public static string Reading(bool russian) => russian ? "Смотрю состояние стека. " : "Reading the stack. ";

    /// <summary>The stack read's answer, by how many backend services it lists and how many of them run.</summary>
    public static string StackRead(int services, int running, bool russian) =>
        russian
            ? $"Сервисов в бэкенде: {services}, запущено: {running}."
            : $"The backend has {services} services, {running} of them running.";

    public static string NotRead(string why, bool russian) =>
        russian ? $"Не удалось прочитать состояние стека: {why}" : $"The stack could not be read: {why}";

    /// <summary>Each screen's path in <c>SCREENS</c> (the SPA's <c>screens.ts</c>) and the words that name it.</summary>
    public static readonly IReadOnlyList<(string Screen, string[] Words)> Screens =
    [
        ("stack", ["stack", "стек", "compose"]),
        ("logs", ["logs", "log lines", "логи", "лог", "журнал"]),
        ("broker", ["broker", "queue", "rabbit", "брокер", "очеред"]),
        ("map", ["map", "карт"]),
        ("erasure", ["erasure", "удален", "стира"]),
        ("mail", ["mail", "почт", "письм"]),
        ("requests", ["api", "request", "запрос"]),
        ("trace", ["trace", "трасс", "трейс"]),
        ("scenario", ["scenario", "сценари"]),
        ("runbooks", ["runbook", "ранбук", "регламент"]),
        ("transcript", ["transcript", "транскрипт", "журнал команд"]),
    ];

    /// <summary>The screen a message names, the first in <see cref="Screens"/>' order, or null.</summary>
    public static string? ScreenNamedIn(string text) =>
        Screens.FirstOrDefault(s => s.Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))).Screen;

    /// <summary>Whether to answer in Russian: the caller wrote in Cyrillic.</summary>
    public static bool IsRussian(string text) => text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    public static string Opening(string label, bool russian) => russian ? $"Открываю «{label}». " : $"Opening {label}. ";

    public static string Opened(string label, bool russian) =>
        russian ? $"Экран «{label}» открыт." : $"{label} is open.";

    public static string NotOpened(string label, string why, bool russian) =>
        russian ? $"Не получилось открыть «{label}»: {why}" : $"{label} did not open: {why}";

    public static string WhatICanDo(IEnumerable<string> labels, bool russian) =>
        russian
            ? $"Я объясняю экраны консоли и открываю нужный. Экраны: {string.Join(", ", labels)}."
            : $"I explain the console's screens and open the one you need. The screens are: {string.Join(", ", labels)}.";
}
