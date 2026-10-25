namespace Admin.Mcp;

/// <summary>
/// Where the console is. The port is the console's own (<c>AdminOptions</c> owns it), so it is given here and not guessed;
/// and the address must be loopback, because the console binds loopback only and holds the realm passwords (spec §8): a
/// bridge to anything else is not a thing this process does.
/// </summary>
public static class ConsoleUrl
{
    /// <summary>The environment variable read when <c>--url</c> is not given.</summary>
    public const string Variable = "ADMIN_CONSOLE_URL";

    /// <summary>
    /// The host names the console answers, copied from <c>LoopbackOriginGuard.LoopbackHosts</c> (this project cannot see the
    /// host's): its host filtering refuses any other, so another loopback address would pass here and fail on every call.
    /// A drift test in <c>Admin.Host.Tests</c> holds the two lists equal.
    /// </summary>
    public static readonly IReadOnlyList<string> Hosts = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>
    /// The handler the console is reached through: no redirects and no proxy, so what answers on the loopback port is the
    /// console or it is an error, and a 307 cannot carry a tool call's arguments to another host. The console's own HTTP
    /// clients refuse redirects for the same reason.
    /// </summary>
    public static SocketsHttpHandler Handler() => new() { AllowAutoRedirect = false, UseProxy = false };

    /// <exception cref="ArgumentException">No address, one that is not an absolute http address, or one that is not loopback.</exception>
    public static Uri From(string[] args, string? fromEnvironment)
    {
        int at = Array.IndexOf(args, "--url");
        string? given = at >= 0 && at + 1 < args.Length ? args[at + 1] : fromEnvironment;

        if (string.IsNullOrWhiteSpace(given))
        {
            throw new ArgumentException($"Give the console's address: --url http://localhost:<port> (or set {Variable}).");
        }

        if (!Uri.TryCreate(given, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException($"'{given}' is not an http address.");
        }

        if (!Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{url.Host}' is not one of {string.Join(", ", Hosts)}; the console binds loopback only and answers only those names, and this process reaches nothing else.");
        }

        // The base address must end in a slash for the relative routes to join under it.
        return new Uri(url.AbsoluteUri.EndsWith('/') ? url.AbsoluteUri : url.AbsoluteUri + "/");
    }
}
