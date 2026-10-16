using System.Globalization;
using System.Text.RegularExpressions;
using Admin.Host.Telemetry;
using Admin.Host.Trace;

namespace Admin.Host.Scenario;

/// <summary>One "flagged for review" line Ordering logged for an order: when, why, and the line as Loki holds it.</summary>
public sealed record ReviewFlag(DateTimeOffset At, string Reason, string Line);

/// <summary>What Ordering has logged of an order being flagged for review, or why Loki could not say.</summary>
public sealed record ReviewFlagView(string OrderId, string Window, bool Reachable, string? Error, IReadOnlyList<ReviewFlag> Flags);

/// <summary>
/// Whether Ordering flagged an order for review, read from Loki as run-locally.md says to by hand ("The same flag is also
/// a log line from <c>Ordering.Api</c>"; blueprint-backend <c>docs/runbooks/order-review.md</c> owns what a review is). The
/// line is a trace and not the queue: it repeats on a redelivery and ages out with the log's retention, so a flag here says
/// Ordering raised it, never that the order still awaits review. That is why the Scenario asserts the line and not the
/// <c>ordering.OrderReviews</c> row, which only a SQL prompt reads.
/// </summary>
public sealed partial class ReviewFlagService(GrafanaClient grafana, TimeProvider time)
{
    /// <summary>A flagged order has one line, a redelivery a few more; this is far above what one order logs.</summary>
    public const int MaxLines = 100;

    /// <summary>The window when the caller names none: a despatch wait with the short-timeouts override is a minute.</summary>
    public const string DefaultWindow = "30m";

    /// <summary>
    /// run-locally.md's query for the flag, with <c>&lt;order id&gt;</c> where this puts the order id. The caller has already
    /// parsed the id as a Guid and passes it in its lowercase <c>D</c> form, whose alphabet holds no quote, brace, backslash
    /// or pipe: that is what makes this interpolation safe. A drift test holds the text to the document.
    /// </summary>
    public static string LogQl(string orderId) =>
        $$""""
        {service_name="Ordering.Api"} |= "flagged for review" |= "{{orderId}}"
        """";

    public async Task<ReviewFlagView> ReadAsync(Guid orderId, TimeSpan window, CancellationToken cancellationToken)
    {
        string id = orderId.ToString("D", CultureInfo.InvariantCulture);
        string windowText = TraceWindow.Format(window);
        DateTimeOffset now = time.GetUtcNow();

        LokiResult loki = await grafana.QueryAsync(LogQl(id), now - window, now, MaxLines, cancellationToken);

        if (!loki.Reachable)
        {
            return new ReviewFlagView(id, windowText, false, loki.Error, []);
        }

        // Loki answers stream by stream, not in time order, so the flags are ordered here.
        ReviewFlag[] flags =
        [
            .. loki.Lines
                .Select(line => (line, match: Flagged().Match(line.Message)))
                .Where(x => x.match.Success && string.Equals(x.match.Groups["id"].Value, id, StringComparison.OrdinalIgnoreCase))
                .Select(x => new ReviewFlag(x.line.At, x.match.Groups["reason"].Value, x.line.Message))
                .OrderBy(flag => flag.At),
        ];

        return new ReviewFlagView(id, windowText, true, null, flags);
    }

    /// <summary>The line's own text: <c>Order {OrderId} flagged for review: {Reason}.</c> (blueprint-backend <c>FlagOrderForReviewHandler</c>).</summary>
    [GeneratedRegex(@"Order (?<id>[0-9a-fA-F-]{36}) flagged for review: (?<reason>[a-z_]+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex Flagged();
}
