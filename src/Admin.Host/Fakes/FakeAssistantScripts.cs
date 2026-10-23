namespace Admin.Host.Fakes;

/// <summary>
/// What the fake assistant answers (AI assistant design §8.3, the script format is the builder's, §16): a message naming a
/// screen, in English or in Russian, is answered by opening it with <c>open_screen</c>; any other message by what the
/// assistant can do. The words are matched case-insensitively anywhere in the caller's text, so "where are the logs?" and
/// "где логи?" both open Logs. The screen's name in the reply is its catalogue label, as the client sent it.
/// </summary>
public static class FakeAssistantScripts
{
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
