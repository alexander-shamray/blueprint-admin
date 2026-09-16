using System.Globalization;

namespace Admin.Host.Trace;

/// <summary>
/// The <c>window</c> query-string value in the <c>15m</c>/<c>2h</c>/<c>90s</c> form spec §5.10 uses.
/// The range is bounded because the window becomes Loki's <c>start</c>: an unbounded range over a
/// workstation's retention is a slow query, not a useful one.
/// </summary>
public static class TraceWindow
{
    public const string Default = "15m";

    public static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan Max = TimeSpan.FromHours(24);

    private const string Accepted = "A window is a whole number and a unit — 90s, 15m or 2h — between 1m and 24h.";

    /// <summary>The largest value any unit can carry and still be inside <see cref="Max"/>, in that unit's own terms.</summary>
    private const long MaxUnits = 24 * 60 * 60;

    public static bool TryParse(string? text, out TimeSpan window, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            window = Parse(Default);
            error = null;

            return true;
        }

        string trimmed = text.Trim();
        long seconds = SecondsIn(trimmed);

        if (seconds < 0)
        {
            window = default;
            error = $"'{trimmed}' is not a window. {Accepted}";

            return false;
        }

        window = TimeSpan.FromSeconds(seconds);

        if (window < Min || window > Max)
        {
            window = default;
            error = $"'{trimmed}' is outside the accepted range. {Accepted}";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>The canonical text for a window, so a parsed value can be echoed back and put in an Explore link.</summary>
    public static string Format(TimeSpan window) => window switch
    {
        { TotalHours: >= 1 } and { Minutes: 0, Seconds: 0, Milliseconds: 0 } => $"{(long)window.TotalHours}h",
        { Seconds: 0, Milliseconds: 0 } => $"{(long)window.TotalMinutes}m",
        _ => $"{(long)window.TotalSeconds}s",
    };

    /// <summary>The window as seconds, or -1 when the text is not a window at all.</summary>
    private static long SecondsIn(string text)
    {
        if (text.Length < 2)
        {
            return -1;
        }

        // NumberStyles.None refuses a sign, a decimal point and surrounding whitespace, so "-5m" and
        // "1.5h" are rejected here rather than becoming a surprising window.
        if (!long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value > MaxUnits)
        {
            return -1;
        }

        return text[^1] switch
        {
            's' => value,
            'm' => value * 60,
            'h' => value * 60 * 60,
            _ => -1,
        };
    }

    private static TimeSpan Parse(string text) => TimeSpan.FromSeconds(SecondsIn(text));
}
