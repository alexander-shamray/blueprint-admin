using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Api;

/// <summary>
/// A placeholder value for an OpenAPI 3.1 schema, because the platform's documents carry no
/// examples. Understands <c>$ref</c> into <c>components.schemas</c>, type arrays (the null branch
/// and the string branch of a string-readable number are skipped), <c>enum</c>, <c>default</c>,
/// <c>oneOf</c>/<c>anyOf</c> (first non-null branch) and <c>allOf</c> (merged).
/// </summary>
public static class ExampleBuilder
{
    private const string RefPrefix = "#/components/schemas/";
    private const int MaxDepth = 10;

    public static JsonNode? Build(JsonElement schema, JsonElement schemas) => Build(schema, schemas, [], 0);

    private static JsonNode? Build(JsonElement schema, JsonElement schemas, HashSet<string> visiting, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > MaxDepth)
        {
            return null;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString() is string r && r.StartsWith(RefPrefix, StringComparison.Ordinal) ? r[RefPrefix.Length..] : "";

            if (name.Length == 0 || schemas.ValueKind != JsonValueKind.Object || !schemas.TryGetProperty(name, out JsonElement target) || !visiting.Add(name))
            {
                return null;
            }

            JsonNode? resolved = Build(target, schemas, visiting, depth + 1);
            visiting.Remove(name);

            return resolved;
        }

        if (schema.TryGetProperty("default", out JsonElement defaultValue))
        {
            return JsonNode.Parse(defaultValue.GetRawText());
        }

        if (schema.TryGetProperty("enum", out JsonElement values) && values.GetArrayLength() > 0)
        {
            return JsonNode.Parse(values[0].GetRawText());
        }

        foreach (string choice in (string[])["oneOf", "anyOf"])
        {
            if (schema.TryGetProperty(choice, out JsonElement branches))
            {
                JsonElement branch = branches.EnumerateArray().FirstOrDefault(b => !(b.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() == "null"));

                return Build(branch, schemas, visiting, depth + 1);
            }
        }

        if (schema.TryGetProperty("allOf", out JsonElement parts))
        {
            JsonObject merged = [];

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (Build(part, schemas, visiting, depth + 1) is JsonObject partObject)
                {
                    foreach ((string key, JsonNode? value) in partObject.ToList())
                    {
                        partObject.Remove(key);
                        merged[key] = value;
                    }
                }
            }

            return merged;
        }

        return TypeOf(schema) switch
        {
            "object" => BuildObject(schema, schemas, visiting, depth),
            "array" => new JsonArray(schema.TryGetProperty("items", out JsonElement items) ? Build(items, schemas, visiting, depth + 1) : null),
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            "string" => JsonValue.Create(StringFor(schema)),
            _ => null,
        };
    }

    private static JsonObject BuildObject(JsonElement schema, JsonElement schemas, HashSet<string> visiting, int depth)
    {
        JsonObject result = [];

        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                result[property.Name] = Build(property.Value, schemas, visiting, depth + 1);
            }
        }

        return result;
    }

    /// <summary>The schema's type with <c>null</c> dropped and a numeric type preferred over the <c>string</c> the generator adds beside it.</summary>
    internal static string? TypeOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return schema.TryGetProperty("properties", out _) ? "object" : null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        string[] types = [.. type.EnumerateArray().Select(t => t.GetString()!).Where(t => t != "null")];

        return types.FirstOrDefault(t => t != "string") ?? types.FirstOrDefault();
    }

    private static string StringFor(JsonElement schema) =>
        (schema.TryGetProperty("format", out JsonElement format) ? format.GetString() : null) switch
        {
            "uuid" => "00000000-0000-0000-0000-000000000000",
            "date-time" => "2026-01-01T00:00:00Z",
            "date" => "2026-01-01",
            "uri" => "https://example.com/",
            _ => "string",
        };
}
