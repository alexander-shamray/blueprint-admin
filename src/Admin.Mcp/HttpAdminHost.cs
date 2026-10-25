using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Mcp;

/// <summary>
/// The console host over HTTP on loopback. The shapes are the host's, owned by <c>AssistantEndpoints</c> in
/// <c>src/Admin.Host/Assistant/</c> and the SPA build's catalogue asset: the tools come back as the host describes them
/// and a call as <c>{ ok, content }</c>.
/// </summary>
public sealed class HttpAdminHost(HttpClient http) : IAdminHost
{
    /// <summary>Where the SPA build leaves the catalogue, under the host's <c>wwwroot</c>.</summary>
    public const string CataloguePath = "assistant-catalogue.json";

    // Copied from AssistantEndpoints.NoSuchTool, its owner; McpRouteTests holds the two equal.
    private const string NoSuchTool = "No such tool";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerOptions.Web);

    private const string AssistantOff = "The console's assistant is off, so it offers no tools: start the host with Admin:Assistant:Provider set to 'fake' or 'anthropic'.";

    public async Task<JsonArray> ListToolsAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(() => http.GetAsync("api/assistant/tools", cancellationToken), cancellationToken);

        return response.StatusCode == HttpStatusCode.NotFound
            ? throw new HostUnavailableException(AssistantOff)
            : await Json<JsonArray>(response, cancellationToken) ?? throw new HostUnavailableException("The host's tool list was not a JSON array.");
    }

    public async Task<ToolResult?> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(() => http.PostAsJsonAsync($"api/assistant/tools/{Uri.EscapeDataString(name)}", arguments, Web, cancellationToken), cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The host's own answer for an unknown tool is a problem document; anything else at that route is the off switch's 404.
            JsonObject? problem = await ProblemAsync(response, cancellationToken);

            return problem?["title"]?.GetValue<string>() == NoSuchTool ? null : throw new HostUnavailableException(AssistantOff);
        }

        JsonObject? body = await Json<JsonObject>(response, cancellationToken);

        return body?["ok"] is JsonValue ok && body["content"] is JsonValue content
            ? new ToolResult(ok.GetValue<bool>(), content.GetValue<string>())
            : throw new HostUnavailableException("The host's tool result had no ok and content.");
    }

    public async Task<string> ReadCatalogueAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(() => http.GetAsync(CataloguePath, cancellationToken), cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);

        // A host with an older build answers any other path with its index page, so a status is not enough.
        return response.IsSuccessStatusCode && IsArray(text)
            ? text
            : throw new HostUnavailableException($"The host serves no {CataloguePath}: build the SPA into the host's wwwroot (npm run build in src/Admin.Web).");
    }

    private static bool IsArray(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException e)
        {
            throw new HostUnavailableException($"The console host could not be reached ({e.Message}); start it with dotnet run --project src/Admin.Host.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller going away.
            throw new HostUnavailableException("The console host did not answer in time; a tool that reads the platform may be waiting on a service that is down.");
        }
    }

    private static async Task<T?> Json<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : JsonNode
    {
        if (!response.IsSuccessStatusCode)
        {
            JsonObject? problem = await ProblemAsync(response, cancellationToken);

            throw new HostUnavailableException($"The host answered {(int)response.StatusCode}: {problem?["detail"] ?? problem?["title"] ?? response.ReasonPhrase}");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Web, cancellationToken);
        }
        catch (JsonException)
        {
            throw new HostUnavailableException("The host answered with something that is not the JSON this process reads; is it a console host?");
        }
    }

    private static async Task<JsonObject?> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonObject>(Web, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
