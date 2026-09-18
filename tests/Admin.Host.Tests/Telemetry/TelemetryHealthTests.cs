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

    private static TelemetryHealthService Service(Func<string, HttpResponseMessage> prometheus) =>
        new(new GrafanaClient(
            new HttpClient(new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/api/datasources"
                ? FakeJson(Datasources)
                : prometheus(Uri.UnescapeDataString(request.RequestUri.Query)))),
            Options.Create(new AdminOptions())));

    [Fact]
    public async Task The_three_dashboard_queries_are_joined_per_service()
    {
        TelemetryHealthService health = Service(query =>
            query.Contains(GoldenSignals.LatencyP99, StringComparison.Ordinal) ? FakeJson(Vector(Series("Catalog.Api", "0.05")))
            : query.Contains(GoldenSignals.ErrorRatio, StringComparison.Ordinal) ? FakeJson(Vector(Series("Catalog.Api", "0.1")))
            : FakeJson(Vector(Series("Catalog.Api", "2"), Series("Ordering.Api", "0"))));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeTrue();
        view.Services.ShouldBe(
        [
            new ServiceSignals("Catalog.Api", 2, 0.1, 0.05),
            new ServiceSignals("Ordering.Api", 0, null, null),
        ]);
    }

    [Fact]
    public async Task One_query_that_fails_makes_the_strip_unreachable_rather_than_partial()
    {
        TelemetryHealthService health = Service(query =>
            query.Contains(GoldenSignals.LatencyP99, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream down") }
                : FakeJson(Vector(Series("Catalog.Api", "1"))));

        TelemetryHealthView view = await health.ReadAsync(Token);

        view.Reachable.ShouldBeFalse();
        view.Error.ShouldNotBeNull().ShouldContain("502");
        view.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_endpoint_answers_the_recordings_in_FakePlatform_mode()
    {
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/health", Token);

        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        JsonElement[] services = [.. view.GetProperty("services").EnumerateArray()];
        services.Select(s => s.GetProperty("service").GetString()).ShouldBe(["Catalog.Api", "Gateway.Api", "Ordering.Api"]);

        // Catalog.Api answered no 5xx, so the ratio has no series for it; Ordering.Api had no traffic.
        services[0].GetProperty("errorRatio").ValueKind.ShouldBe(JsonValueKind.Null);
        services[1].GetProperty("errorRatio").GetDouble().ShouldBe(0.05);
        services[2].GetProperty("latencyP99Seconds").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
