using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Trace;

public sealed class TraceEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Kinds(JsonElement view) =>
        [.. view.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task A_correlation_id_the_recordings_know_returns_its_timeline()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/trace/demo-trace-0001", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        view.GetProperty("correlationId").GetString().ShouldBe("demo-trace-0001");
        view.GetProperty("events").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_timeline_ends_at_the_outbox_with_the_projection_snapshot()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/trace/demo-trace-0001", Token);
        string[] kinds = Kinds(view);

        // The recording is one publish of a product: two inbound requests and the outbox write that
        // ends what this correlation id can see (plan M2). No Publish or Consume can appear here.
        kinds.ShouldContain("HttpIn");
        kinds.ShouldContain("Outbox");
        kinds.ShouldContain("Error");
        kinds[^1].ShouldBe("Queued");
        kinds.ShouldNotContain("Publish");

        JsonElement last = view.GetProperty("events").EnumerateArray().Last();
        last.GetProperty("summary").GetString().ShouldNotBeNull().ShouldContain("the outbox carries no trace context");
    }

    [Fact]
    public async Task The_recorded_lines_carry_two_traces_of_which_Tempo_holds_one()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/trace/demo-trace-0001", Token);

        // The failed first attempt is a second trace; Tempo answers 404 for it, and its Loki line stays.
        view.GetProperty("traceIds").GetArrayLength().ShouldBe(2);
        view.GetProperty("tracesTruncated").GetBoolean().ShouldBeFalse();
        view.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("summary").GetString())
            .ShouldContain(summary => summary!.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_line_echoes_the_correlation_id_that_was_asked_for()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/trace/some-other-id", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        view.GetProperty("correlationId").GetString().ShouldBe("some-other-id");
        view.GetProperty("events").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("a%22b")]
    [InlineData("a%20b")]
    [InlineData("a%7Cb")]
    public async Task A_correlation_id_the_backend_would_not_adopt_is_refused_before_it_reaches_LogQL(string encoded)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/trace/{encoded}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Invalid correlation id");
    }

    [Fact]
    public async Task A_correlation_id_longer_than_the_backend_accepts_is_refused()
    {
        HttpResponseMessage response = await client.GetAsync($"/api/trace/{new string('a', 200)}", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unparseable_window_is_refused_with_the_accepted_form()
    {
        HttpResponseMessage response = await client.GetAsync("/api/trace/demo-trace-0001?window=banana", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Invalid window");
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("24h");
    }

    [Theory]
    [InlineData("?window=2h", "2h")]
    [InlineData("?window=90s", "90s")]
    [InlineData("", "15m")]
    public async Task The_window_is_echoed_back_in_the_form_it_was_accepted_in(string query, string expected)
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/trace/demo-trace-0001{query}", Token);

        view.GetProperty("window").GetString().ShouldBe(expected);
    }
}
