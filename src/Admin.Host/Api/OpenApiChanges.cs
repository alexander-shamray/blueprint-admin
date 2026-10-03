using System.Text.Json;

namespace Admin.Host.Api;

/// <summary>
/// The facts of an OpenAPI document the API screen reports a change in (spec §5.7): its operations as
/// <c>GET /v1/catalog/products/</c>, its response codes as that plus the code, and its schema fields as
/// <c>ProductSummaryDto.name</c>. A description or an example moving is not news to an operator.
/// </summary>
public sealed record OpenApiShape(IReadOnlySet<string> Operations, IReadOnlySet<string> Statuses, IReadOnlySet<string> Fields)
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    public static OpenApiShape Of(JsonElement document)
    {
        HashSet<string> operations = new(StringComparer.Ordinal);
        HashSet<string> statuses = new(StringComparer.Ordinal);
        HashSet<string> fields = new(StringComparer.Ordinal);

        if (document.TryGetProperty("paths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty operation in path.Value.EnumerateObject().Where(p => Methods.Contains(p.Name)))
                {
                    string name = $"{operation.Name.ToUpperInvariant()} {path.Name}";
                    operations.Add(name);

                    if (operation.Value.ValueKind == JsonValueKind.Object
                        && operation.Value.TryGetProperty("responses", out JsonElement responses)
                        && responses.ValueKind == JsonValueKind.Object)
                    {
                        statuses.UnionWith(responses.EnumerateObject().Select(r => $"{name} {r.Name}"));
                    }
                }
            }
        }

        if (document.TryGetProperty("components", out JsonElement components)
            && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("schemas", out JsonElement schemas)
            && schemas.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty schema in schemas.EnumerateObject())
            {
                if (schema.Value.ValueKind == JsonValueKind.Object
                    && schema.Value.TryGetProperty("properties", out JsonElement properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    fields.UnionWith(properties.EnumerateObject().Select(p => $"{schema.Name}.{p.Name}"));
                }
            }
        }

        return new OpenApiShape(operations, statuses, fields);
    }
}

/// <summary>
/// One service's document against its kept baseline. <see cref="BaselineTaken"/> is the first sight of the
/// service, when there is nothing to compare yet; <see cref="Error"/> is a baseline that could not be read
/// or written, which the screen shows rather than a comparison it could not make.
/// </summary>
public sealed record OpenApiChanges(
    DateTimeOffset? BaselineAt,
    bool BaselineTaken,
    IReadOnlyList<string> OperationsAdded,
    IReadOnlyList<string> OperationsRemoved,
    IReadOnlyList<string> StatusesAdded,
    IReadOnlyList<string> StatusesRemoved,
    IReadOnlyList<string> FieldsAdded,
    IReadOnlyList<string> FieldsRemoved,
    string? Error = null)
{
    public bool Any =>
        OperationsAdded.Count + OperationsRemoved.Count + StatusesAdded.Count + StatusesRemoved.Count + FieldsAdded.Count + FieldsRemoved.Count > 0;

    public static OpenApiChanges Between(OpenApiShape baseline, OpenApiShape current, DateTimeOffset baselineAt) => new(
        baselineAt,
        false,
        Added(baseline.Operations, current.Operations),
        Added(current.Operations, baseline.Operations),
        Added(baseline.Statuses, current.Statuses),
        Added(current.Statuses, baseline.Statuses),
        Added(baseline.Fields, current.Fields),
        Added(current.Fields, baseline.Fields));

    public static OpenApiChanges Taken(DateTimeOffset at) => new(at, true, [], [], [], [], [], []);

    public static OpenApiChanges Failed(string error) => new(null, false, [], [], [], [], [], [], error);

    private static string[] Added(IReadOnlySet<string> before, IReadOnlySet<string> after) =>
        [.. after.Where(a => !before.Contains(a)).Order(StringComparer.Ordinal)];
}
