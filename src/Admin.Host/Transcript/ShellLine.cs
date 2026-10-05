using System.Text.RegularExpressions;
using Admin.Host.Fakes;
using Admin.Host.Jobs;

namespace Admin.Host.Transcript;

/// <summary>
/// A command or a request as a person would type it in bash. Each word is single-quoted unless it is made only of
/// characters no shell treats specially, and every line leaves through <see cref="FixtureScrubber.Scrub"/>.
/// </summary>
public static partial class ShellLine
{
    /// <summary>
    /// The argv as given, never joined into a shell string by the host (<see cref="ProcessSpec"/>). Each word is
    /// scrubbed before it is quoted, so a placeholder is quoted rather than read as a redirection, and the line once
    /// more after, for a secret that spans two words.
    /// </summary>
    public static string Command(ProcessSpec spec) =>
        FixtureScrubber.Scrub(string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments).Select(Word)));

    /// <summary>
    /// The curl for the message the proxy built, headers as it sends them. The token it attached and any
    /// Authorization a caller pasted keep their scheme and lose their value, and a Cookie keeps its names and loses
    /// its values: the credential headers the API screen's history drops (<c>CREDENTIAL_HEADERS</c> in
    /// request-builder.ts), which the scrubber's patterns cannot recognise in an opaque value. Each word is scrubbed
    /// before it is quoted, as <see cref="Command"/> does, so no placeholder breaks the quoting. A GET or HEAD
    /// carrying a body names its method, since curl would otherwise send the body as a POST. Content-Length is
    /// curl's to set.
    /// </summary>
    public static string Curl(HttpRequestMessage message, string? body)
    {
        bool hasBody = body is { Length: > 0 };
        List<string> words = ["curl", "-i"];

        words.AddRange(message.Method.Method switch
        {
            "GET" when !hasBody => [],
            "HEAD" when !hasBody => ["--head"],
            string method => ["-X", method],
        });
        words.Add(Word(message.RequestUri!.AbsoluteUri));

        IEnumerable<KeyValuePair<string, IEnumerable<string>>> content = message.Content is null ? [] : message.Content.Headers;

        foreach ((string name, IEnumerable<string> values) in message.Headers.Concat(content))
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = string.Join(", ", values);
            words.Add("-H");
            words.Add(Word($"{name}: {Elided(name, value)}"));
        }

        if (hasBody)
        {
            words.Add("--data-raw");
            words.Add(Word(body!));
        }

        return FixtureScrubber.Scrub(string.Join(' ', words));
    }

    /// <summary>POSIX single quotes, with an embedded quote closed, escaped and reopened.</summary>
    public static string Quote(string word) =>
        word.Length > 0 && Plain().IsMatch(word) ? word : $"'{word.Replace("'", @"'\''", StringComparison.Ordinal)}'";

    private static string Word(string word) => Quote(FixtureScrubber.Scrub(word));

    private static string Elided(string name, string value)
    {
        if (name.EndsWith("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return value.Split(' ', 2) is [string scheme, _] ? $"{scheme} {FixtureScrubber.Scrubbed}" : FixtureScrubber.Scrubbed;
        }

        if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join("; ", value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => $"{pair.Split('=', 2)[0]}={FixtureScrubber.Scrubbed}"));
        }

        return value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_@%+=:,./-]+$")]
    private static partial Regex Plain();
}
