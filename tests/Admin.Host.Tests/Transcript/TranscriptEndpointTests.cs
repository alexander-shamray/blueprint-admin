using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Transcript;

public sealed class TranscriptEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task What_the_operator_did_against_the_fakes_is_transcribed_and_what_the_page_polled_is_not()
    {
        (await client.PostAsync("/api/stack/backend/up", null, Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/api/proxy", new { method = "GET", url = "http://localhost:5000/api/v1/catalog/products", identity = new { username = "demo" } }, Token))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/stack", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/transcript", Token);
        JsonElement[] entries = [.. view.GetProperty("entries").EnumerateArray()];
        string[] commands = [.. entries.Select(e => e.GetProperty("command").GetString()!)];

        commands.ShouldContain(c => c.StartsWith("docker compose -f ", StringComparison.Ordinal) && c.EndsWith(" up -d --wait", StringComparison.Ordinal));
        commands.ShouldNotContain(c => c.Contains(" ps ", StringComparison.Ordinal));
        // The class shares one host, so the other test's request may be here too.
        JsonElement request = entries.Single(e =>
            e.GetProperty("kind").GetString() == "Request" && e.GetProperty("command").GetString()!.Contains("/catalog/products", StringComparison.Ordinal));
        request.GetProperty("identity").GetString().ShouldBe("demo");
        request.GetProperty("command").GetString()!.ShouldContain("-H 'Authorization: Bearer <scrubbed>'");
    }

    [Fact]
    public async Task The_script_is_plain_text_and_every_entry_is_clean_by_the_fixture_gates_rules()
    {
        await client.PostAsJsonAsync("/api/proxy", new { method = "GET", url = "http://localhost:5000/api/v1/orders", identity = new { username = "demo", password = "demo" } }, Token);

        HttpResponseMessage response = await client.GetAsync("/api/transcript/script", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        string script = await response.Content.ReadAsStringAsync(Token);
        script.ShouldStartWith("#!/usr/bin/env bash\n");
        script.ShouldContain("curl -i http://localhost:5000/api/v1/orders");
        FixtureScrubber.Findings(script).ShouldBeEmpty();

        // The entries as well as the script: /api/transcript serves each line as the proxy kept it, to the screen.
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/transcript", Token);
        string[] commands = [.. view.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("command").GetString()!)];
        commands.ShouldContain(c => c.Contains("Authorization: Bearer <scrubbed>", StringComparison.Ordinal));
        commands.ShouldAllBe(c => FixtureScrubber.Findings(c).Count == 0);
    }
}
