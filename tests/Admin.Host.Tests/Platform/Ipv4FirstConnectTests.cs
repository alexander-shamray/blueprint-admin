using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Admin.Host.Config;
using Admin.Host.Platform;
using Admin.Host.Stack;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Platform;

public sealed class Ipv4FirstConnectTests
{
    // Answers 200 to anything, on 127.0.0.1 only: the shape Compose publishes its ports in.
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public LoopbackServer()
        {
            listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(() => AnswerAsync(client));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private static async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void Orders_every_ipv4_address_before_any_ipv6_one_and_keeps_each_family_in_resolver_order()
    {
        IPAddress[] resolved = [IPAddress.IPv6Loopback, IPAddress.Parse("10.0.0.2"), IPAddress.Parse("fe80::1"), IPAddress.Loopback];

        Ipv4FirstConnect.Order(resolved).ShouldBe([IPAddress.Parse("10.0.0.2"), IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("fe80::1")]);
    }

    [Fact]
    public async Task A_name_that_resolves_to_ipv6_first_still_reaches_a_server_listening_on_127_0_0_1_only()
    {
        using LoopbackServer server = new();
        using HttpClient http = new(Ipv4FirstConnect.Handler());

        using HttpResponseMessage response = await http.GetAsync($"http://localhost:{server.Port}/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_probe_reports_a_loopback_only_server_up_well_inside_its_deadline()
    {
        using LoopbackServer server = new();
        string url = $"http://localhost:{server.Port}";
        AdminOptions options = new()
        {
            GatewayUrl = url,
            CatalogUrl = url,
            OrderingUrl = url,
            BffUrl = url,
            InventoryUrl = url,
            PaymentsUrl = url,
            PrivacyUrl = url,
            ObjectStoreUrl = url,
            ImageEdgeUrl = url,
            KeycloakUrl = url,
            GrafanaUrl = url,
            ClientUrl = url,
        };
        using HttpClient http = new(Ipv4FirstConnect.Handler());
        PlatformProbe probe = new(http, Options.Create(options));
        Stopwatch elapsed = Stopwatch.StartNew();

        IReadOnlyList<Reachability> result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        // The probe's deadline is two seconds and an IPv6 fallback on Windows costs about that (#371), so
        // without the fix the targets land at the deadline and read down: the Up assertion is what fails
        // there, and the clock only keeps "up" from meaning "up at the deadline". The bound leaves room for
        // twelve first-time connects, JIT and a loaded runner. On a runner that refuses ::1 at once neither
        // assertion can tell the two orders apart, which is what the ordering test above is for.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
        result.ShouldAllBe(r => r.Up);
    }

    [Fact]
    public async Task A_port_nothing_listens_on_is_a_refusal_not_a_hang()
    {
        int port;
        using (TcpListener free = new(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }

        using HttpClient http = new(Ipv4FirstConnect.Handler()) { Timeout = TimeSpan.FromSeconds(10) };

        // `localhost` resolves to both families, so every address is tried and the last failure is what surfaces.
        // The port was released a moment ago; if another process took it the request would be answered, which is
        // a risk of a few microseconds on an ephemeral port that this test accepts.
        await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{port}/", TestContext.Current.CancellationToken));
    }
}
