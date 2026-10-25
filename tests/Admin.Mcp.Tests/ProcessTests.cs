using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;

namespace Admin.Mcp.Tests;

/// <summary>The process itself, as an agent launches it: standard output is the protocol and nothing else.</summary>
public sealed class ProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Process Start(params string[] args)
    {
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(typeof(McpServer).Assembly.Location);

        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info)!;
    }

    [Fact]
    public async Task It_speaks_the_protocol_on_standard_output_and_ends_when_its_input_does()
    {
        // Nothing listens on the discard port, so a tool call fails; initialize and ping need no console.
        using Process process = Start("--url", "http://localhost:9");

        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""".AsMemory(), Ct);
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""".AsMemory(), Ct);
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""".AsMemory(), Ct);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(2);
        JsonNode.Parse(lines[0])!["result"]!["serverInfo"]!["name"]!.GetValue<string>().ShouldBe("blueprint-admin");
        JsonNode.Parse(lines[1])!["id"]!.GetValue<int>().ShouldBe(2);
        process.ExitCode.ShouldBe(0);
    }

    [Theory]
    [InlineData("--url", "http://example.com:5100")]
    [InlineData("--url", "ftp://localhost")]
    public async Task An_address_it_must_not_reach_ends_it_with_2_a_message_on_standard_error_and_nothing_on_standard_output(string flag, string value)
    {
        using Process process = Start(flag, value);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync(Ct);
        string error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        process.ExitCode.ShouldBe(2);
        output.ShouldBeEmpty();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}
