using System.Net;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Security;

public sealed class LoopbackOriginGuardTests : IClassFixture<AdminHostFactory>
{
    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public LoopbackOriginGuardTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private FakeProcessRunner Runner => factory.Services.GetRequiredService<FakeProcessRunner>();

    [Fact]
    public async Task A_post_from_a_foreign_origin_is_forbidden_and_starts_no_job()
    {
        int startedBefore = Runner.Started.Count;
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/stack/backend/up");
        request.Headers.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Runner.Started.Count.ShouldBe(startedBefore);
    }

    [Fact]
    public async Task A_cross_site_fetch_is_forbidden_and_starts_no_job()
    {
        int startedBefore = Runner.Started.Count;
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/stack/backend/up");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Runner.Started.Count.ShouldBe(startedBefore);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5301")]
    [InlineData("http://localhost:5300")]
    [InlineData("http://[::1]:5300")]
    public async Task A_post_from_a_loopback_origin_is_accepted(string origin)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/stack/backend/up");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_request_for_a_foreign_host_name_is_rejected_by_host_filtering()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/config");
        request.Headers.Host = "evil.example";

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Allowed_hosts_are_the_loopback_names_only()
    {
        factory.Services.GetRequiredService<IConfiguration>()["AllowedHosts"].ShouldBe("127.0.0.1;localhost;[::1]");
    }
}
