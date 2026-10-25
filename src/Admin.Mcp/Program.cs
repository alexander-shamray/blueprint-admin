using System.Text;

namespace Admin.Mcp;

/// <summary>
/// The MCP route of AI assistant design §12. An agent launches this process and speaks MCP to it over stdin and stdout;
/// it speaks HTTP to the console host's /api on loopback and opens no listener, so spec §8 holds. Stdout carries the
/// protocol and nothing else: a diagnostic goes to stderr. An explicit entry point, not top-level statements, so the
/// host's tests, which see this assembly's internals, meet one <c>Program</c> and not two.
/// </summary>
internal static class McpProcess
{
    private const string HostOption = "--host";

    private static async Task<int> Main(string[] args)
    {
        string? host = Array.IndexOf(args, HostOption) is int at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

        if (host is null || !Uri.TryCreate(host, UriKind.Absolute, out Uri? baseAddress) || baseAddress.Scheme != Uri.UriSchemeHttp || !baseAddress.IsLoopback)
        {
            await Console.Error.WriteLineAsync($"Admin.Mcp needs {HostOption} <url>, the console host's loopback address, such as http://127.0.0.1:<Admin:Port>. The console binds loopback only (spec §8), and this process reaches nothing else.");

            return 2;
        }

        using HttpClient http = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        using StreamReader input = new(stdin, new UTF8Encoding(false));
        using StreamWriter output = new(stdout, new UTF8Encoding(false)) { NewLine = "\n" };

        await new McpServer(new HttpAdminHost(http)).RunAsync(input, output, CancellationToken.None);

        return 0;
    }
}
