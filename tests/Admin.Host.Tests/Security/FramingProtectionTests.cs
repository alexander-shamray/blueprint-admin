using System.Net;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Security;

public sealed class FramingProtectionTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public FramingProtectionTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/no-such-endpoint")]
    [InlineData("/")]
    public async Task Every_response_forbids_framing_the_console(string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["frame-ancestors 'none'"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task A_refused_cross_site_request_forbids_framing_too()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/stack/backend/up");
        request.Headers.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
    }
}
