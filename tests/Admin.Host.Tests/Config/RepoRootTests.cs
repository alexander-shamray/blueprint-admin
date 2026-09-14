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
}
