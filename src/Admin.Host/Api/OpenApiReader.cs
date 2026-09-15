using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Api;

/// <summary>
/// Turns a service's OpenAPI document into operations addressed through the gateway, which adds
/// <c>/api</c> in front of the service's own path (Gateway.Api appsettings.json, <c>PathRemovePrefix: /api</c>).
/// </summary>
public static class OpenApiReader
{
    private const string GatewayPrefix = "/api";

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<ApiOperation> Read(string source, JsonElement document, string gatewayUrl)
    {
        List<ApiOperation> operations = [];

        if (!document.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object)
        {
            return operations;
        }

        JsonElement schemas = document.TryGetProperty("components", out JsonElement components) && components.TryGetProperty("schemas", out JsonElement s) ? s : default;

        foreach (JsonProperty path in paths.EnumerateObject())
        {
            foreach (JsonProperty entry in path.Value.EnumerateObject().Where(e => Methods.Contains(e.Name)))
            {
                string method = entry.Name.ToUpperInvariant();
                string? operationId = entry.Value.TryGetProperty("operationId", out JsonElement id) ? id.GetString() : null;
                string name = operationId ?? $"{method} {path.Name}";
                string gatewayPath = GatewayPrefix + path.Name;
                List<(string In, ApiParameter Parameter)> parameters = [.. Parameters(path.Value), .. Parameters(entry.Value)];
                string? example = RunLocallyExamples.For(operationId) ?? Example(entry.Value, schemas);

                operations.Add(new ApiOperation(
                    $"{source}:{name}",
                    source,
                    name,
                    method,
                    gatewayUrl.TrimEnd('/') + gatewayPath,
                    [.. parameters.Where(p => p.In == "path").Select(p => p.Parameter)],
                    [.. parameters.Where(p => p.In == "query").Select(p => p.Parameter)],
                    example,
                    HasCommandId(example),
                    GatewayRoutes.PolicyFor(method, gatewayPath),
                    true));
            }
        }

        return operations;
    }

    private static IEnumerable<(string In, ApiParameter Parameter)> Parameters(JsonElement owner)
    {
        if (!owner.TryGetProperty("parameters", out JsonElement parameters))
        {
            yield break;
        }

        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("name", out JsonElement name) && parameter.TryGetProperty("in", out JsonElement location))
            {
                bool required = parameter.TryGetProperty("required", out JsonElement r) && r.ValueKind == JsonValueKind.True;
                string? type = parameter.TryGetProperty("schema", out JsonElement schema) ? ExampleBuilder.TypeOf(schema) : null;

                yield return (location.GetString()!, new ApiParameter(name.GetString()!, required, type));
            }
        }
    }

    private static string? Example(JsonElement operation, JsonElement schemas)
    {
        if (operation.TryGetProperty("requestBody", out JsonElement body)
            && body.TryGetProperty("content", out JsonElement content)
            && content.TryGetProperty("application/json", out JsonElement json)
            && json.TryGetProperty("schema", out JsonElement schema))
        {
            return ExampleBuilder.Build(schema, schemas)?.ToJsonString(Indented);
        }

        return null;
    }

    private static bool HasCommandId(string? example)
    {
        if (example is null)
        {
            return false;
        }

        return JsonNode.Parse(example) is JsonObject body && body.ContainsKey("commandId");
    }
}
