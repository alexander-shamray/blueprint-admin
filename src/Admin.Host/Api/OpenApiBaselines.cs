using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Api;

/// <summary>
/// The last accepted OpenAPI document per service, one file each under <c>Admin:DataDir</c>
/// (<see cref="Config.AdminOptions.DataDir"/>), with the time it was taken. A service seen for the
/// first time is baselined there and then; after that the baseline stays until it is accepted again, so
/// a change keeps showing until someone says they have read it.
/// </summary>
public sealed class OpenApiBaselines(string directory, TimeProvider time)
{
    // One read-or-write at a time: a first sight writes the file another call may be reading.
    private readonly Lock gate = new();

    /// <summary>Whether <paramref name="service"/> has a baseline yet.</summary>
    public bool Has(string service)
    {
        lock (gate)
        {
            return File.Exists(PathOf(service));
        }
    }

    public OpenApiChanges Compare(string service, string document)
    {
        lock (gate)
        {
            return CompareUnlocked(service, document);
        }
    }

    /// <summary>
    /// Makes <paramref name="document"/> the baseline <paramref name="service"/> is compared with from now on, or
    /// says why the file could not be written.
    /// </summary>
    public string? Accept(string service, string document)
    {
        lock (gate)
        {
            try
            {
                Save(PathOf(service), document);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"The baseline for {service} could not be written to {PathOf(service)}: {e.Message}";
            }
        }
    }

    private OpenApiChanges CompareUnlocked(string service, string document)
    {
        try
        {
            string path = PathOf(service);

            if (!File.Exists(path))
            {
                return OpenApiChanges.Taken(Save(path, document));
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject kept
                || kept["savedAt"] is not JsonValue savedAt
                || kept["document"] is not JsonNode keptDocument)
            {
                return OpenApiChanges.Failed($"{path} is not a baseline this console wrote; delete it to take a new one.");
            }

            using JsonDocument baseline = JsonDocument.Parse(keptDocument.ToJsonString());
            using JsonDocument current = JsonDocument.Parse(document);

            return OpenApiChanges.Between(OpenApiShape.Of(baseline.RootElement), OpenApiShape.Of(current.RootElement), savedAt.GetValue<DateTimeOffset>());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return OpenApiChanges.Failed($"The kept document for {service} could not be read or written: {e.Message}");
        }
    }

    private DateTimeOffset Save(string path, string document)
    {
        DateTimeOffset now = time.GetUtcNow();
        JsonObject envelope = new() { ["savedAt"] = now, ["document"] = JsonNode.Parse(document) };

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, envelope.ToJsonString());

        return now;
    }

    // The service names are ApiCatalog's own; its accept refuses any name it did not fetch, so a caller's string never reaches a path.
    private string PathOf(string service) => Path.Combine(directory, $"openapi-{service}.json");
}
