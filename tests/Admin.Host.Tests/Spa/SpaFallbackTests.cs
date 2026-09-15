using System.Net;
using Admin.Host.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Admin.Host.Tests.Spa;

/// <summary>
/// The host serves the built SPA from a fixed location under the repository
/// root regardless of the process's working directory or environment, and
/// never lets an unknown API route fall back to it. Each test points
/// <c>Admin:WebRoot</c> at its own temp directory so these do not depend on
/// <c>npm run build</c> having produced the real wwwroot.
/// </summary>
public sealed class SpaFallbackTests : IDisposable
{
    private const string RelativeMarker = "fake spa shell resolved against the repo root, not the working directory";

    private readonly string webRoot = Directory.CreateTempSubdirectory("admin-host-spa-").FullName;
    private readonly string repoRoot = RepoRoot.Find(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("BlueprintAdmin.slnx not found above the test assembly.");
    private readonly string relativeWebRoot = Path.Combine("artifacts", $"spa-fallback-test-{Guid.NewGuid():N}");

    public SpaFallbackTests()
    {
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html><body>fake spa shell</body></html>");

        string absoluteRelativeWebRoot = Path.Combine(repoRoot, relativeWebRoot);
        Directory.CreateDirectory(absoluteRelativeWebRoot);
        File.WriteAllText(Path.Combine(absoluteRelativeWebRoot, "index.html"), $"<html><body>{RelativeMarker}</body></html>");
    }

    public void Dispose()
    {
        Directory.Delete(webRoot, recursive: true);
        Directory.Delete(Path.Combine(repoRoot, relativeWebRoot), recursive: true);
    }

    private static WebApplicationFactory<Program> MakeFactory(string webRootValue) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:FakePlatform"] = "true",
                ["Admin:WebRoot"] = webRootValue,
            }));
        });

    [Fact]
    public async Task Deep_link_falls_back_to_the_built_index_html()
    {
        using WebApplicationFactory<Program> factory = MakeFactory(webRoot);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/stack", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("fake spa shell");
    }

    [Fact]
    public async Task The_bare_api_screen_route_falls_back_to_the_built_index_html()
    {
        using WebApplicationFactory<Program> factory = MakeFactory(webRoot);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("fake spa shell");
    }

    [Fact]
    public async Task An_unknown_api_route_stays_a_404_and_never_falls_back()
    {
        using WebApplicationFactory<Program> factory = MakeFactory(webRoot);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("fake spa shell");
    }

    /// <summary>
    /// The two tests above use an absolute <c>Admin:WebRoot</c>, which
    /// <see cref="Path.GetFullPath(string, string)"/> returns unchanged
    /// regardless of the base path passed alongside it — so they never
    /// actually exercise resolution against the repository root, only
    /// against whatever base path Program.cs happens to pass. This test uses
    /// a *relative* <c>Admin:WebRoot</c> so the resolution only succeeds if
    /// Program.cs resolves it against the repository root rather than
    /// <see cref="Directory.GetCurrentDirectory"/> — the test process's
    /// working directory is the test host's content root, not the repo root
    /// (the assembly runs from under <c>artifacts/bin/...</c>), so the two
    /// differ here and the assertion below is not vacuous.
    /// </summary>
    [Fact]
    public async Task WebRoot_is_resolved_against_the_repository_root_not_the_process_working_directory()
    {
        Directory.GetCurrentDirectory().ShouldNotBe(repoRoot,
            "this test proves nothing about repo-root-relative resolution when the process's working directory already is the repository root");

        using WebApplicationFactory<Program> factory = MakeFactory(relativeWebRoot);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/stack", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain(RelativeMarker);
    }
}
