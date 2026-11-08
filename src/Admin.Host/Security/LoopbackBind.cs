using System.Net;

namespace Admin.Host.Security;

/// <summary>
/// The loopback-only bind (spec §8), held against configuration as well as code. Program.cs listens on loopback in
/// code, but the generic web host still reads <c>urls</c>, <c>http_ports</c>, <c>https_ports</c> and
/// <c>preferHostingUrls</c> from the app's configuration, and with <c>preferHostingUrls</c> true Kestrel binds the
/// hosting urls and drops the code-backed listener. The documented launch makes the caller's directory the content
/// root, so a root <c>appsettings.json</c> could set both (#620). <see cref="PinnedHostingKeys"/> is added after every
/// other source. The pin that does the work is <c>preferHostingUrls=false</c>: with it Kestrel keeps the code's
/// listener and ignores any hosting url. The empty <c>urls</c> and ports are best effort only, since the web host falls
/// back to the <c>urls</c> it read while the builder was created, before this source existed. <see cref="NonLoopback"/>
/// is the check made once the server has bound, in case anything still widened it.
/// </summary>
public static class LoopbackBind
{
    /// <summary>The hosting keys that could widen the bind; <c>preferHostingUrls</c> is the one the bind rests on.</summary>
    public static readonly IReadOnlyDictionary<string, string?> PinnedHostingKeys = new Dictionary<string, string?>
    {
        [WebHostDefaults.ServerUrlsKey] = "",
        [WebHostDefaults.HttpPortsKey] = "",
        [WebHostDefaults.HttpsPortsKey] = "",
        [WebHostDefaults.PreferHostingUrlsKey] = "false",
    };

    /// <summary>The bound addresses whose host is not a loopback one; empty is the only answer the console starts with.</summary>
    public static IReadOnlyList<string> NonLoopback(IEnumerable<string> addresses) =>
        [.. addresses.Where(address => !IsLoopback(address))];

    private static bool IsLoopback(string address)
    {
        string host = BindingAddress.Parse(address).Host.Trim('[', ']');

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
    }
}
