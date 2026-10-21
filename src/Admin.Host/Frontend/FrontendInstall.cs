using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Admin.Host.Frontend;

public enum InstallState
{
    /// <summary>The installed tree agrees with <c>package-lock.json</c>.</summary>
    Current,

    /// <summary>There is no <c>node_modules</c> at all.</summary>
    Missing,

    /// <summary>There is one, and it differs from <c>package-lock.json</c>: an <c>npm ci</c> is owed.</summary>
    Stale,

    /// <summary>One of the two lock files could not be read, so nothing can be said either way.</summary>
    Unverified,
}

/// <summary>What the frontend clone's install is, with the sentence the Stack screen and the doctor show for it.</summary>
public sealed record InstallReading(InstallState State, string Detail)
{
    /// <summary>Whether Start may run: only an install known to differ from the lock, or none, is refused.</summary>
    public bool Usable => State is InstallState.Current or InstallState.Unverified;
}

/// <summary>How the host reads the frontend clone's install; FakePlatform replaces it with a recorded answer.</summary>
public delegate InstallReading InstallReader(string frontendDir);

/// <summary>
/// Whether the frontend clone's <c>node_modules</c> is the one its <c>package-lock.json</c> describes (#369).
/// <c>Start</c> used to check that the directory existed, and an install left from an older lock passed: <c>ng serve</c>
/// then failed with dozens of compile errors that read as a broken frontend <c>main</c>. npm writes
/// <c>node_modules/.package-lock.json</c> beside what it installs, in the lock file's own shape, so the two are
/// compared by the version of every package: a file's age is not the question, because a checkout touches the lock
/// without changing it. A package the lock lists that the install lacks is a difference unless the lock marks it
/// optional (a platform's own binaries); an installed package the lock does not list is not, because npm may keep
/// extraneous ones. The console never runs <c>npm ci</c> itself (spec §2.1); this only makes the reason visible.
/// </summary>
public static class FrontendInstall
{
    private const string MissingTail = "Run npm ci there; the console does not.";

    private static readonly ConcurrentDictionary<string, (Stamp Lock, Stamp Hidden, InstallReading Reading)> Cache = new();

    private readonly record struct Stamp(long Ticks, long Length);

    /// <summary>The recorded answer FakePlatform gives: an install that agrees with its lock.</summary>
    public static InstallReading Replayed(string frontendDir) =>
        new(InstallState.Current, "node_modules matches package-lock.json.");

    public static InstallReading Read(string frontendDir)
    {
        string modules = Path.Combine(frontendDir, "node_modules");

        if (!Directory.Exists(modules))
        {
            return new InstallReading(InstallState.Missing, $"{frontendDir} has no node_modules. {MissingTail}");
        }

        FileInfo lockFile = new(Path.Combine(frontendDir, "package-lock.json"));
        FileInfo hidden = new(Path.Combine(modules, ".package-lock.json"));

        if (!lockFile.Exists || !hidden.Exists)
        {
            string absent = lockFile.Exists ? "node_modules/.package-lock.json" : "package-lock.json";

            return new InstallReading(InstallState.Unverified, $"{absent} is not there, so the install cannot be compared with the lock.");
        }

        Stamp lockStamp = new(lockFile.LastWriteTimeUtc.Ticks, lockFile.Length);
        Stamp hiddenStamp = new(hidden.LastWriteTimeUtc.Ticks, hidden.Length);

        if (Cache.TryGetValue(frontendDir, out var cached) && cached.Lock == lockStamp && cached.Hidden == hiddenStamp)
        {
            return cached.Reading;
        }

        InstallReading reading = Compare(frontendDir, lockFile, hidden);
        Cache[frontendDir] = (lockStamp, hiddenStamp, reading);

        return reading;
    }

    private static InstallReading Compare(string frontendDir, FileInfo lockFile, FileInfo hidden)
    {
        try
        {
            using JsonDocument locked = JsonDocument.Parse(File.ReadAllBytes(lockFile.FullName));
            using JsonDocument installed = JsonDocument.Parse(File.ReadAllBytes(hidden.FullName));

            Dictionary<string, string> have = new(StringComparer.Ordinal);

            foreach ((string key, string version, _) in Wanted(installed.RootElement))
            {
                have[key] = version;
            }

            List<string> differences = [];

            foreach ((string key, string version, bool optional) in Wanted(locked.RootElement))
            {
                if (!have.TryGetValue(key, out string? present))
                {
                    if (!optional)
                    {
                        differences.Add($"{Short(key)} is not installed (the lock has {version})");
                    }
                }
                else if (present != version)
                {
                    differences.Add($"{Short(key)} {present} is installed (the lock has {version})");
                }
            }

            if (differences.Count == 0)
            {
                return new InstallReading(InstallState.Current, "node_modules matches package-lock.json.");
            }

            string shown = string.Join("; ", differences.Take(3));
            string more = differences.Count > 3 ? $", and {differences.Count - 3} more" : "";

            return new InstallReading(
                InstallState.Stale,
                $"node_modules does not match package-lock.json (installed {Date(hidden)}, lock changed {Date(lockFile)}): {shown}{more}. Run npm ci in {frontendDir}; the console does not.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new InstallReading(InstallState.Unverified, $"The lock files could not be compared: {e.Message}");
        }
    }

    private static string Date(FileInfo file) => file.LastWriteTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A key such as <c>node_modules/a/node_modules/@b/c</c> by the package it names, <c>@b/c</c>.</summary>
    private static string Short(string key) => key[(key.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];

    /// <summary>Every <c>node_modules/…</c> entry that has a version, by the key npm files it under.</summary>
    private static IEnumerable<(string Key, string Version, bool Optional)> Wanted(JsonElement root)
    {
        if (!root.TryGetProperty("packages", out JsonElement packages) || packages.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty entry in packages.EnumerateObject())
        {
            // The root project ("") and workspace or linked entries are not installed packages.
            if (!entry.Name.StartsWith("node_modules/", StringComparison.Ordinal)
                || entry.Value.ValueKind != JsonValueKind.Object
                || Flag(entry.Value, "link")
                || !entry.Value.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            yield return (entry.Name, version.GetString()!, Flag(entry.Value, "optional") || Flag(entry.Value, "devOptional"));
        }
    }

    private static bool Flag(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
