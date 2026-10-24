using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Admin.Assistant;
using Admin.Assistant.Protocol;
using Admin.Assistant.Providers;
using Admin.Assistant.Tools;
using Admin.Host.Assistant;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Jobs;
using Admin.Host.Stack;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>
/// The console's reads as the assistant's server tools (AI assistant design §6.2), against FakePlatform, and the redaction
/// gate of §10: every tool's result reaches the provider with a planted secret replaced.
/// </summary>
public sealed partial class AdminReadToolsTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    public static TheoryData<string> Names() => ["stack_status", "firing_alerts", "queue_depths", "recent_logs", "trace_summary", "list_api_operations", "list_runbooks"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolContext Context => new(AssistantRuntime.Operator, "conversation", []);

    [Fact]
    public void The_tools_are_the_reads_of_section_6_2_and_the_read_only_policy_admits_every_one()
    {
        AdminReadTools tools = factory.Services.GetRequiredService<AdminReadTools>();

        tools.Tools.Select(t => t.Name).ShouldBe(Names().Select(n => (string)n.Data), ignoreOrder: true);
        tools.Tools.ShouldAllBe(t => t.Side == ToolSide.Server && t.Annotations.ReadOnlyHint && !t.Annotations.DestructiveHint);
        Should.NotThrow(() => ToolRegistry.Build([tools], [], ToolPolicy.ReadOnly));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Each_read_answers_against_FakePlatform_as_json(string name)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        await Prepare(host, name);

        ToolOutcome outcome = await Tools(host).InvokeAsync(name, Input(name), Context, Ct);

        outcome.Ok.ShouldBeTrue(outcome.Content);
        JsonNode.Parse(outcome.Content).ShouldNotBeNull();
    }

    [Fact]
    public async Task Recent_logs_with_no_follow_started_says_so_and_starts_none()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });

        ToolOutcome outcome = await Tools(host).InvokeAsync("recent_logs", [], Context, Ct);

        outcome.Ok.ShouldBeTrue();
        outcome.Content.ShouldContain("No log follow has been started");
        host.Services.GetRequiredService<LogFollower>().Current.ShouldBeNull();
    }

    [Fact]
    public async Task Recent_logs_returns_the_follows_latest_lines_filtered_and_bounded()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        Job follow = await FollowAsync(host);
        string word = follow.Tail(1)[0].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

        JsonNode two = JsonNode.Parse((await Tools(host).InvokeAsync("recent_logs", new JsonObject { ["lines"] = 2 }, Context, Ct)).Content)!;
        JsonNode filtered = JsonNode.Parse((await Tools(host).InvokeAsync("recent_logs", new JsonObject { ["filter"] = word.ToUpperInvariant() }, Context, Ct)).Content)!;

        two["follow"]!["id"]!.GetValue<string>().ShouldBe(follow.Id);
        two["lines"]!.AsArray().Count.ShouldBeInRange(1, 2);
        filtered["lines"]!.AsArray().ShouldNotBeEmpty();
        filtered["lines"]!.AsArray().ShouldAllBe(l => l!["text"]!.GetValue<string>().Contains(word, StringComparison.OrdinalIgnoreCase));
        host.Services.GetRequiredService<LogFollower>().Current.ShouldBeSameAs(follow);
    }

    [Theory]
    [InlineData("""{"lines":0}""")]
    [InlineData("""{"lines":201}""")]
    [InlineData("""{"lines":"ten"}""")]
    [InlineData("""{"filter":5}""")]
    public async Task Recent_logs_refuses_input_outside_its_schema(string input)
    {
        ToolOutcome outcome = await factory.Services.GetRequiredService<AdminReadTools>()
            .InvokeAsync("recent_logs", JsonNode.Parse(input)!.AsObject(), Context, Ct);

        outcome.Ok.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"correlationId":"a\"b"}""")]
    [InlineData("""{"correlationId":"fake-corr-0001","window":"forever"}""")]
    public async Task Trace_summary_refuses_what_the_trace_endpoint_refuses(string input)
    {
        ToolOutcome outcome = await factory.Services.GetRequiredService<AdminReadTools>()
            .InvokeAsync("trace_summary", JsonNode.Parse(input)!.AsObject(), Context, Ct);

        outcome.Ok.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_read_reaches_the_provider_with_a_planted_secret_replaced(string name)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        await Prepare(host, name);
        string raw = (await Tools(host).InvokeAsync(name, Input(name), Context, Ct)).Content;

        // A value this read really returns, which the scrubber's own rules leave alone, becomes a secret the host knows.
        string planted = PlantableWord(FixtureScrubber.Scrub(raw));
        host.Services.GetRequiredService<IOptions<AdminOptions>>().Value.SqlPassword = planted;

        AssistantCore core = new(
            new CallsOnce(name, Input(name)),
            [Tools(host)],
            ToolPolicy.ReadOnly,
            host.Services.GetRequiredService<AssistantRuntime>().Core!.Redactor,
            "",
            new AssistantLimits(),
            TimeProvider.System);
        AssistantSession session = core.Open(AssistantRuntime.Operator, new OpenConversation(new ClientInfo("test", "1"), "en-US", [], [], null))!;
        session.TryStart(new StartTurn("read it", null, null, null)).ShouldBeTrue();
        await Ended(session);

        ToolResult sent = session.History.OfType<ToolResultsMessage>().Single().Results.Single();
        sent.Ok.ShouldBeTrue(sent.Content);
        sent.Content.ShouldNotContain(planted);
        sent.Content.ShouldContain(FixtureScrubber.Scrubbed);
    }

    private static AdminReadTools Tools(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<AdminReadTools>();

    private static JsonObject Input(string name) =>
        name == "trace_summary" ? new JsonObject { ["correlationId"] = "fake-corr-0001" } : [];

    private static async Task Prepare(WebApplicationFactory<Program> host, string name)
    {
        if (name == "recent_logs")
        {
            await FollowAsync(host);
        }
    }

    // POST /api/logs/follow, as the Logs screen starts one, then the fake's first lines.
    private static async Task<Job> FollowAsync(WebApplicationFactory<Program> host)
    {
        using HttpResponseMessage started = await host.CreateClient().PostAsJsonAsync("/api/logs/follow", new { services = Array.Empty<string>() }, Ct);
        started.EnsureSuccessStatusCode();
        Job job = host.Services.GetRequiredService<LogFollower>().Current!;

        for (int i = 0; i < 200 && job.Tail(2).Count < 2; i++)
        {
            await Task.Delay(25, Ct);
        }

        job.Tail(2).Count.ShouldBe(2, "the fake follow wrote no lines");

        return job;
    }

    private static async Task Ended(AssistantSession session)
    {
        for (int i = 0; i < 400 && session.Running; i++)
        {
            await Task.Delay(25, Ct);
        }

        session.Running.ShouldBeFalse("the turn did not end");
    }

    // The longest word of six or more characters inside one of the result's string values, never a property name.
    private static string PlantableWord(string json)
    {
        string? word = Strings(JsonNode.Parse(json))
            .SelectMany(s => Word().Matches(s))
            .Select(w => w.Value)
            .OrderByDescending(w => w.Length)
            .ThenBy(w => w, StringComparer.Ordinal)
            .FirstOrDefault();

        word.ShouldNotBeNull($"the read returned no value to plant: {json}");

        return word;
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue(out string? s) => [s],
        _ => [],
    };

    [GeneratedRegex("[A-Za-z][A-Za-z0-9_-]{5,}")]
    private static partial Regex Word();

    /// <summary>A provider that asks for one tool, then ends the turn when its result comes back.</summary>
    private sealed class CallsOnce(string name, JsonObject input) : IAssistantProvider
    {
        public string Id => "calls-once";

        public ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ProviderStatus(true, "test", null));

        public async IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (request.History[^1] is not ToolResultsMessage)
            {
                yield return new ToolCall("call-1", name, (JsonObject)input.DeepClone());
            }

            yield return new UsageReport(new Usage(1, 1, 0));
            yield return new RoundEnd(ProviderStop.Done);
        }
    }
}
