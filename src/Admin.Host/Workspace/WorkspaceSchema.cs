using System.Text;
using System.Text.Json.Nodes;

namespace Admin.Host.Workspace;

/// <summary>
/// The structural half of the workspace's validation (widget-workspace spec §6.3): the shape of §4.1, unique ids, the
/// grid's bounds, the size of each <c>config</c> and the secret-named keys of §6.4. The host does not know widget types,
/// so a type it has never heard of passes untouched and each type validates its own <c>config</c> in the SPA.
/// </summary>
public static class WorkspaceSchema
{
    /// <summary>The schema this console writes. A change bumps it and adds a step to <see cref="WorkspaceMigrations.All"/>.</summary>
    public const int Current = 2;

    /// <summary>The grid's width in cells (spec §4.1).</summary>
    public const int GridColumns = 24;

    /// <summary>The tallest a widget may be, in grid rows; the spec leaves the bound to the builder.</summary>
    public const int MaxRows = 100;

    /// <summary>The document's total size; <c>PUT</c> and <c>import</c> answer 413 past it.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>One widget's serialised <c>config</c>.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    // A key containing one of these, ignoring case, is refused: a widget cannot start saving a credential by accident.
    private static readonly string[] SecretWords = ["password", "token", "secret", "authorization"];

    /// <summary>What is wrong with <paramref name="root"/> as a version-<paramref name="version"/> workspace, or null.</summary>
    public static string? Validate(JsonNode? root, int version)
    {
        if (root is not JsonObject workspace)
        {
            return "The workspace must be a JSON object.";
        }

        if (!TryInt(workspace["version"], out int found) || found != version)
        {
            return $"version must be {version}.";
        }

        if (!workspace.ContainsKey("activePageId") || (workspace["activePageId"] is { } active && !TryString(active, out _)))
        {
            return "activePageId must be a string or null.";
        }

        if (workspace["pages"] is not JsonArray pages)
        {
            return "pages must be an array.";
        }

        HashSet<string> pageIds = new(StringComparer.Ordinal);
        HashSet<string> widgetIds = new(StringComparer.Ordinal);

        for (int p = 0; p < pages.Count; p++)
        {
            string? problem = ValidatePage(pages[p], $"pages[{p}]", pageIds, widgetIds);

            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? ValidatePage(JsonNode? node, string at, HashSet<string> pageIds, HashSet<string> widgetIds)
    {
        if (node is not JsonObject page)
        {
            return $"{at} must be an object.";
        }

        if (!TryString(page["id"], out string? id) || id.Length == 0)
        {
            return $"{at}.id must be a non-empty string.";
        }

        if (!pageIds.Add(id))
        {
            return $"{at}.id \"{id}\" is used by another page.";
        }

        if (!TryString(page["title"], out _))
        {
            return $"{at}.title must be a string.";
        }

        if (!page.ContainsKey("builtIn") || (page["builtIn"] is { } builtIn && !TryString(builtIn, out _)))
        {
            return $"{at}.builtIn must be a string or null.";
        }

        if (page["hidden"] is not JsonValue hidden || !hidden.TryGetValue(out bool _))
        {
            return $"{at}.hidden must be a boolean.";
        }

        if (!page.ContainsKey("widgets"))
        {
            return $"{at}.widgets must be an array or null.";
        }

        if (page["widgets"] is null)
        {
            return null;
        }

        if (page["widgets"] is not JsonArray widgets)
        {
            return $"{at}.widgets must be an array or null.";
        }

        for (int w = 0; w < widgets.Count; w++)
        {
            string? problem = ValidateWidget(widgets[w], $"{at}.widgets[{w}]", widgetIds);

            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? ValidateWidget(JsonNode? node, string at, HashSet<string> widgetIds)
    {
        if (node is not JsonObject widget)
        {
            return $"{at} must be an object.";
        }

        if (!TryString(widget["id"], out string? id) || id.Length == 0)
        {
            return $"{at}.id must be a non-empty string.";
        }

        if (!widgetIds.Add(id))
        {
            return $"{at}.id \"{id}\" is used by another widget.";
        }

        if (!TryString(widget["type"], out string? type) || type.Length == 0)
        {
            return $"{at}.type must be a non-empty string.";
        }

        foreach (string cell in new[] { "x", "y", "cols", "rows" })
        {
            if (!TryInt(widget[cell], out _))
            {
                return $"{at}.{cell} must be an integer.";
            }
        }

        int x = widget["x"]!.GetValue<int>();
        int y = widget["y"]!.GetValue<int>();
        int cols = widget["cols"]!.GetValue<int>();
        int rows = widget["rows"]!.GetValue<int>();

        if (x < 0 || y < 0 || cols < 1 || x > GridColumns || cols > GridColumns - x)
        {
            return $"{at} lies outside the {GridColumns}-column grid (x {x}, y {y}, cols {cols}).";
        }

        if (rows is < 1 or > MaxRows)
        {
            return $"{at}.rows must be between 1 and {MaxRows}.";
        }

        if (widget["config"] is not JsonObject config)
        {
            return $"{at}.config must be an object.";
        }

        if (Encoding.UTF8.GetByteCount(config.ToJsonString()) > MaxConfigBytes)
        {
            return $"{at}.config is larger than {MaxConfigBytes / 1024} KiB.";
        }

        string? secret = SecretKeyIn(config, $"{at}.config");

        return secret is null ? null : $"{secret} is named like a secret; the workspace never saves one.";
    }

    private static string? SecretKeyIn(JsonNode? node, string at)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (SecretWords.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        return $"{at}.{key}";
                    }

                    if (SecretKeyIn(value, $"{at}.{key}") is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (SecretKeyIn(array[i], $"{at}[{i}]") is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = string.Empty;

        return node is JsonValue v && v.TryGetValue(out value!);
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;

        return node is JsonValue v && v.TryGetValue(out value);
    }
}
