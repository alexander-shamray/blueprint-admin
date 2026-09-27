using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class RepoRootTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("admin-repo-root-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Finds_the_directory_containing_the_marker_two_levels_up()
    {
        File.WriteAllText(Path.Combine(root, "BlueprintAdmin.slnx"), string.Empty);
        string start = Path.Combine(root, "artifacts", "bin");
        Directory.CreateDirectory(start);

        string? found = RepoRoot.Find(start);

        found.ShouldBe(root);
    }

    [Fact]
    public void Returns_null_when_no_ancestor_has_the_marker()
    {
        string start = Path.Combine(root, "nested");
        Directory.CreateDirectory(start);

        string? found = RepoRoot.Find(start);

        found.ShouldBeNull();
    }

    [Fact]
    public void A_main_checkout_is_its_own_main_checkout()
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        RepoRoot.MainCheckout(root).ShouldBe(root);
    }

    [Fact]
    public void A_directory_outside_any_repository_is_left_as_is()
    {
        RepoRoot.MainCheckout(root).ShouldBe(root);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_linked_worktree_resolves_to_the_main_checkout_holding_it(bool absoluteGitDir)
    {
        string main = Path.Combine(root, "blueprint-admin");
        string worktree = Path.Combine(main, ".claude", "worktrees", "feature");
        string gitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        string pointer = absoluteGitDir ? gitDir.Replace('\\', '/') : "../../../.git/worktrees/feature";
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {pointer}\n");

        RepoRoot.MainCheckout(worktree).ShouldBe(main);
    }

    [Fact]
    public void A_git_file_that_is_not_a_worktree_pointer_is_left_as_is()
    {
        File.WriteAllText(Path.Combine(root, ".git"), "not a pointer\n");

        RepoRoot.MainCheckout(root).ShouldBe(root);
    }
}
