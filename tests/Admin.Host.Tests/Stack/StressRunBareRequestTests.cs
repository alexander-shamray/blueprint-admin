using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Stack;

/// <summary>A host of its own, as <see cref="StressRunEndpointTests"/>: a fake stress run claims its ComposeGate and leaves the stack on the override.</summary>
public sealed class StressRunBareRequestTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task A_post_with_no_body_is_a_run_at_the_default_level_and_rule_and_a_named_choice_is_the_one_reported()
    {
        // The Stack screen's client used to post null, and the binder answers that 404 once the route takes a body.
        HttpResponseMessage bare = await client.PostAsync("/api/stack/stress-run", null, TestContext.Current.CancellationToken);

        bare.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);
        JsonElement choice = stack.GetProperty("stressRun").GetProperty("choice");
        choice.GetProperty("level").GetString().ShouldBe("low");
        choice.GetProperty("stop").GetString().ShouldBe("knee");
        choice.TryGetProperty("environment", out _).ShouldBeFalse();

        (await client.PostAsync("/api/stack/stress-run/stop", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage named = await client.PostAsJsonAsync("/api/stack/stress-run", new { level = "high", stop = "errors" }, TestContext.Current.CancellationToken);

        named.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", TestContext.Current.CancellationToken);
        stack.GetProperty("stressRun").GetProperty("choice").GetProperty("level").GetString().ShouldBe("high");
        stack.GetProperty("stressRun").GetProperty("choice").GetProperty("stop").GetString().ShouldBe("errors");
        (await client.PostAsync("/api/stack/stress-run/stop", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
