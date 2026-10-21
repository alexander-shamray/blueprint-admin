using System.Net;
using System.Net.Sockets;

namespace Admin.Host.Platform;

/// <summary>
/// Connects to a name by its IPv4 addresses first (#371). The workstation publishes every Compose port on
/// <c>127.0.0.1</c> only, and <c>localhost</c> resolves to <c>::1</c> first: on Windows a connect to a refused
/// loopback port is not answered with a fast reset, so the attempt takes about two seconds before the handler falls
/// back, which is the whole of <c>PlatformProbe</c>'s per-target deadline and a fixed cost on every proxied call.
/// The URL and the <c>Host</c> header stay <c>localhost</c> (Keycloak's issuer is what the backend validates); only
/// the order in which the resolved addresses are tried changes.
/// <para>
/// Addresses are tried one after another with no timeout of their own, as the stock serial connect does: a URL
/// that points at a black-holed non-loopback IPv4 address holds the connect until the caller's deadline and the IPv6
/// fallback is never reached. Every default target is loopback, where a connect is answered or refused.
/// </para>
/// </summary>
public static class Ipv4FirstConnect
{
    /// <summary>
    /// A handler for the host's clients. <paramref name="configure"/> sets the per-client options (redirects,
    /// cookies) that the shared handler does not.
    /// </summary>
    public static SocketsHttpHandler Handler(Action<SocketsHttpHandler>? configure = null)
    {
        SocketsHttpHandler handler = new() { ConnectCallback = ConnectAsync };
        configure?.Invoke(handler);

        return handler;
    }

    /// <summary>The addresses with every IPv4 one first; each family keeps the order the resolver gave.</summary>
    public static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> addresses) =>
        [.. addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] resolved = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? last = null;

        foreach (IPAddress address in Order(resolved))
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();

                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
