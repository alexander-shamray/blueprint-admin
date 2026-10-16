using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Scenario;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>The fake Loki answers the review-flag query from <see cref="FakeOrders"/>, and every other query as it always did.</summary>
public sealed class FakeGrafanaReviewTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
    private readonly AdminOptions options = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<JsonElement> Query(FakeOrders orders, string logQl)
    {
        using HttpClient http = new(new FakePlatformHandler(options, orders));
        string url = $"{options.GrafanaUrl.TrimEnd('/')}/api/datasources/proxy/uid/loki/loki/api/v1/query_range?query={Uri.EscapeDataString(logQl)}&start=0&end=1&limit=100";

        using HttpResponseMessage response = await http.GetAsync(url, Token);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.Clone();
    }

    [Fact]
    public async Task A_flagged_order_answers_the_line_Ordering_logs_for_it()
    {
        FakeOrders orders = new(time, new FakeStack { ShortWaits = true });
        string id = orders.Place(postalCode: FakeOrders.StrangePostalCode);
        time.Advance(FakeOrders.ShortDespatchWait);

        JsonElement answer = await Query(orders, ReviewFlagService.LogQl(id));

        JsonElement stream = answer.GetProperty("data").GetProperty("result").EnumerateArray().ShouldHaveSingleItem();
        stream.GetProperty("stream").GetProperty("service_name").GetString().ShouldBe("Ordering.Api");
        JsonElement value = stream.GetProperty("values").EnumerateArray().ShouldHaveSingleItem();
        value[0].GetString().ShouldBe((time.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        value[1].GetString().ShouldBe($"Order {id} flagged for review: not_despatched.");
    }

    [Fact]
    public async Task An_order_whose_wait_has_not_lapsed_answers_an_empty_result()
    {
        FakeOrders orders = new(time, new FakeStack { ShortWaits = true });
        string id = orders.Place(postalCode: FakeOrders.StrangePostalCode);

        JsonElement answer = await Query(orders, ReviewFlagService.LogQl(id));

        answer.GetProperty("status").GetString().ShouldBe("success");
        answer.GetProperty("data").GetProperty("result").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Any_other_query_still_answers_the_recording()
    {
        JsonElement answer = await Query(new FakeOrders(time), """{service_name=~".+"} | CorrelationId = "demo-trace-0001" """);

        answer.GetProperty("data").GetProperty("result").GetArrayLength().ShouldBeGreaterThan(0);
    }
}
