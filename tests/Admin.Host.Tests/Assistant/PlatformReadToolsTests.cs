using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Admin.Host.Api;
using Admin.Host.Assistant;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Identity;
using Admin.Host.Tests.TestSupport;
using Admin.Host.Transcript;
using Common.Assistant;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Assistant;

/// <summary>
/// The platform's data as the assistant's server tools (AI assistant design §6.5), against FakePlatform: the tools derived
/// from the API catalogue's GET operations, the realm user each read is sent as, and what comes back.
/// </summary>
public sealed class PlatformReadToolsTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private const string StockedProduct = "0199a1b2-0000-7000-8000-00000000000a";

    private const string PlacedOrder = "0199a1b2-0000-7000-8000-000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolContext Context(JsonObject? hostOptions = null) => new(AssistantRuntime.Operator, "conversation", hostOptions ?? []);

    private static async Task<PlatformReadTools> Prepared(WebApplicationFactory<Program> host)
    {
        PlatformReadTools tools = host.Services.GetRequiredService<PlatformReadTools>();
        await tools.PrepareAsync(Ct);

        return tools;
    }

    [Fact]
    public void The_defaults_are_catalog_inventory_ordering_payments_and_the_bff_and_privacy_is_the_operators_choice()
    {
        new AssistantOptions().PlatformReadServices().ShouldBe(["catalog", "inventory", "ordering", "payments", "bff"], ignoreOrder: true);
        new AssistantOptions { PlatformReads = ["Privacy", " catalog "] }.PlatformReadServices().ShouldBe(["privacy", "catalog"], ignoreOrder: true);
        new AssistantOptions { PlatformReads = [] }.PlatformReadServices().ShouldBeEmpty();
    }

    [Fact]
    public void A_service_the_catalogue_does_not_hold_stops_startup_naming_the_key()
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => new AssistantOptions { PlatformReads = ["catalog", "ghost"] }.PlatformReadServices());

        error.Message.ShouldContain("Admin:Assistant:PlatformReads");
        error.Message.ShouldContain("'ghost'");
    }

    [Fact]
    public async Task No_tool_exists_until_the_catalogue_has_been_read_and_then_one_for_each_get_of_the_chosen_services()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = host.Services.GetRequiredService<PlatformReadTools>();
        tools.Tools.ShouldBeEmpty();

        await tools.PrepareAsync(Ct);

        string[] names = [.. tools.Tools.Select(t => t.Name)];
        names.ShouldBe([.. names.Order(StringComparer.Ordinal)]);
        names.ShouldContain("get_stock");
        names.ShouldContain("get_reservation");
        names.ShouldContain("get_payment");
        names.ShouldContain("get_order_saga_state");
        names.ShouldContain("get_product");
        names.ShouldContain("get_products");
        // Nothing that changes anything, whatever the catalogue holds.
        names.ShouldNotContain("publish_product");
        names.ShouldNotContain("place_order");
        names.ShouldNotContain("set_on_hand");
        tools.Tools.ShouldAllBe(t => t.Side == ToolSide.Server && t.Annotations.ReadOnlyHint && !t.Annotations.DestructiveHint);
        Should.NotThrow(() => ToolRegistry.Build([tools], [], ToolPolicy.ReadOnly));
    }

    [Fact]
    public async Task A_tool_takes_the_operations_parameters_and_nothing_else()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        JsonObject schema = tools.Tools.Single(t => t.Name == "get_stock").InputSchema;

        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!.AsObject().Select(p => p.Key).ShouldBe(["productId"]);
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["productId"]);
    }

    [Fact]
    public async Task Privacy_is_offered_only_when_the_operator_names_it()
    {
        using WebApplicationFactory<Program> without = factory.WithWebHostBuilder(_ => { });
        using WebApplicationFactory<Program> with = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Assistant:PlatformReads:0", "privacy"));

        (await Prepared(without)).Tools.ShouldNotContain(t => t.Name.Contains("erasure", StringComparison.Ordinal));
        (await Prepared(with)).Tools.ShouldContain(t => t.Name.Contains("erasure", StringComparison.Ordinal));
        (await Prepared(with)).Tools.ShouldNotContain(t => t.Name == "get_stock");
    }

    [Fact]
    public async Task A_read_is_sent_as_the_first_realm_user_whose_token_holds_the_routes_permission()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(), Ct);

        outcome.Ok.ShouldBeTrue(outcome.Content);
        JsonNode read = JsonNode.Parse(outcome.Content)!;
        read["status"]!.GetValue<int>().ShouldBe(200);
        read["body"]!.GetValue<string>().ShouldContain("\"available\":10");
        read["identity"]!.GetValue<string>().ShouldBe("demo");
        read["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        read["truncated"]!.GetValue<bool>().ShouldBeFalse();
        // Never headers: the answer carries the status, the body, the correlation id and who it was sent as.
        read.AsObject().Select(p => p.Key).Order().ShouldBe(["body", "bodyError", "correlationId", "identity", "status", "truncated"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_read_is_in_the_operator_transcript_as_the_curl_that_repeats_it_marked_as_the_assistants()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);
        OperatorTranscript transcript = host.Services.GetRequiredService<OperatorTranscript>();

        await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(), Ct);

        TranscriptEntry entry = transcript.Read().Entries.Single(e => e.Command.Contains($"/api/v1/inventory/stock/{StockedProduct}", StringComparison.Ordinal));
        entry.Assistant.ShouldBeTrue();
        entry.Kind.ShouldBe(TranscriptKind.Request);
        entry.Identity.ShouldBe("demo");
        entry.Status.ShouldBe(200);
        entry.Command.ShouldStartWith("curl");
        transcript.Script().ShouldContain("asked by the assistant · as demo · HTTP 200");
    }

    [Fact]
    public async Task A_refusal_or_a_missing_thing_comes_back_as_the_platform_sent_it()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        JsonNode missing = JsonNode.Parse((await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = "0199a1b2-0000-7000-8000-0000000000ff" }, Context(), Ct)).Content)!;
        // The user the conversation pins holds no inventory permission: the gateway's 403 is the answer, not a guess.
        ToolOutcome refused = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(new JsonObject { ["identity"] = "privacy" }), Ct);

        missing["status"]!.GetValue<int>().ShouldBe(404);
        refused.Ok.ShouldBeTrue(refused.Content);
        JsonNode.Parse(refused.Content)!["status"]!.GetValue<int>().ShouldBe(403);
        JsonNode.Parse(refused.Content)!["identity"]!.GetValue<string>().ShouldBe("privacy");
    }

    [Fact]
    public async Task A_pinned_user_who_is_not_configured_sends_nothing()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);
        OperatorTranscript transcript = host.Services.GetRequiredService<OperatorTranscript>();

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(new JsonObject { ["identity"] = "mallory" }), Ct);

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldContain("mallory");
        transcript.Read().Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_no_user_holding_the_permission_the_tool_says_so_and_sends_nothing()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Admin:Users:0:Username", "browser");
            b.UseSetting("Admin:Users:0:Password", "browser");
        });
        PlatformReadTools tools = await Prepared(host);
        OperatorTranscript transcript = host.Services.GetRequiredService<OperatorTranscript>();

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(), Ct);

        outcome.Ok.ShouldBeFalse();
        outcome.Content.ShouldContain("inventory:admin");
        transcript.Read().Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"productId":""}""")]
    [InlineData("""{"productId":{"a":1}}""")]
    [InlineData("""{"productId":".."}""")]
    [InlineData("""{"productId":"."}""")]
    [InlineData("""{"productId":"0199a1b2-0000-7000-8000-00000000000a","extra":1}""")]
    public async Task Input_the_operation_does_not_take_is_an_error_the_model_reads_and_sends_nothing(string input)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", JsonNode.Parse(input)!.AsObject(), Context(), Ct);

        outcome.Ok.ShouldBeFalse();
        host.Services.GetRequiredService<OperatorTranscript>().Read().Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_value_cannot_leave_its_path_segment()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = "../payments/x?y=1" }, Context(), Ct);

        outcome.Ok.ShouldBeTrue(outcome.Content);
        host.Services.GetRequiredService<OperatorTranscript>().Read().Entries.Single().Command
            .ShouldContain("/api/v1/inventory/stock/..%2Fpayments%2Fx%3Fy%3D1");
    }

    [Fact]
    public async Task An_anonymous_route_is_read_as_nobody_and_an_authenticated_one_as_the_first_realm_user()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        JsonNode anonymous = JsonNode.Parse((await tools.InvokeAsync("get_products", [], Context(), Ct)).Content)!;
        JsonNode authenticated = JsonNode.Parse((await tools.InvokeAsync("list_orders", [], Context(), Ct)).Content)!;

        anonymous["status"]!.GetValue<int>().ShouldBe(200);
        anonymous["identity"].ShouldBeNull();
        authenticated["identity"]!.GetValue<string>().ShouldBe("demo");
    }

    private static ApiOperation Get(string source, string name, string policy = "anonymous", bool available = true, string method = "GET") =>
        new($"{source}:{name}", source, name, method, $"http://localhost:5000/api/v1/{source}/{name}", [], [], null, false, policy, available);

    private static string[] Listed(ApiOperation[] operations, string[] services, string[]? reserved = null) =>
    [
        .. PlatformReadTools.Snapshot.Of(new ApiCatalogView([], operations), new HashSet<string>(services), new HashSet<string>(reserved ?? []))
            .Tools.Select(t => t.Name),
    ];

    [Fact]
    public void Only_the_chosen_services_available_GETs_are_tools_and_a_name_two_services_share_says_whose_it_is()
    {
        ApiOperation[] operations =
        [
            Get("catalog", "GetItem"),
            Get("inventory", "GetItem"),
            Get("catalog", "PublishItem", method: "POST"),
            Get("catalog", "GetGone", available: false),
            Get("privacy", "GetSubject"),
            Get("payments", "GetPayment"),
        ];

        Listed(operations, ["catalog", "inventory", "payments"]).ShouldBe(["catalog_get_item", "get_payment", "inventory_get_item"]);
    }

    [Fact]
    public void A_get_whose_name_reads_as_an_action_or_is_too_long_or_belongs_to_another_source_is_left_out_not_a_startup_failure()
    {
        ApiOperation[] operations =
        [
            Get("catalog", "ResetCache"),
            Get("catalog", "GetSomethingWithAVeryLongNameThatGoesOnAndOnAndOnPastWhatAToolNameMayHoldInTotal"),
            Get("catalog", "StackStatus"),
            Get("catalog", "GetFine"),
        ];

        Listed(operations, ["catalog"], reserved: ["stack_status"]).ShouldBe(["get_fine"]);
    }

    [Theory]
    [InlineData(5, -1, 100, true)]
    [InlineData(1, -1, 100, true)]
    [InlineData(1, 1_000, 20_000, false)]
    [InlineData(1, 1_000, 31_001, true)]
    [InlineData(0, -1, 100, false)]
    public void A_catalogue_lacking_a_chosen_service_is_asked_for_again_no_oftener_than_every_thirty_seconds_and_the_first_time_at_once(int missing, long last, long now, bool reload)
    {
        PlatformReadTools.ShouldReload(missing, last, now).ShouldBe(reload);
    }

    [Theory]
    [InlineData("""{"identity":{"a":1}}""")]
    [InlineData("""{"identity":["demo"]}""")]
    [InlineData("""{"identity":7}""")]
    public async Task A_pin_that_is_not_a_name_is_refused_and_does_not_fall_back_to_the_first_user(string hostOptions)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        ToolOutcome outcome = await tools.InvokeAsync("get_stock", new JsonObject { ["productId"] = StockedProduct }, Context(JsonNode.Parse(hostOptions)!.AsObject()), Ct);

        outcome.Ok.ShouldBeFalse();
        host.Services.GetRequiredService<OperatorTranscript>().Read().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 11999 ASCII characters then an astral one, so the 12000-character cut would land between its halves.
        string body = new string('a', PlatformReadTools.MaxBodyChars - 1) + "\U0001F600" + "tail";
        ProxyResponded responded = new(200, new Dictionary<string, string[]>(), body, false, null, 5, "corr-2");

        string kept = JsonNode.Parse(PlatformReadTools.Outcome(responded, null).Content)!["body"]!.GetValue<string>();

        kept.Length.ShouldBe(PlatformReadTools.MaxBodyChars - 1);
        char.IsHighSurrogate(kept[^1]).ShouldBeFalse();
    }

    [Fact]
    public void A_body_whose_quotes_double_its_length_is_cut_until_the_whole_result_fits_the_core_bound_with_its_flags_first()
    {
        string quoted = string.Concat(Enumerable.Repeat("{\"a\":\"b\"}", 1_500));
        ProxyResponded responded = new(200, new Dictionary<string, string[]>(), quoted, false, null, 5, "corr-1");

        ToolOutcome outcome = PlatformReadTools.Outcome(responded, "demo");

        outcome.Ok.ShouldBeTrue();
        outcome.Content.Length.ShouldBeLessThanOrEqualTo(PlatformReadTools.MaxResultChars);
        JsonNode read = JsonNode.Parse(outcome.Content)!;
        read["truncated"]!.GetValue<bool>().ShouldBeTrue();
        read["correlationId"]!.GetValue<string>().ShouldBe("corr-1");
        quoted.ShouldStartWith(read["body"]!.GetValue<string>());
        read.AsObject().First().Key.ShouldBe("status");
        read.AsObject().Last().Key.ShouldBe("body");
    }

    [Fact]
    public async Task A_route_the_gateway_does_not_have_is_not_sent_to_whoever_the_conversation_pins()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);
        ApiOperation noRoute = Get("catalog", "GetNowhere", policy: GatewayRoutes.NoRoute);

        (IdentityRequest? identity, string? refusal) = await tools.IdentityAsync(noRoute, Context(new JsonObject { ["identity"] = "demo" }), Ct);

        identity.ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("no route");
    }

    [Fact]
    public void A_service_is_missing_when_the_view_holds_no_available_operation_of_it_however_many_it_holds_that_are_not_tools()
    {
        ApiCatalogView view = new(
            [],
            [
                Get("catalog", "GetItem"),
                Get("inventory", "GetStock", available: false),
                Get("payments", "PublishPayment", method: "POST"),
            ]);

        // Inventory is down; payments holds an operation, only not a GET, so it is not down; ordering was never loaded.
        PlatformReadTools.Missing(view, new HashSet<string> { "catalog", "inventory", "payments", "ordering" }).ShouldBe(2);
    }

    [Fact]
    public async Task A_tool_that_does_not_exist_is_an_error_result_not_an_exception()
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);

        ToolOutcome outcome = await tools.InvokeAsync("delete_product", [], Context(), Ct);

        outcome.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_reaches_the_provider_with_a_planted_secret_replaced()
    {
        // The payment read returns the word "Authorised"; the host knows it as a secret, so it never leaves.
        const string Planted = "Authorised";
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(_ => { });
        PlatformReadTools tools = await Prepared(host);
        host.Services.GetRequiredService<IOptions<AdminOptions>>().Value.SqlPassword = Planted;
        JsonObject input = new() { ["orderId"] = PlacedOrder };
        ((await tools.InvokeAsync("get_payment", input, Context(), Ct)).Content).ShouldContain(Planted);

        AssistantCore core = new(
            new CallsOnce("get_payment", input),
            [tools],
            ToolPolicy.ReadOnly,
            host.Services.GetRequiredService<AssistantRuntime>().Core!.Redactor,
            "",
            new AssistantLimits(),
            TimeProvider.System);
        AssistantSession session = core.Open(AssistantRuntime.Operator, new OpenConversation(new ClientInfo("test", "1"), "en-US", [], [], null))!;
        session.TryStart(new StartTurn("read it", null, null, null)).ShouldBeTrue();

        for (int i = 0; i < 400 && session.Running; i++)
        {
            await Task.Delay(25, Ct);
        }

        session.Running.ShouldBeFalse("the turn did not end");
        ToolResult sent = session.History.OfType<ToolResultsMessage>().Single().Results.Single();
        sent.Ok.ShouldBeTrue(sent.Content);
        sent.Content.ShouldNotContain(Planted);
        sent.Content.ShouldContain(FixtureScrubber.Scrubbed);
    }

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
