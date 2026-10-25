using System.Text;
using Admin.Mcp;

// The MCP route (AI assistant design §12): `dotnet run --project src/Admin.Mcp -- --url <the console's address>`, launched
// by the agent. Standard output carries the protocol and nothing else; a problem is said on standard error.
Uri url;

try
{
    url = ConsoleUrl.From(args, Environment.GetEnvironmentVariable(ConsoleUrl.Variable));
}
catch (ArgumentException e)
{
    await Console.Error.WriteLineAsync(e.Message);

    return 2;
}

using SocketsHttpHandler handler = ConsoleUrl.Handler();
using HttpClient host = new(handler) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(60) };
using StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
using StreamReader input = new(Console.OpenStandardInput(), new UTF8Encoding(false));

using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try
{
    await new McpServer(host).RunAsync(input, output, stop.Token);
}
catch (OperationCanceledException)
{
    // The agent closed us, or the terminal did.
}

return 0;
