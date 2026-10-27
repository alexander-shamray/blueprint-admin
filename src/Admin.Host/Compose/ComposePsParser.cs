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
        (row.Publishers ?? []).Where(p => p.PublishedPort > 0).Select(p => p.PublishedPort).Distinct().ToList(),
        row.Labels is null ? null : ConfigFiles(row.Labels));

    /// <summary>
    /// The file names in the container's <c>com.docker.compose.project.config_files</c> label. <c>ps</c> joins labels with commas
    /// and Compose joins the files inside that one with commas too, so a part with no <c>=</c> belongs to the label before it.
    /// </summary>
    internal static IReadOnlyList<string> ConfigFiles(string labels)
    {
        List<string> files = [];
        bool inside = false;

        foreach (string part in labels.Split(','))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals >= 0)
            {
                inside = part[..equals] == ConfigFilesLabel;

                if (!inside)
                {
                    continue;
                }
            }
            else if (!inside)
            {
                continue;
            }

            string path = equals >= 0 ? part[(equals + 1)..] : part;

            if (path.Length > 0)
            {
                files.Add(path[(path.LastIndexOfAny(['/', '\\']) + 1)..]);
            }
        }

        return files;
    }

    private const string ConfigFilesLabel = "com.docker.compose.project.config_files";

    private sealed record PsRow(
        string Service,
        string State,
        string? Health,
        int? ExitCode,
        [property: JsonPropertyName("Publishers")] List<Publisher>? Publishers,
        string? Labels);

    private sealed record Publisher(int PublishedPort);
}
