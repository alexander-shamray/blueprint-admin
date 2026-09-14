using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Shouldly;

namespace Admin.Host.Tests.Security;

/// <summary>
/// The loopback-only invariant against real Kestrel: the in-memory factory has no
/// sockets, so this starts the built host as a child process.
/// </summary>
public sealed class LoopbackBindingTests
{
    [Fact]
    public async Task Configuration_cannot_add_a_listener_beside_the_loopback_one()
    {
        int adminPort = FreePort();
        int widePort = FreePort();
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Admin.Host.dll"));
        start.Environment.Remove("ASPNETCORE_URLS");
        start.Environment["Admin__FakePlatform"] = "true";
        start.Environment["Admin__Port"] = adminPort.ToString(CultureInfo.InvariantCulture);
        start.Environment["Kestrel__Endpoints__Wide__Url"] = $"http://0.0.0.0:{widePort}";

        using Process host = Process.Start(start).ShouldNotBeNull();
        host.OutputDataReceived += (_, _) => { };
        host.ErrorDataReceived += (_, _) => { };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();

        try
        {
            (await WaitForListenerAsync(adminPort, host)).ShouldBeTrue("the host never listened on its loopback port");

            // A listener on 0.0.0.0 accepts loopback connections too.
            (await CanConnectAsync(widePort)).ShouldBeFalse($"a configured endpoint on 0.0.0.0:{widePort} was bound");
        }
        finally
        {
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<bool> WaitForListenerAsync(int port, Process host)
    {
        for (int attempt = 0; attempt < 150 && !host.HasExited; attempt++)
        {
            if (await CanConnectAsync(port))
            {
                return true;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using TcpClient client = new();

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
