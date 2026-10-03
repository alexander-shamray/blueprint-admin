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

public sealed class TelemetryHealthTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Datasources = """[{"uid":"loki","type":"loki"},{"uid":"tempo","type":"tempo"},{"uid":"prometheus","type":"prometheus"}]""";

    private static HttpResponseMessage FakeJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Vector(params string[] series) =>
        $$$"""{"status":"success","data":{"resultType":"vector","result":[{{{string.Join(',', series)}}}]}}""";

    private static string Series(string service, string value) =>
        $$$"""{"metric":{"service_name":"{{{service}}}"},"value":[1789532580,"{{{value}}}"]}""";

    private static string Series(string service, string route, string value) =>
        $$$"""{"metric":{"service_name":"{{{service}}}","http_route":"{{{route}}}"},"value":[1789532580,"{{{value}}}"]}""";

    private static TelemetryHealthService Service(Func<string, HttpResponseMessage> prometheus) =>
        new(new GrafanaClient(
            new HttpClient(new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/api/datasources"
                ? FakeJson(Datasources)
                : prometheus(PromQlIn(request)))),
            Options.Create(new AdminOptions())));

    /// <summary>The query as sent, whole: <c>GrafanaClient.InstantQueryAsync</c> escapes it into the one <c>query</c> parameter.</summary>
    private static string PromQlIn(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.Query["?query=".Length..]);

    /// <summary>
    /// Each query named answers its vector; any other answers an empty one, as Prometheus does for no series.
    /// Matched by exact text: the 401 and 422 queries differ from the request-rate one only inside the selector.
    /// </summary>
    private static Func<string, HttpResponseMessage> Answering(Dictionary<string, string> vectors) =>
        promQl => FakeJson(vectors.GetValueOrDefault(promQl) ?? Vector());

    [Fact]
    public async Task All_seven_dashboard_queries_are_joined_per_service()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.ErrorRatio] = Vector(Series("Catalog.Api", "0.1")),
            [GoldenSignals.LatencyP99] = Vector(Series("Catalog.Api", "0.05")),
            [GoldenSignals.DomainRefusalRate] = Vector(Series("Catalog.Api", "/v1/catalog/products/", "0.5")),
            [GoldenSignals.UnauthorisedRate] = Vector(Series("Catalog.Api", "0.25")),
            [GoldenSignals.CommandP95] = Vector(Series("Catalog.Api", "0.012")),
            [GoldenSignals.QueryP95] = Vector(Series("Catalog.Api", "0.008")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeTrue();
        view.Services.ShouldBe([new ServiceSignals("Catalog.Api", 2, 0.1, 0.05, 0.5, 0.25, 0.012, 0.008)]);
    }

    [Fact]
    public async Task The_422_panel_split_by_route_is_summed_per_service()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Gateway.Api", "3")),
            [GoldenSignals.DomainRefusalRate] = Vector(
                Series("Gateway.Api", "/api/v1/orders/{**catch-all}", "0.5"),
                Series("Gateway.Api", "/api/v1/catalog/{**catch-all}", "0.25")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Services.ShouldHaveSingleItem().DomainRefusalRate.ShouldBe(0.75);
    }

    [Fact]
    public async Task A_service_with_traffic_and_no_5xx_series_has_a_zero_share_not_an_absent_one()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // Catalog.Api served requests and answered no 5xx; Ordering.Api served none, so its share is undefined.
        view.Services.Select(s => s.ErrorRatio).ShouldBe([0, null]);
    }

    [Fact]
    public async Task Query_results_that_disagree_on_traffic_show_no_share_and_no_refusal_rate()
    {
        // Seven instant queries are seven reads, not one snapshot: the ratio and the 401 panel can name a
        // service the request-rate read reported idle, or did not report at all.
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "0")),
            [GoldenSignals.ErrorRatio] = Vector(Series("Catalog.Api", "0.5"), Series("Ordering.Api", "0.25")),
            [GoldenSignals.UnauthorisedRate] = Vector(Series("Ordering.Api", "1")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Services.Select(s => (s.Service, s.ErrorRatio, s.UnauthorisedRate)).ShouldBe<(string, double?, double?)>(
        [
            ("Catalog.Api", null, 0),
            ("Ordering.Api", null, null),
        ]);
    }

    [Fact]
    public async Task A_5xx_share_that_is_present_but_not_finite_stays_absent_even_with_traffic()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.ErrorRatio] = Vector(Series("Catalog.Api", "NaN")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // The zero fill is for a series that is absent; a NaN one is present and unknown.
        view.Services.ShouldHaveSingleItem().ErrorRatio.ShouldBeNull();
    }

    [Fact]
    public async Task A_status_code_with_no_series_is_a_zero_rate_only_for_a_service_the_request_rate_names()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0")),
            [GoldenSignals.CommandP95] = Vector(Series("Shipping.Worker", "0.01")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        // A counter that never recorded a 401 has no series: its rate is zero. Shipping.Worker is named only
        // by the command quantile, so nothing says it served HTTP at all.
        view.Services.Select(s => (s.Service, s.DomainRefusalRate, s.UnauthorisedRate)).ShouldBe<(string, double?, double?)>(
        [
            ("Catalog.Api", 0, 0),
            ("Ordering.Api", 0, 0),
            ("Shipping.Worker", null, null),
        ]);
    }

    [Fact]
    public async Task A_quantile_with_no_series_or_no_finite_value_stays_absent()
    {
        TelemetryHealthService health = Service(Answering(new()
        {
            [GoldenSignals.RequestRate] = Vector(Series("Catalog.Api", "2")),
            [GoldenSignals.LatencyP99] = Vector(Series("Catalog.Api", "NaN")),
            [GoldenSignals.CommandP95] = Vector(Series("Catalog.Api", "NaN")),
        }));

        TelemetryHealthView view = await health.ReadAsync(Token);

        ServiceSignals catalog = view.Services.ShouldHaveSingleItem();
        catalog.LatencyP99Seconds.ShouldBeNull();
        catalog.CommandP95Seconds.ShouldBeNull();
        catalog.QueryP95Seconds.ShouldBeNull();
    }

    [Theory]
    [InlineData(GoldenSignals.RequestRate)]
    [InlineData(GoldenSignals.QueryP95)]
    public async Task One_query_that_fails_makes_the_strip_unreachable_rather_than_partial(string failing)
    {
        TelemetryHealthService health = Service(promQl => promQl == failing
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream down") }
            : FakeJson(Vector(Series("Catalog.Api", "1"))));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("502");
        view.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_datasource_list_is_asked_once_per_read_not_once_per_query()
    {
        // GrafanaClient caches a uid only once resolved and does not gate a resolve in flight, so seven
        // cold queries fanned out together would each ask for the list; a Grafana with no Prometheus, which
        // never resolves, would be asked seven times on every poll.
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/datasources"
            ? FakeJson("""[{"uid":"loki","type":"loki"}]""")
            : FakeJson(Vector()));
        TelemetryHealthService health = new(new GrafanaClient(new HttpClient(handler), Options.Create(new AdminOptions())));

        TelemetryHealthView first = await health.ReadAsync(Token);
        TelemetryHealthView second = await health.ReadAsync(Token);

        first.Reachable.ShouldBeFalse();
        second.Reachable.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.ShouldAllBe(r => r.Request.RequestUri!.AbsolutePath == "/api/datasources");
    }

    [Fact]
    public async Task A_cold_read_that_succeeds_asks_the_datasource_list_once_for_all_seven_queries()
    {
        // The case above never reaches the fan-out. This one does, and the six fanned-out queries must
        // read the uid the first one cached. The list answers late, so that queries started together would
        // all be waiting on it and each ask; a synchronous answer would cache before a second one began.
        // Counted here rather than through ScriptedHandler.Requests, a plain List the fan-out may add to from
        // more than one thread.
        int listRequests = 0;
        int queryRequests = 0;
        TelemetryHealthService health = new(new GrafanaClient(
            new HttpClient(new ScriptedHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/datasources")
                {
                    Interlocked.Increment(ref listRequests);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    return FakeJson(Datasources);
                }

                Interlocked.Increment(ref queryRequests);
                return FakeJson(Vector());
            })),
            Options.Create(new AdminOptions())));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeTrue();
        queryRequests.ShouldBe(typeof(GoldenSignals).GetFields().Length);
        listRequests.ShouldBe(1);
    }

    [Fact]
    public async Task The_endpoint_answers_the_live_recording_in_FakePlatform_mode()
    {
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/health", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        Dictionary<string, JsonElement> services = view.GetProperty("services").EnumerateArray()
            .ToDictionary(s => s.GetProperty("service").GetString()!);
        services.Keys.ShouldBe(
            ["Catalog.Api", "Gateway.Api", "Inventory.Api", "Notifications.Worker", "Ordering.Api", "Payments.Api", "Shipping.Worker", "Web.Bff"],
            ignoreOrder: false);

        // Catalog.Api served requests and answered no 5xx: a zero share, where the ratio itself has no series.
        services["Catalog.Api"].GetProperty("errorRatio").GetDouble().ShouldBe(0);
        // Each query reaches its own recording: a fall-through to the empty vector would null these two.
        services["Notifications.Worker"].GetProperty("errorRatio").GetDouble().ShouldBeGreaterThan(0);
        services["Catalog.Api"].GetProperty("queryP95Seconds").GetDouble().ShouldBeGreaterThan(0);
        // Gateway.Api's 401s and 422s were recorded; its counters never recorded a command or a query.
        services["Gateway.Api"].GetProperty("unauthorisedRate").GetDouble().ShouldBeGreaterThan(0);
        services["Gateway.Api"].GetProperty("domainRefusalRate").GetDouble().ShouldBeGreaterThan(0);
        services["Gateway.Api"].GetProperty("commandP95Seconds").ValueKind.ShouldBe(JsonValueKind.Null);
        // Payments.Api had no HTTP traffic in the window: its share is undefined, its refusals zero.
        services["Payments.Api"].GetProperty("requestRate").GetDouble().ShouldBe(0);
        services["Payments.Api"].GetProperty("errorRatio").ValueKind.ShouldBe(JsonValueKind.Null);
        services["Payments.Api"].GetProperty("domainRefusalRate").GetDouble().ShouldBe(0);
        // Shipping.Worker serves no HTTP the request rate names, so its refusal cells are not vouched for.
        services["Shipping.Worker"].GetProperty("unauthorisedRate").ValueKind.ShouldBe(JsonValueKind.Null);
        services["Shipping.Worker"].GetProperty("commandP95Seconds").GetDouble().ShouldBeGreaterThan(0);
    }
}
