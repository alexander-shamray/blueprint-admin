using System.Text.Json;
using Admin.Host.Jobs;

namespace Admin.Host.Compose;

/// <summary>
/// A line of <c>docker compose --progress json</c> (see <see cref="ComposeService.Progress"/>). The SPA's output pane
/// reads these as they are (<c>compose-json.ts</c>); every other reader of a job's lines gets them in the plain form
/// Compose wrote before the flag.
/// </summary>
internal static class ComposeJsonLine
{
    /// <summary>The resource a JSON line reports on (<c>Container commerce-sql-1</c>, a layer's id), or null for any other line.</summary>
    public static string? ResourceOf(string text) => Parse(text) is { } root ? Text(root, "id") : null;

    /// <summary>
    /// The line as Compose's plain output writes it, <c> Container commerce-sql-1  Healthy</c>; a line that is not Compose's
    /// JSON comes back as it is. This is <c>composePlain</c> in <c>compose-json.ts</c>.
    /// </summary>
    public static string Plain(string text)
    {
        if (Parse(text) is not { } root)
        {
            return text;
        }

        string? message = Text(root, "message");

        if (Text(root, "id") is not { } id)
        {
            return message ?? text;
        }

        string? details = Text(root, "details");
        string suffix = details is { Length: > 0 } && details != "0B" ? $" {details}" : "";

        return $" {id}  {Text(root, "text") ?? message ?? ""}{suffix}";
    }

    public static OutputLine Plain(OutputLine line) => line.Text.StartsWith('{') ? line with { Text = Plain(line.Text) } : line;

    private static JsonElement? Parse(string text)
    {
        if (!text.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement.Clone();

            return root.ValueKind == JsonValueKind.Object && (Text(root, "id") is not null || Text(root, "message") is not null) ? root : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
