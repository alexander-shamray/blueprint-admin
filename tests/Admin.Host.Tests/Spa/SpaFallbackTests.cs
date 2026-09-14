using System.Net;
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
    private readonly string webRoot = Directory.CreateTempSubdirectory("admin-host-spa-").FullName;

    public SpaFallbackTests()
    {
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html><body>fake spa shell</body></html>");
    }

    public void Dispose() => Directory.Delete(webRoot, recursive: true);

    private WebApplicationFactory<Program> MakeFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:FakePlatform"] = "true",
            ["Admin:WebRoot"] = webRoot,
        }));
    });

    [Fact]
    public async Task Deep_link_falls_back_to_the_built_index_html()
    {
        using WebApplicationFactory<Program> factory = MakeFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/stack", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("fake spa shell");
    }

    [Fact]
    public async Task An_unknown_api_route_stays_a_404_and_never_falls_back()
    {
        using WebApplicationFactory<Program> factory = MakeFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("fake spa shell");
    }
}
