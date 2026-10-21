using Admin.Host.Frontend;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class FrontendInstallTests : IDisposable
{
    private readonly DirectoryInfo clone = Directory.CreateTempSubdirectory("admin-frontend-");

    public void Dispose() => clone.Delete(recursive: true);

    private static string Lock(params (string Key, string Version, string? Extra)[] packages) =>
        "{\"lockfileVersion\":3,\"packages\":{\"\":{\"name\":\"app\"},"
        + string.Join(",", packages.Select(p => $"\"{p.Key}\":{{\"version\":\"{p.Version}\"{(p.Extra is null ? "" : "," + p.Extra)}}}"))
        + "}}";

    private void Write(string locked, string? installed)
    {
        File.WriteAllText(Path.Combine(clone.FullName, "package-lock.json"), locked);

        if (installed is not null)
        {
            DirectoryInfo modules = clone.CreateSubdirectory("node_modules");
            File.WriteAllText(Path.Combine(modules.FullName, ".package-lock.json"), installed);
        }
    }

    [Fact]
    public void No_node_modules_is_missing_and_says_to_run_npm_ci()
    {
        InstallReading reading = FrontendInstall.Read(clone.FullName);

        reading.State.ShouldBe(InstallState.Missing);
        reading.Usable.ShouldBeFalse();
        reading.Detail.ShouldBe($"{clone.FullName} has no node_modules. Run npm ci there; the console does not.");
    }

    [Fact]
    public void An_install_that_agrees_with_its_lock_is_current()
    {
        string both = Lock(("node_modules/@angular/core", "22.2.3", null), ("node_modules/rxjs", "7.8.2", null));
        Write(both, both);

        InstallReading reading = FrontendInstall.Read(clone.FullName);

        reading.State.ShouldBe(InstallState.Current);
        reading.Usable.ShouldBeTrue();
    }

    [Fact]
    public void A_version_that_differs_and_a_package_that_is_absent_make_it_stale_and_are_named()
    {
        // The workstation of #369: core at 22.1.6 against a lock at 22.2.3, and a package added since the last npm ci.
        Write(
            Lock(("node_modules/@angular/core", "22.2.3", null), ("node_modules/@angular/service-worker", "22.2.3", null), ("node_modules/rxjs", "7.8.2", null)),
            Lock(("node_modules/@angular/core", "22.1.6", null), ("node_modules/rxjs", "7.8.2", null)));

        InstallReading reading = FrontendInstall.Read(clone.FullName);

        reading.State.ShouldBe(InstallState.Stale);
        reading.Usable.ShouldBeFalse();
        reading.Detail.ShouldContain("@angular/core 22.1.6 is installed (the lock has 22.2.3)");
        reading.Detail.ShouldContain("@angular/service-worker is not installed (the lock has 22.2.3)");
        reading.Detail.ShouldContain($"Run npm ci in {clone.FullName}; the console does not.");
        reading.Detail.ShouldContain("installed ");
        reading.Detail.ShouldContain("lock changed ");
    }

    [Fact]
    public void An_optional_package_the_platform_skipped_and_an_extraneous_one_do_not_make_it_stale()
    {
        Write(
            Lock(("node_modules/rxjs", "7.8.2", null), ("node_modules/@esbuild/darwin-arm64", "0.25.0", "\"optional\":true")),
            Lock(("node_modules/rxjs", "7.8.2", null), ("node_modules/left-over", "1.0.0", null)));

        FrontendInstall.Read(clone.FullName).State.ShouldBe(InstallState.Current);
    }

    [Fact]
    public void The_cache_follows_the_files_so_an_npm_ci_after_the_reading_is_seen()
    {
        string old = Lock(("node_modules/rxjs", "7.8.1", null));
        string fresh = Lock(("node_modules/rxjs", "7.8.2", null));
        Write(fresh, old);
        FrontendInstall.Read(clone.FullName).State.ShouldBe(InstallState.Stale);

        File.WriteAllText(Path.Combine(clone.FullName, "node_modules", ".package-lock.json"), fresh + " ");

        FrontendInstall.Read(clone.FullName).State.ShouldBe(InstallState.Current);
    }

    [Fact]
    public void A_lock_that_cannot_be_compared_is_unverified_and_still_usable()
    {
        Write("{ not json", Lock(("node_modules/rxjs", "7.8.2", null)));

        InstallReading broken = FrontendInstall.Read(clone.FullName);

        broken.State.ShouldBe(InstallState.Unverified);
        broken.Usable.ShouldBeTrue();

        File.Delete(Path.Combine(clone.FullName, "node_modules", ".package-lock.json"));

        FrontendInstall.Read(clone.FullName).Detail.ShouldContain("node_modules/.package-lock.json is not there");
    }

    [Fact]
    public void A_lock_that_lists_no_packages_is_unverified_not_current()
    {
        // lockfileVersion 1 keeps `dependencies`, not `packages`.
        Write("{\"lockfileVersion\":1,\"dependencies\":{\"rxjs\":{\"version\":\"7.8.2\"}}}", Lock(("node_modules/rxjs", "7.8.2", null)));

        InstallReading reading = FrontendInstall.Read(clone.FullName);

        reading.State.ShouldBe(InstallState.Unverified);
        reading.Usable.ShouldBeTrue();
        reading.Detail.ShouldContain("lists no node_modules packages");
    }

    [Fact]
    public void An_unverified_reading_is_read_again_so_a_file_that_was_held_is_not_remembered_as_unreadable()
    {
        string both = Lock(("node_modules/rxjs", "7.8.2", null));
        Write(both, both);

        // npm or a scanner holds the lock for a moment: the read fails, and the files do not change when it lets go.
        using (new FileStream(Path.Combine(clone.FullName, "package-lock.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FrontendInstall.Read(clone.FullName).State.ShouldBe(InstallState.Unverified);
        }

        FrontendInstall.Read(clone.FullName).State.ShouldBe(InstallState.Current);
    }

    [Fact]
    public void FakePlatform_replays_an_install_that_agrees()
    {
        FrontendInstall.Replayed("/does/not/exist").State.ShouldBe(InstallState.Current);
    }
}
