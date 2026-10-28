using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Compose;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Jobs;

public sealed class JobComposeProgressTests : IClassFixture<AdminHostFactory>
{
    private const string Healthy = """{"id":"Container commerce-sql-1","status":"Done","text":"Healthy"}""";

    private readonly AdminHostFactory factory;

    public JobComposeProgressTests(AdminHostFactory factory) => this.factory = factory;

    private static Job NewJob(int capacity) =>
        new("job-1", new ProcessSpec("docker", ["compose", "up"], "/tmp"), new FakeTimeProvider(), capacity);

    [Fact]
    public void A_json_line_reads_in_the_plain_form_compose_wrote_before_the_flag()
    {
        ComposeJsonLine.Plain(Healthy).ShouldBe(" Container commerce-sql-1  Healthy");
        ComposeJsonLine.Plain("""{"id":"abc","parent_id":"Image x","status":"Working","text":"Downloading","details":"8.389MB"}""")
            .ShouldBe(" abc  Downloading 8.389MB");
        ComposeJsonLine.Plain("""{"id":"abc","status":"Done","text":"Pull complete","details":"0B"}""").ShouldBe(" abc  Pull complete");
        ComposeJsonLine.Plain("""{"error":true,"message":"pull access denied"}""").ShouldBe("pull access denied");
    }

    [Theory]
    [InlineData("== k6 (job j-1)")]
    [InlineData("{not json")]
    [InlineData("""{"level":"info"}""")]
    [InlineData("  Container commerce-sql-1  Healthy")]
    public void A_line_that_is_not_compose_json_is_left_as_it_is(string text) => ComposeJsonLine.Plain(text).ShouldBe(text);

    [Fact]
    public async Task The_job_read_returns_compose_json_in_its_plain_form()
    {
        FakeProcessRunner runner = factory.Services.GetRequiredService<FakeProcessRunner>();
        Job job = runner.On("docker", "compose up", 0, Healthy).Start(new ProcessSpec("docker", ["compose", "up"], "/"));
        HttpClient client = factory.CreateClient();

        JsonElement one = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.Id}", TestContext.Current.CancellationToken);

        one.GetProperty("lines").EnumerateArray().Single().GetProperty("text").GetString().ShouldBe(" Container commerce-sql-1  Healthy");
    }

    [Fact]
    public async Task A_follower_from_the_start_gets_each_resources_last_line_that_left_the_ring()
    {
        Job job = NewJob(capacity: 4);
        job.Append(OutputStream.Stdout, """{"id":"layer-a","parent_id":"Image x","status":"Done","text":"Pull complete"}""");
        job.Append(OutputStream.Stdout, """{"id":"layer-b","parent_id":"Image x","status":"Working","text":"Downloading","current":1,"total":9}""");
        job.Append(OutputStream.Stdout, """{"id":"layer-b","parent_id":"Image x","status":"Done","text":"Pull complete"}""");

        for (int i = 0; i < 5; i++)
        {
            job.Append(OutputStream.Stdout, $"plain {i}");
        }

        job.MarkExited(0);

        List<OutputLine> lines = [];

        await foreach (OutputLine line in job.Follow(-1, TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        lines.Select(l => l.Sequence).ShouldBe([0L, 2L, 4L, 5L, 6L, 7L]);
        lines[1].Text.ShouldContain("Pull complete");
    }

    [Fact]
    public async Task A_follower_resuming_past_the_evicted_lines_is_not_sent_them_again()
    {
        Job job = NewJob(capacity: 2);
        job.Append(OutputStream.Stdout, Healthy);
        job.Append(OutputStream.Stdout, "one");
        job.Append(OutputStream.Stdout, "two");
        job.MarkExited(0);

        List<long> sequences = [];

        await foreach (OutputLine line in job.Follow(0, TestContext.Current.CancellationToken))
        {
            sequences.Add(line.Sequence);
        }

        sequences.ShouldBe([1L, 2L]);
    }
}
