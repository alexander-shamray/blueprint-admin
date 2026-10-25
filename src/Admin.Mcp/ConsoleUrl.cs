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

        if (!url.IsLoopback)
        {
            throw new ArgumentException($"'{url.Host}' is not a loopback address; the console binds loopback only, and this process reaches nothing else.");
        }

        // The base address must end in a slash for the relative routes to join under it.
        return new Uri(url.AbsoluteUri.EndsWith('/') ? url.AbsoluteUri : url.AbsoluteUri + "/");
    }
}
