using System.Text.Json.Nodes;

namespace Admin.Mcp;

/// <summary>What the MCP route needs of the console host (AI assistant design §12), so the server is tested without one.</summary>
public interface IAdminHost
{
    /// <summary>The host's server tools, each as the host describes it (<c>GET /api/assistant/tools</c>).</summary>
    Task<JsonArray> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>One tool's result (<c>POST /api/assistant/tools/{name}</c>); null when the host has no tool of that name.</summary>
    Task<ToolResult?> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken);

    /// <summary>The console's catalogue as the SPA build wrote it, as JSON text.</summary>
    Task<string> ReadCatalogueAsync(CancellationToken cancellationToken);
}

/// <summary>What a tool call came back with: an error result is one the agent reads.</summary>
public sealed record ToolResult(bool Ok, string Content);

/// <summary>The host could not answer; the message says what to do about it.</summary>
public sealed class HostUnavailableException(string message) : Exception(message);
