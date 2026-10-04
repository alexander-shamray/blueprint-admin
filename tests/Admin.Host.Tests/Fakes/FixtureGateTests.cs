using System.Xml.Linq;
using Admin.Host.Fakes;
using Admin.Host.Tests.Drift;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>
/// The fixtures' gate, and its subject is what it looks at: every file under <c>Fakes/</c>, read from the
/// directory rather than from a list someone keeps. A file the scrubber would change fails the build, so a token
/// pasted into a hand-written fixture is caught the same way as one a recording would have carried. A file
/// <see cref="FixtureRecordings"/> does not account for fails it too, so a fixture always names its source.
/// </summary>
public sealed class FixtureGateTests
{
    private static readonly string FakesDir = Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Fakes");

    private static readonly string FixturesDir = Path.Combine(FakesDir, "fixtures");

    [Fact]
    public void No_file_under_fakes_holds_anything_the_scrubber_would_remove()
    {
        Unscrubbed(FakesDir).ShouldBeEmpty();
    }

    [Fact]
    public void The_scan_reaches_every_fixture_the_host_embeds_and_the_fakes_own_source()
    {
        string[] scanned = [.. Scanned(FakesDir).Select(Relative)];

        scanned.ShouldContain("src/Admin.Host/Fakes/WorkstationRecordings.cs");
        Embedded().Where(f => !scanned.Contains(f)).ShouldBeEmpty();
    }

    [Fact]
    public void A_token_planted_in_a_fixture_is_what_the_scan_reports()
    {
        DirectoryInfo planted = Directory.CreateTempSubdirectory("admin-fixture-gate-");

        try
        {
            File.WriteAllText(Path.Combine(planted.FullName, "clean.json"), """{"status":"success"}""");
            File.WriteAllText(
                Path.Combine(planted.FullName, "requests.json"),
                """[{"request":{"headers":{"Authorization":["Bearer 0123456789abcdef0123"]}}}]""");

            Unscrubbed(planted.FullName).ShouldBe(["requests.json: a bearer credential, an Authorization header"]);
        }
        finally
        {
            planted.Delete(recursive: true);
        }
    }

    [Fact]
    public void Every_fixture_is_either_recorded_or_hand_written_with_a_reason()
    {
        string[] recorded = [.. FixtureRecordings.Http.Select(r => r.Fixture), .. FixtureRecordings.Process.Select(r => r.Fixture)];
        string[] files = [.. Directory.EnumerateFiles(FixturesDir).Select(Path.GetFileName).OfType<string>()];

        files.Where(f => !recorded.Contains(f) && !FixtureRecordings.HandWritten.ContainsKey(f)).ShouldBeEmpty("a fixture with no source named");
        recorded.Intersect(FixtureRecordings.HandWritten.Keys).ShouldBeEmpty("a fixture both recorded and hand-written");
        recorded.Concat(FixtureRecordings.HandWritten.Keys).Where(f => !files.Contains(f)).ShouldBeEmpty("a source for a fixture that is not there");
        recorded.ShouldBeUnique();
        FixtureRecordings.HandWritten.Values.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Every_fixture_file_is_embedded_and_every_embedded_fixture_is_a_file()
    {
        string[] files = [.. Directory.EnumerateFiles(FixturesDir).Select(Relative)];

        Embedded().Order(StringComparer.Ordinal).ShouldBe(files.Order(StringComparer.Ordinal));
    }

    /// <summary>Each scanned file whose content the scrubber would change, with the rules it breaks.</summary>
    private static string[] Unscrubbed(string root) =>
    [
        .. Scanned(root)
            .Select(file => (File: file, Findings: FixtureScrubber.Findings(File.ReadAllText(file))))
            .Where(f => f.Findings.Count > 0)
            .Select(f => $"{Path.GetRelativePath(root, f.File).Replace('\\', '/')}: {string.Join(", ", f.Findings)}"),
    ];

    /// <summary>Every file, whatever its extension: a fixture's format is not the gate's to guess.</summary>
    private static IEnumerable<string> Scanned(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    /// <summary>The host project's embedded resources, as repository-relative paths.</summary>
    private static string[] Embedded()
    {
        string project = Path.Combine(Backend.AdminRoot, "src", "Admin.Host", "Admin.Host.csproj");

        return
        [
            .. XDocument.Load(project).Descendants("EmbeddedResource")
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .Select(include => Relative(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)))),
        ];
    }

    private static string Relative(string file) => Path.GetRelativePath(Backend.AdminRoot, file).Replace('\\', '/');
}
