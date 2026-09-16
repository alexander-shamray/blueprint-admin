namespace Admin.Host.Api;

/// <summary>
/// The correlation id's header name and the backend's adoption rule. Both live here rather than on
/// <see cref="RequestProxy"/> because two callers need them: the proxy sends the header, and the
/// trace endpoint validates an id before it is interpolated into a LogQL query. One copy, because a
/// second alphabet drifting from the backend's is exactly what the citation below exists to prevent.
/// </summary>
public static class CorrelationId
{
    /// <summary>Owner: blueprint-backend <c>Common.Web.CorrelationIdExtensions.Header</c>.</summary>
    public const string Header = "X-Correlation-Id";

    /// <summary>Owner: <c>Common.Web.CorrelationIdExtensions.MaxSuppliedLength</c>.</summary>
    public const int MaxLength = 128;

    /// <summary>The backend's adoption rule, <c>CorrelationIdExtensions.IsAdoptable</c>: 1-128 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsAdoptable(string id) =>
        id.Length is >= 1 and <= MaxLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
