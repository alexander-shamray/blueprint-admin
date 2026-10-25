using System.Net;
using Admin.Mcp;
using Shouldly;

namespace Admin.Host.Tests.Mcp;

/// <summary>The MCP process's HTTP client against answers a real host gives on a bad day (AI assistant design §12).</summary>
public sealed class HttpAdminHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpAdminHost Host(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) =>
        new(new HttpClient(new Handler(answer)) { BaseAddress = new Uri("http://127.0.0.1:5300") });

    private static Task<HttpResponseMessage> Html() =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!doctype html><html></html>", System.Text.Encoding.UTF8, "text/html") });

    [Fact]
    public async Task A_catalogue_path_answered_with_the_index_page_is_a_missing_build_not_a_crash()
    {
        HostUnavailableException error = await Should.ThrowAsync<HostUnavailableException>(() => Host(_ => Html()).ReadCatalogueAsync(Ct));

        error.Message.ShouldContain("npm run build");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }
}
