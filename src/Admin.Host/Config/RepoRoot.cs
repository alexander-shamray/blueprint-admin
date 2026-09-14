namespace Admin.Host.Config;

/// <summary>
/// Locates the repository root by walking up from a starting directory to the
/// first ancestor containing <c>BlueprintAdmin.slnx</c>. Build output always
/// lands under <c>artifacts/</c> beneath the root (Directory.Build.props), so
/// walking up from <see cref="AppContext.BaseDirectory"/> finds it regardless
/// of the working directory the host was started from.
/// </summary>
public static class RepoRoot
{
    private const string Marker = "BlueprintAdmin.slnx";

    public static string? Find(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, Marker)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
