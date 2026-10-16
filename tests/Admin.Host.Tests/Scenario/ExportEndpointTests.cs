using System.Net;
using System.Net.Http.Json;
using Admin.Host.Scenario;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Scenario;

public sealed class ExportEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_comes_back_as_markdown_with_a_secret_in_it_scrubbed()
    {
        const string Document = "# Run\n\n```json\n{\"password\":\"hunter2\",\"id\":\"42\"}\n```\n";

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/scenario/export", new ScenarioExportRequest(Document), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/markdown");
        string body = await response.Content.ReadAsStringAsync(Token);
        body.ShouldNotContain("hunter2");
        body.ShouldContain("<scrubbed>");
        body.ShouldContain("\"id\":\"42\"");
    }

    [Fact]
    public async Task A_document_with_nothing_to_scrub_comes_back_unchanged()
    {
        const string Document = "# Run\n\nThe order read shows confirmed.\n";

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/scenario/export", new ScenarioExportRequest(Document), Token);

        (await response.Content.ReadAsStringAsync(Token)).ShouldBe(Document);
    }

    [Fact]
    public async Task No_document_is_a_400()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/scenario/export", new ScenarioExportRequest(null), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain("Nothing to export");
    }

    [Fact]
    public async Task A_document_past_the_cap_is_a_413()
    {
        string huge = new('a', ExportEndpoints.MaxCharacters + 1);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/scenario/export", new ScenarioExportRequest(huge), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
