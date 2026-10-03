using System.Text.RegularExpressions;

namespace Admin.Host.Tests.Drift;

/// <summary>One Compose service of the backend, the unit file that declares it, and its first host port.</summary>
internal sealed record ComposeUnitService(string Name, string Unit, int? HostPort);

/// <summary>
/// The backend's Compose tree read the way it is written: <c>docker-compose.yml</c> is an index of
/// <c>include</c> lines and declares nothing, <c>infrastructure.yml</c> and each <c>services/*.yml</c>
/// declare under a top-level <c>services:</c>. A line reader rather than a YAML library, because
/// these files keep that shape on purpose (one service per unit file) and a parser that accepted
/// more would hide a change of shape this gate should see.
/// </summary>
internal static partial class Compose
{
    /// <summary>The unit files the index includes, as written: <c>infrastructure.yml</c>, <c>services/catalog.yml</c>.</summary>
    public static IReadOnlyList<string> Includes()
    {
        List<string> includes = [];
        bool inInclude = false;

        foreach (string line in Lines("docker-compose.yml"))
        {
            if (TopLevel().IsMatch(line))
            {
                inInclude = line.StartsWith("include:", StringComparison.Ordinal);
                continue;
            }

            Match item = IncludeItem().Match(line);

            if (inInclude && item.Success)
            {
                includes.Add(item.Groups[1].Value);
            }
        }

        return includes;
    }

    /// <summary>The service units: every included file under <c>services/</c>, named without its extension.</summary>
    public static IReadOnlyList<string> Units() =>
        [.. Includes().Where(i => i.StartsWith("services/", StringComparison.Ordinal)).Select(i => Path.GetFileNameWithoutExtension(i))];

    public static IReadOnlyList<ComposeUnitService> Services()
    {
        List<ComposeUnitService> services = [];

        foreach (string include in Includes())
        {
            string unit = include.StartsWith("services/", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(include) : "";
            bool inServices = false;
            string? current = null;
            int? port = null;

            foreach (string line in Lines(include))
            {
                if (TopLevel().IsMatch(line))
                {
                    Flush();
                    inServices = line.StartsWith("services:", StringComparison.Ordinal);
                    continue;
                }

                if (!inServices)
                {
                    continue;
                }

                Match name = ServiceKey().Match(line);

                if (name.Success)
                {
                    Flush();
                    current = name.Groups[1].Value;
                    continue;
                }

                Match ports = HostPort().Match(line);

                if (current is not null && port is null && line.TrimStart().StartsWith("ports:", StringComparison.Ordinal) && ports.Success)
                {
                    port = int.Parse(ports.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            Flush();

            void Flush()
            {
                if (current is not null)
                {
                    services.Add(new ComposeUnitService(current, unit, port));
                }

                current = null;
                port = null;
            }
        }

        return services;
    }

    private static string[] Lines(string relative) =>
        Backend.Read("deploy", "compose", relative).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [GeneratedRegex(@"^[a-z]")]
    private static partial Regex TopLevel();

    [GeneratedRegex(@"^  - ([\w./-]+\.yml)\s*$")]
    private static partial Regex IncludeItem();

    [GeneratedRegex(@"^  ([a-z0-9-]+):\s*$")]
    private static partial Regex ServiceKey();

    [GeneratedRegex(@"""(?:[\d.]+:)?(\d+):\d+""")]
    private static partial Regex HostPort();
}
