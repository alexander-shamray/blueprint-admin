using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Scenario;

public sealed class ReviewEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_order_the_fake_never_flagged_answers_reachable_with_no_flags()
    {
        string id = Guid.NewGuid().ToString();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/review/{id}", Token);

        view.GetProperty("orderId").GetString().ShouldBe(id);
        view.GetProperty("window").GetString().ShouldBe("30m");
        view.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        view.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("flags").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_window_is_the_trace_screens_and_is_reported_back()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/review/{Guid.NewGuid()}?window=2h", Token);

        view.GetProperty("window").GetString().ShouldBe("2h");
    }

    [Fact]
    public async Task An_id_that_is_not_a_Guid_is_a_400_before_it_reaches_a_query()
    {
        using HttpResponseMessage response = await client.GetAsync("/api/review/not-a-guid\"%20or%20true", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain("Invalid order id");
    }

    [Fact]
    public async Task A_window_the_trace_screen_refuses_is_a_400()
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/review/{Guid.NewGuid()}?window=forever", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Token)).ShouldContain("Invalid window");
    }
}
