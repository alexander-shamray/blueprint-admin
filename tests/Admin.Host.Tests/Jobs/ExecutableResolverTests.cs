using Admin.Host.Jobs;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class ExecutableResolverTests
{
    private const string Tools = @"C:\tools";
    private const string Node = @"C:\node";
    private const string PathExt = ".COM;.EXE;.BAT;.CMD";

    [Fact]
    public void Npm_resolves_to_its_cmd_shim_on_windows()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd")];

        ExecutableResolver.Resolve("npm", true, $"{Tools};{Node}", PathExt, files.Contains).ShouldBe(Path.Combine(Node, "npm.cmd"));
    }

    [Fact]
    public void An_earlier_path_directory_wins_over_a_better_extension_later()
    {
        HashSet<string> files = [Path.Combine(Tools, "npm.cmd"), Path.Combine(Node, "npm.exe")];

        ExecutableResolver.Resolve("npm", true, $"{Tools};{Node}", PathExt, files.Contains).ShouldBe(Path.Combine(Tools, "npm.cmd"));
    }

    [Fact]
    public void Within_a_directory_pathext_order_decides()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd"), Path.Combine(Node, "npm.exe")];

        ExecutableResolver.Resolve("npm", true, Node, PathExt, files.Contains).ShouldBe(Path.Combine(Node, "npm.exe"));
    }

    [Fact]
    public void An_empty_pathext_falls_back_to_the_windows_default()
    {
        HashSet<string> files = [Path.Combine(Node, "npm.cmd")];

        ExecutableResolver.Resolve("npm", true, Node, null, files.Contains).ShouldBe(Path.Combine(Node, "npm.cmd"));
    }

    [Fact]
    public void Names_are_unchanged_off_windows()
    {
        ExecutableResolver.Resolve("npm", false, Node, PathExt, _ => true).ShouldBe("npm");
    }

    [Theory]
    [InlineData("npm.cmd")]
    [InlineData(@"C:\node\npm")]
    [InlineData("./npm")]
    public void A_name_with_an_extension_or_a_directory_is_unchanged(string fileName)
    {
        ExecutableResolver.Resolve(fileName, true, Node, PathExt, _ => true).ShouldBe(fileName);
    }

    [Fact]
    public void A_quoted_path_entry_is_searched_without_its_quotes()
    {
        // Windows accepts "C:\Program Files\nodejs" in PATH; the quotes are not part of the directory.
        HashSet<string> files = [Path.Combine(@"C:\Program Files\nodejs", "npm.cmd")];

        ExecutableResolver.Resolve("npm", true, $"{Tools};\"C:\\Program Files\\nodejs\"", PathExt, files.Contains)
            .ShouldBe(Path.Combine(@"C:\Program Files\nodejs", "npm.cmd"));
    }

    [Fact]
    public void An_unresolvable_name_is_returned_as_is_so_start_reports_it()
    {
        ExecutableResolver.Resolve("nope", true, $"{Tools};{Node}", PathExt, _ => false).ShouldBe("nope");
    }
}
