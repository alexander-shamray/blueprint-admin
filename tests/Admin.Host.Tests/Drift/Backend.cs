using Admin.Host.Config;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// Where the sibling clone is, resolved the way the host resolves it (<see cref="RepoPaths"/>,
/// <see cref="RepoRoot.MainCheckout"/>) and overridden by the host's own key,
/// <c>BLUEPRINT_Admin__BackendDir</c>. A drift check with no clone to read skips, because a
/// developer without the backend has nothing to drift from; <c>ADMIN_DRIFT_REQUIRED=true</c>
/// turns that skip into a failure, so the CI job that checks the backend out cannot pass by
/// failing to find it.
/// </summary>
internal static class Backend
{
    private static readonly Lazy<string?> Located = new(Locate);

    /// <summary>The backend clone, or the test is skipped (or failed, where it is required).</summary>
    public static string Dir
    {
        get
        {
            string? dir = Located.Value;

            if (dir is not null)
            {
                return dir;
            }

            if (Required)
            {
                Assert.Fail("ADMIN_DRIFT_REQUIRED is set and no blueprint-backend clone was found; set BLUEPRINT_Admin__BackendDir.");
            }

            Assert.Skip("No blueprint-backend clone beside this checkout, so there is nothing to drift from.");
            return "";
        }
    }

    /// <summary>
    /// run-locally.md, at the workspace root above the clones. No repository carries it, so CI never
    /// has it and this skips there even where the backend is required.
    /// </summary>
    public static string RunLocally
    {
        get
        {
            string path = Path.GetFullPath(Path.Combine(Dir, "..", "run-locally.md"));
            Assert.SkipUnless(File.Exists(path), "run-locally.md is at the workspace root, which no repository carries.");
            return path;
        }
    }

    /// <summary>This repository's root, the main checkout's or a worktree's.</summary>
    public static string AdminRoot { get; } =
        RepoRoot.Find(AppContext.BaseDirectory) ?? throw new InvalidOperationException("BlueprintAdmin.slnx not found above the test assembly.");

    public static string Read(params string[] relative) => File.ReadAllText(Path.Combine([Dir, .. relative]));

    private static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("ADMIN_DRIFT_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);

    private static string? Locate()
    {
        AdminOptions defaults = new();
        string configured = Environment.GetEnvironmentVariable("BLUEPRINT_Admin__BackendDir") ?? defaults.BackendDir;
        string dir = Path.GetFullPath(configured, RepoRoot.MainCheckout(AdminRoot));

        return File.Exists(Path.Combine(dir, defaults.ComposeFile)) ? dir : null;
    }
}
