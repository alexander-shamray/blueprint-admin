using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Runbooks;

public sealed class RunbookEndpointTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public RunbookEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_listing_is_the_tools_catalogue_and_names_the_word_a_confirm_takes()
    {
        JsonElement catalogue = await client.GetFromJsonAsync<JsonElement>("/api/runbooks", Token);

        catalogue.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        catalogue.GetProperty("runbooks").EnumerateArray().Select(r => r.GetProperty("runbook").GetString())
            .ShouldBe(["contact-refused", "error-rate", "outbox-growth", "stuck-saga"]);
    }

    [Fact]
    public async Task A_runbook_is_read_from_the_clone_and_one_with_no_file_is_a_404()
    {
        JsonElement text = await client.GetFromJsonAsync<JsonElement>("/api/runbooks/outbox-growth", Token);

        text.GetProperty("markdown").GetString()!.ShouldStartWith("# Runbook");
        (await client.GetAsync("/api/runbooks/stuck-saga", Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/runbooks/Not_A_Name", Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_cause_that_costs_the_stack_is_refused_without_its_confirmation_and_run_with_it()
    {
        JsonElement refused = await CauseAsync("outbox-growth", new { }, HttpStatusCode.BadRequest);
        refused.GetProperty("detail").GetString()!.ShouldContain("confirm: \"cause\"");

        JsonElement job = await CauseAsync("outbox-growth", new { confirm = "cause" }, HttpStatusCode.Accepted);
        (await LinesAsync(job.GetProperty("id").GetString()!)).ShouldContain("game day: cause OK in 0m05s");

        await RestoreAsync("outbox-growth");
    }

    [Fact]
    public async Task A_cause_that_deletes_data_takes_the_runbooks_name_typed()
    {
        await CauseAsync("contact-refused", new { confirm = "cause" }, HttpStatusCode.BadRequest);
        JsonElement job = await CauseAsync("contact-refused", new { confirm = "contact-refused" }, HttpStatusCode.Accepted);
        await LinesAsync(job.GetProperty("id").GetString()!);

        await RestoreAsync("contact-refused");
    }

    [Fact]
    public async Task A_caused_alert_fires_in_the_alert_strip_until_the_restore_takes_it_back()
    {
        (await StateOfAsync("OutboxGrowth")).ShouldBe("inactive");

        JsonElement cause = await CauseAsync("outbox-growth", new { confirm = "cause" }, HttpStatusCode.Accepted);
        await LinesAsync(cause.GetProperty("id").GetString()!);
        (await StateOfAsync("OutboxGrowth")).ShouldBe("firing");

        await RestoreAsync("outbox-growth");
        (await StateOfAsync("OutboxGrowth")).ShouldBe("inactive");
    }

    [Fact]
    public async Task A_second_phase_is_named_by_its_alert_and_an_alert_the_script_does_not_cause_is_refused()
    {
        JsonElement unknown = await CauseAsync("error-rate", new { phase = "Nope", confirm = "cause" }, HttpStatusCode.BadRequest);
        unknown.GetProperty("title").GetString().ShouldBe("No such phase");

        JsonElement job = await CauseAsync("error-rate", new { phase = "ErrorRateService", confirm = "cause" }, HttpStatusCode.Accepted);
        await LinesAsync(job.GetProperty("id").GetString()!);
        (await StateOfAsync("ErrorRateService")).ShouldBe("firing");
        (await StateOfAsync("ErrorRateGateway")).ShouldBe("inactive");

        await RestoreAsync("error-rate");
    }

    [Theory]
    [InlineData("stuck-saga", HttpStatusCode.BadRequest)]
    [InlineData("no-such-runbook", HttpStatusCode.NotFound)]
    [InlineData("--restore", HttpStatusCode.BadRequest)]
    public async Task A_runbook_with_no_script_or_no_listing_is_never_handed_to_the_tool(string name, HttpStatusCode expected)
    {
        await CauseAsync(name, new { confirm = "cause" }, expected);

        HttpResponseMessage restore = await client.PostAsync($"/api/runbooks/{name}/restore", null, Token);
        restore.StatusCode.ShouldBe(expected);
    }

    private async Task<JsonElement> CauseAsync(string name, object body, HttpStatusCode expected)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/runbooks/{name}/cause", body, Token);
        response.StatusCode.ShouldBe(expected);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private async Task RestoreAsync(string name)
    {
        HttpResponseMessage response = await client.PostAsync($"/api/runbooks/{name}/restore", null, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        (await LinesAsync(job.GetProperty("id").GetString()!)).ShouldContain("game day: restore OK in 0m04s");
    }

    private async Task<string?> StateOfAsync(string alert)
    {
        JsonElement strip = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/alerts", Token);

        return strip.GetProperty("loaded").EnumerateArray().Single(r => r.GetProperty("name").GetString() == alert).GetProperty("state").GetString();
    }

    /// <summary>The job's lines once it has exited, so the next job finds the gate free.</summary>
    private async Task<string[]> LinesAsync(string id)
    {
        for (int i = 0; i < 200; i++)
        {
            JsonElement job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", Token);

            if (job.GetProperty("summary").GetProperty("state").GetString() == "Exited")
            {
                return [.. job.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!)];
            }

            await Task.Delay(20, Token);
        }

        throw new TimeoutException($"job {id} did not exit");
    }
}
