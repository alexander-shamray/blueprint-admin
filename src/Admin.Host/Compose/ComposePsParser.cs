using System.Text.Json;
using System.Text.Json.Serialization;

namespace Admin.Host.Compose;

/// <summary>
/// <c>docker compose ps --format json</c> prints one object per line since
/// Compose 2.21 and a single array before that. Both are accepted.
/// </summary>
public static class ComposePsParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ServiceStatus> Parse(IEnumerable<string> stdoutLines)
    {
        List<ServiceStatus> services = [];

        foreach (string raw in stdoutLines)
        {
            string line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                services.AddRange(JsonSerializer.Deserialize<List<PsRow>>(line, Options)!.Select(ToStatus));
            }
            else
            {
                services.Add(ToStatus(JsonSerializer.Deserialize<PsRow>(line, Options)!));
            }
        }

        return services;
    }

    private static ServiceStatus ToStatus(PsRow row) => new(
        row.Service,
        row.State,
        string.IsNullOrEmpty(row.Health) ? null : row.Health,
        row.ExitCode,
        (row.Publishers ?? []).Where(p => p.PublishedPort > 0).Select(p => p.PublishedPort).Distinct().ToList());

    private sealed record PsRow(
        string Service,
        string State,
        string? Health,
        int? ExitCode,
        [property: JsonPropertyName("Publishers")] List<Publisher>? Publishers);

    private sealed record Publisher(int PublishedPort);
}
