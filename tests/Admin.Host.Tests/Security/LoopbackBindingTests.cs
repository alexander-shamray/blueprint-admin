using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Admin.Host.Security;
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
        (int adminPort, int widePort) = TwoFreePorts();
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

    [Fact]
    public async Task A_root_appsettings_cannot_prefer_hosting_urls_over_the_loopback_listener()
    {
        // The documented launch makes the caller's directory the content root, so
        // its appsettings.json is read: this one asks for the attack in #620.
        (int adminPort, int widePort) = TwoFreePorts();
        string root = Directory.CreateTempSubdirectory("admin-bind-").FullName;
        await File.WriteAllTextAsync(
            Path.Combine(root, "appsettings.json"),
            $$"""{ "urls": "http://0.0.0.0:{{widePort}}", "preferHostingUrls": true, "http_ports": "{{widePort}}" }""",
            TestContext.Current.CancellationToken);

        try
        {
            await AssertOnlyLoopbackAsync(root, adminPort, widePort, _ => { });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Environment_hosting_urls_cannot_widen_the_bind_either()
    {
        (int adminPort, int widePort) = TwoFreePorts();

        await AssertOnlyLoopbackAsync(AppContext.BaseDirectory, adminPort, widePort, environment =>
        {
            environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{widePort}";
            environment["ASPNETCORE_PREFERHOSTINGURLS"] = "true";
        });
    }

    [Theory]
    [InlineData("http://127.0.0.1:5300", true)]
    [InlineData("http://localhost:5300", true)]
    [InlineData("http://[::1]:5300", true)]
    [InlineData("http://0.0.0.0:5300", false)]
    [InlineData("http://[::]:5300", false)]
    [InlineData("http://*:5300", false)]
    [InlineData("http://+:5300", false)]
    [InlineData("http://192.168.1.10:5300", false)]
    public void Only_a_loopback_address_passes_the_started_check(string address, bool loopback)
    {
        string[] expected = loopback ? [] : [address];

        LoopbackBind.NonLoopback([address]).ShouldBe(expected);
    }

    private static async Task AssertOnlyLoopbackAsync(string workingDirectory, int adminPort, int widePort, Action<IDictionary<string, string?>> configure)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Admin.Host.dll"));
        start.Environment.Remove("ASPNETCORE_URLS");
        start.Environment["Admin__FakePlatform"] = "true";
        start.Environment["Admin__Port"] = adminPort.ToString(CultureInfo.InvariantCulture);
        configure(start.Environment);

        using Process host = Process.Start(start).ShouldNotBeNull();
        host.OutputDataReceived += (_, _) => { };
        host.ErrorDataReceived += (_, _) => { };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();

        try
        {
            (await WaitForListenerAsync(adminPort, host)).ShouldBeTrue("the host never listened on its loopback port");
            (await CanConnectAsync(widePort)).ShouldBeFalse($"a hosting url on 0.0.0.0:{widePort} was bound");
        }
        finally
        {
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    // Both probes stay open until both ports are known, so the OS cannot hand the
    // same port out twice; a shared port would let the loopback listener answer
    // for the wide one.
    private static (int First, int Second) TwoFreePorts()
    {
        using TcpListener first = new(IPAddress.Loopback, 0);
        using TcpListener second = new(IPAddress.Loopback, 0);
        first.Start();
        second.Start();

        return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
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
