using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Telemetry;

public sealed class AlertStripTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Datasources = """[{"uid":"loki","type":"loki"},{"uid":"tempo","type":"tempo"},{"uid":"prometheus","type":"prometheus"}]""";

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Rule(string name, string state, string severity, int alerts = 0, string type = "alerting") =>
        $$"""
        {"state":"{{state}}","name":"{{name}}","query":"up == 0","duration":300,"labels":{"severity":"{{severity}}"},
         "annotations":{"summary":"{{name}} summary"},"alerts":[{{string.Join(',', Enumerable.Repeat("{}", alerts))}}],
         "health":"ok","type":"{{type}}"}
        """;

    private static string Groups(params string[] rules) =>
        $$$"""{"status":"success","data":{"groups":[{"name":"g","file":"f","rules":[{{{string.Join(',', rules)}}}]}]}}""";

    private static GrafanaClient Client(Func<HttpRequestMessage, HttpResponseMessage> prometheus) =>
        new(
            new HttpClient(new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/api/datasources" ? Json(Datasources) : prometheus(request))),
            Options.Create(new AdminOptions()));

    [Fact]
    public async Task Loaded_rules_are_read_with_their_state_severity_and_instance_count()
    {
        GrafanaClient client = Client(_ => Json(Groups(Rule("Latency", "pending", "page", 1), Rule("OutboxGrowth", "inactive", "ticket"))));

        RulesResult result = await client.RulesAsync(Token);

        result.Reachable.ShouldBeTrue();
        result.Rules.ShouldBe(
        [
            new AlertRule("Latency", "g", "pending", "ok", "page", "Latency summary", 1),
            new AlertRule("OutboxGrowth", "g", "inactive", "ok", "ticket", "OutboxGrowth summary", 0),
        ]);
    }

    [Fact]
    public async Task A_recording_rule_is_not_an_alert()
    {
        GrafanaClient client = Client(_ => Json(Groups(Rule("job:up:sum", "inactive", "none", type: "recording"), Rule("Latency", "inactive", "page"))));

        RulesResult result = await client.RulesAsync(Token);

        result.Rules.Select(r => r.Name).ShouldBe(["Latency"]);
    }

    [Fact]
    public async Task The_read_asks_the_prometheus_datasource_for_alerting_rules()
    {
        Uri? asked = null;
        GrafanaClient client = Client(request =>
        {
            asked = request.RequestUri;
            return Json(Groups());
        });

        await client.RulesAsync(Token);

        asked!.AbsolutePath.ShouldBe("/api/datasources/proxy/uid/prometheus/api/v1/rules");
        asked.Query.ShouldBe("?type=alert");
    }

    [Fact]
    public async Task A_prometheus_with_no_rules_is_reachable_and_empty_not_unreachable()
    {
        RulesResult result = await Client(_ => Json("""{"status":"success","data":{"groups":[]}}""")).RulesAsync(Token);

        result.Reachable.ShouldBeTrue();
        result.Rules.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_a_malformed_answer_and_a_missing_datasource_are_each_a_state()
    {
        (await Client(_ => Json("nope", HttpStatusCode.BadGateway)).RulesAsync(Token)).Error!.ShouldContain("502");
        (await Client(_ => Json("""{"data":{}}""")).RulesAsync(Token)).Reachable.ShouldBeFalse();

        GrafanaClient noPrometheus = new(
            new HttpClient(new ScriptedHandler(_ => Json("""[{"uid":"loki","type":"loki"}]"""))),
            Options.Create(new AdminOptions()));
        RulesResult missing = await noPrometheus.RulesAsync(Token);

        missing.Reachable.ShouldBeFalse();
        missing.Rules.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_strip_puts_firing_first_then_pending_then_quiet_and_keeps_the_waiting_list_apart()
    {
        AlertStripService service = new(Client(_ => Json(Groups(
            Rule("B", "inactive", "page"),
            Rule("A", "inactive", "page"),
            Rule("Z", "firing", "page", 2),
            Rule("M", "pending", "ticket", 1)))));

        AlertStripView view = await service.ReadAsync(Token);

        view.Loaded.Select(r => r.Name).ShouldBe(["Z", "M", "A", "B"]);
        view.Waiting.ShouldBe(WaitingAlerts.Table);
        view.Loaded.Select(r => r.Name).Intersect(view.Waiting.Select(w => w.Name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_prometheus_still_lists_the_waiting_rules()
    {
        AlertStripService service = new(Client(_ => Json("down", HttpStatusCode.ServiceUnavailable)));

        AlertStripView view = await service.ReadAsync(Token);

        view.Reachable.ShouldBeFalse();
        view.Loaded.ShouldBeEmpty();
        view.Waiting.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_endpoint_answers_firing_pending_quiet_and_waiting_rules_in_FakePlatform_mode()
    {
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/alerts", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        Dictionary<string, string> loaded = view.GetProperty("loaded").EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("state").GetString()!);
        loaded["ErrorQueueDepth"].ShouldBe("firing");
        loaded["Latency"].ShouldBe("pending");
        loaded["ErrorRateGateway"].ShouldBe("inactive");
        view.GetProperty("loaded")[0].GetProperty("name").GetString().ShouldBe("ErrorQueueDepth");
        view.GetProperty("waiting").EnumerateArray().Select(w => w.GetProperty("name").GetString())
            .ShouldBe(WaitingAlerts.Table.Select(w => w.Name));
    }
}
