using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly AdminHostFactory factory;
    private readonly HttpClient client;

    public JobEndpointTests(AdminHostFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    private FakeProcessRunner Runner => factory.Services.GetRequiredService<FakeProcessRunner>();

    [Fact]
    public async Task Lists_jobs_and_shows_one_with_its_tail()
    {
        Job job = Runner.On("echo", "hello", 0, "hello").Start(new ProcessSpec("echo", ["hello"], "/"));

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/jobs", TestContext.Current.CancellationToken);
        JsonElement one = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.Id}", TestContext.Current.CancellationToken);

        list.EnumerateArray().Select(j => j.GetProperty("id").GetString()).ShouldContain(job.Id);
        one.GetProperty("summary").GetProperty("commandLine").GetString().ShouldBe("echo hello");
        one.GetProperty("summary").GetProperty("state").GetString().ShouldBe("Exited");
        one.GetProperty("lines").EnumerateArray().Single().GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task An_unknown_job_is_a_404_problem()
    {
        HttpResponseMessage response = await client.GetAsync("/api/jobs/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Streams_buffered_lines_then_live_lines_then_exit()
    {
        Job job = Runner.OnLongRunning("tail", "x", "first").Start(new ProcessSpec("tail", ["x"], "/"));

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/jobs/{job.Id}/stream");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        job.Append(OutputStream.Stdout, "second");
        job.MarkExited(0);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldContain("event: line");
        body.ShouldContain("id: 0");
        body.ShouldContain("\"text\":\"first\"");
        body.ShouldContain("\"stream\":\"Stdout\"");
        body.ShouldContain("id: 1");
        body.ShouldContain("\"text\":\"second\"");
        body.ShouldContain("event: exited");
        body.ShouldContain("\"exitCode\":0");
    }

    [Fact]
    public async Task Resumes_after_the_given_sequence()
    {
        Job job = Runner.On("echo", "three", 0, "a", "b", "c").Start(new ProcessSpec("echo", ["three"], "/"));

        string body = await client.GetStringAsync($"/api/jobs/{job.Id}/stream?after=1", TestContext.Current.CancellationToken);

        body.ShouldNotContain("\"text\":\"a\"");
        body.ShouldNotContain("\"text\":\"b\"");
        body.ShouldContain("\"text\":\"c\"");
    }

    [Fact]
    public async Task Resumes_from_the_last_event_id_header()
    {
        Job job = Runner.On("echo", "three", 0, "a", "b", "c").Start(new ProcessSpec("echo", ["three"], "/"));
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/jobs/{job.Id}/stream");
        request.Headers.Add("Last-Event-ID", "0");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldNotContain("\"text\":\"a\"");
        body.ShouldContain("\"text\":\"b\"");
    }

    [Fact]
    public async Task The_last_event_id_header_wins_over_the_after_query_on_reconnect()
    {
        // EventSource reconnects to the URL it was opened with and adds Last-Event-ID,
        // so a stale ?after must not replay lines the browser has already seen.
        Job job = Runner.On("echo", "three", 0, "a", "b", "c").Start(new ProcessSpec("echo", ["three"], "/"));
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/jobs/{job.Id}/stream?after=0");
        request.Headers.Add("Last-Event-ID", "1");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldNotContain("\"text\":\"a\"");
        body.ShouldNotContain("\"text\":\"b\"");
        body.ShouldContain("\"text\":\"c\"");
    }
}
