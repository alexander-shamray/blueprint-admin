using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Config;

public sealed class RepoPathsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("admin-paths-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string MakeClones()
    {
        Directory.CreateDirectory(Path.Combine(root, "blueprint-backend", "deploy", "compose"));
        File.WriteAllText(Path.Combine(root, "blueprint-backend", "deploy", "compose", "docker-compose.yml"), "name: commerce\n");
        Directory.CreateDirectory(Path.Combine(root, "blueprint-frontend"));
        File.WriteAllText(Path.Combine(root, "blueprint-frontend", "package.json"), "{}");

        return Path.Combine(root, "blueprint-admin");
    }

    [Fact]
    public void Resolves_the_defaults_against_the_base_directory()
    {
        string baseDir = MakeClones();

        RepoPaths paths = RepoPaths.From(new AdminOptions(), baseDir);

        paths.BackendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "blueprint-backend")));
        paths.FrontendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "blueprint-frontend")));
        paths.ComposeFile.ShouldBe(Path.Combine(paths.BackendDir, "deploy", "compose", "docker-compose.yml"));
    }

    [Fact]
    public void Names_the_key_when_the_backend_clone_is_missing()
    {
        string baseDir = MakeClones();
        Directory.Delete(Path.Combine(root, "blueprint-backend"), recursive: true);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:BackendDir");
    }

    [Fact]
    public void Names_the_key_when_the_compose_file_is_missing()
    {
        string baseDir = MakeClones();
        File.Delete(Path.Combine(root, "blueprint-backend", "deploy", "compose", "docker-compose.yml"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:ComposeFile");
    }

    [Fact]
    public void Names_the_key_when_the_frontend_has_no_package_json()
    {
        string baseDir = MakeClones();
        File.Delete(Path.Combine(root, "blueprint-frontend", "package.json"));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => RepoPaths.From(new AdminOptions(), baseDir));

        error.Message.ShouldContain("Admin:FrontendDir");
    }

    [Fact]
    public void Skips_validation_when_the_platform_is_fake()
    {
        AdminOptions options = new() { FakePlatform = true, BackendDir = "nowhere", FrontendDir = "nowhere" };

        RepoPaths paths = RepoPaths.From(options, root);

        paths.BackendDir.ShouldBe(Path.GetFullPath(Path.Combine(root, "nowhere")));
    }
}
