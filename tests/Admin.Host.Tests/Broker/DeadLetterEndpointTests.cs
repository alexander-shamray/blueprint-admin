using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Broker;

public sealed class DeadLetterEndpointTests : IClassFixture<AdminHostFactory>
{
    private const string Queue = "ordering-catalog-events_error";

    private readonly HttpClient client;

    public DeadLetterEndpointTests(AdminHostFactory factory)
    {
        client = factory.CreateClient();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plan_names_the_confirmation_and_how_many_messages_a_read_takes()
    {
        JsonElement plan = await client.GetFromJsonAsync<JsonElement>("/api/broker/dead-letters", Token);

        plan.GetProperty("executeConfirm").GetString().ShouldBe("--execute");
        plan.GetProperty("inspectLimit").GetInt32().ShouldBe(20);
        plan.GetProperty("recordDir").GetString()!.ShouldEndWith("dead-letters");
    }

    [Fact]
    public async Task An_error_queue_opens_into_the_message_the_fake_tool_holds()
    {
        JsonElement inspection = await client.GetFromJsonAsync<JsonElement>($"/api/broker/dead-letters/{Queue}", Token);

        inspection.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        inspection.GetProperty("endpoint").GetString().ShouldBe("ordering-catalog-events");
        JsonElement message = inspection.GetProperty("messages").EnumerateArray().ShouldHaveSingleItem();
        message.GetProperty("messageId").GetString().ShouldBe(DeadLetterRecordings.FaultedId);
        message.GetProperty("correlationId").GetString().ShouldBe("fake-corr-0002");
        message.GetProperty("faultMessage").GetString()!.ShouldContain("no row in the price projection");
    }

    [Theory]
    [InlineData("ordering-catalog-events")]
    [InlineData("Not_error")]
    public async Task A_queue_that_is_not_parked_is_refused_before_anything_runs(string queue)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/broker/dead-letters/{queue}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_dry_run_is_a_job_that_says_what_would_move()
    {
        JsonElement job = await MoveAsync("replay", new { messageIds = new[] { DeadLetterRecordings.FaultedId }, execute = false }, HttpStatusCode.Accepted);

        string[] lines = await LinesAsync(job.GetProperty("id").GetString()!);
        lines.ShouldContain(l => l.StartsWith("would-replay", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("dry run: nothing left", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_real_run_is_refused_without_the_typed_confirmation()
    {
        JsonElement problem = await MoveAsync("discard", new { messageIds = new[] { DeadLetterRecordings.FaultedId }, execute = true }, HttpStatusCode.BadRequest);

        problem.GetProperty("detail").GetString()!.ShouldContain("confirm: \"--execute\"");
    }

    [Fact]
    public async Task The_real_run_with_the_confirmation_moves_the_message()
    {
        JsonElement job = await MoveAsync(
            "discard", new { messageIds = new[] { DeadLetterRecordings.FaultedId }, execute = true, confirm = "--execute" }, HttpStatusCode.Accepted);

        string[] lines = await LinesAsync(job.GetProperty("id").GetString()!);
        lines.ShouldContain(l => l.StartsWith("discarded", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("--execute --record", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("replay", """{"messageIds": [], "execute": false}""")]
    [InlineData("replay", """{"messageIds": ["--all"], "execute": false}""")]
    [InlineData("purge", """{"messageIds": ["0199b2a0-5c1d-7e2f-8a3b-4c5d6e7f8a90"], "execute": false}""")]
    [InlineData("Replay", """{"messageIds": ["0199b2a0-5c1d-7e2f-8a3b-4c5d6e7f8a90"], "execute": false}""")]
    public async Task A_move_with_no_message_a_flag_for_an_id_or_another_verb_is_refused(string verb, string body)
    {
        HttpResponseMessage response = await client.PostAsync(
            $"/api/broker/dead-letters/{Queue}/{verb}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JsonElement> MoveAsync(string verb, object body, HttpStatusCode expected)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/broker/dead-letters/{Queue}/{verb}", body, Token);
        response.StatusCode.ShouldBe(expected);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>The job's lines once it has exited, so the next test finds the operator free.</summary>
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
