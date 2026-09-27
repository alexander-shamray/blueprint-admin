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

    /// <summary>
    /// The main checkout <paramref name="root"/> belongs to when it is a linked
    /// git worktree, or <paramref name="root"/> itself. The sibling clones sit
    /// beside the main checkout, and <c>/branch</c> forks its worktrees inside
    /// it, under <c>.claude/worktrees/</c>, where <c>..</c> is not the
    /// workspace. A linked worktree's <c>.git</c> is a file naming its private
    /// git directory, and that directory's <c>commondir</c> names the main
    /// checkout's <c>.git</c>; anything else found there leaves the root as is.
    /// </summary>
    public static string MainCheckout(string root)
    {
        const string Prefix = "gitdir:";
        string dotGit = Path.Combine(root, ".git");

        if (!File.Exists(dotGit))
        {
            return root;
        }

        string pointer = File.ReadAllText(dotGit).Trim();

        if (!pointer.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return root;
        }

        string gitDir = Path.GetFullPath(pointer[Prefix.Length..].Trim(), root);
        string commonDirFile = Path.Combine(gitDir, "commondir");

        if (!File.Exists(commonDirFile))
        {
            return root;
        }

        DirectoryInfo common = new(Path.GetFullPath(File.ReadAllText(commonDirFile).Trim(), gitDir));

        return common.Name == ".git" && common.Parent is not null ? common.Parent.FullName : root;
    }
}
