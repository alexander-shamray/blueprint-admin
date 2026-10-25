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

    [Fact]
    public async Task A_200_that_is_not_the_JSON_expected_is_an_unavailable_host_not_a_crash()
    {
        await Should.ThrowAsync<HostUnavailableException>(() => Host(_ => Html()).ListToolsAsync(Ct));
        await Should.ThrowAsync<HostUnavailableException>(() => Host(_ => Html()).CallToolAsync("stack_status", [], Ct));
    }

    [Fact]
    public async Task A_404_that_is_not_a_problem_document_is_the_assistant_being_off_not_a_crash()
    {
        HttpAdminHost host = Host(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<h1>404</h1>", System.Text.Encoding.UTF8, "text/html") }));

        HostUnavailableException error = await Should.ThrowAsync<HostUnavailableException>(() => host.CallToolAsync("stack_status", [], Ct));

        error.Message.ShouldContain("assistant is off");
    }

    [Fact]
    public async Task The_clients_own_timeout_is_an_unavailable_host_while_the_callers_cancellation_still_cancels()
    {
        HttpAdminHost slow = Host(_ => throw new TaskCanceledException("timed out"));

        HostUnavailableException error = await Should.ThrowAsync<HostUnavailableException>(() => slow.ListToolsAsync(Ct));
        error.Message.ShouldContain("did not answer in time");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<TaskCanceledException>(() => slow.ListToolsAsync(cancelled.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }
}
