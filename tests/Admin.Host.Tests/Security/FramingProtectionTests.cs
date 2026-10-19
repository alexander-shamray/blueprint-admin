using System.Net;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Admin.Host.Tests.Security;

public sealed class FramingProtectionTests : IClassFixture<AdminHostFactory>
{
    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public FramingProtectionTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private static void ForbidsFraming(HttpResponseMessage response)
    {
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["frame-ancestors 'none'"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/no-such-endpoint")]
    public async Task Every_api_response_forbids_framing_the_console(string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        ForbidsFraming(response);
    }

    [Fact]
    public async Task A_refused_cross_site_request_forbids_framing_too()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/stack/backend/up");
        request.Headers.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ForbidsFraming(response);
    }

    /// <summary>
    /// With a build present the SPA is served by the static files and by a short-circuited fallback endpoint, which runs inside
    /// routing and before the middleware: each must carry the headers, a deep link included.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/jobs")]
    [InlineData("/stack/frontend")]
    public async Task The_served_spa_forbids_framing_the_console(string path)
    {
        string webRoot = Directory.CreateTempSubdirectory("admin-webroot-").FullName;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>console</title>", TestContext.Current.CancellationToken);
            using WebApplicationFactory<Program> built = factory.WithWebHostBuilder(builder => builder.UseSetting("Admin:WebRoot", webRoot));
            using HttpClient builtClient = built.CreateClient();

            using HttpResponseMessage response = await builtClient.GetAsync(path, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("<title>console</title>");
            ForbidsFraming(response);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
