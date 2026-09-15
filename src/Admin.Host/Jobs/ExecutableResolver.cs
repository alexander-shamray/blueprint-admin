namespace Admin.Host.Jobs;

/// <summary>
/// Turns a bare command name into something the operating system can start.
/// Windows' CreateProcess appends only <c>.exe</c>, so <c>npm</c>, which is
/// <c>npm.cmd</c> there, is "file not found" unless PATH is searched with
/// PATHEXT the way a shell does. Off Windows, and for a name that already
/// carries an extension or a directory, the name is returned unchanged; a name
/// that resolves nowhere is returned unchanged too, so the start fails with the
/// name the user recognises. A name may resolve to a <c>.cmd</c> or <c>.bat</c>,
/// which Windows runs through <c>cmd.exe</c> without escaping its
/// metacharacters, so arguments passed to such a command must never carry
/// user input.
/// </summary>
public static class ExecutableResolver
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    public static string Resolve(string fileName) => Resolve(
        fileName,
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        File.Exists);

    public static string Resolve(string fileName, bool isWindows, string? path, string? pathExt, Func<string, bool> fileExists)
    {
        if (!isWindows || string.IsNullOrEmpty(path) || Path.HasExtension(fileName) || fileName.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return fileName;
        }

        string[] extensions = (string.IsNullOrEmpty(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Windows accepts a quoted entry such as "C:\Program Files\nodejs";
            // the quotes are not part of the directory.
            string directory = entry.Trim('"');

            if (directory.Length == 0)
            {
                continue;
            }

            foreach (string extension in extensions)
            {
                // Real command shims are lowercase (npm.cmd) while PATHEXT is
                // conventionally upper case; File.Exists is case-insensitive on
                // Windows, but a case-sensitive fileExists (as in tests) is not,
                // so match the on-disk convention rather than PATHEXT's casing.
                string candidate = Path.Combine(directory, fileName + extension.ToLowerInvariant());

                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }
}
