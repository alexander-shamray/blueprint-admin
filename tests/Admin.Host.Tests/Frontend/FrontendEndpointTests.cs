using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Frontend;
using Admin.Host.Jobs;
using Admin.Host.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Admin.Host.Tests.Frontend;

public sealed class FrontendEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_reports_an_installed_frontend_with_no_job_before_any_start()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);

        JsonElement frontend = stack.GetProperty("frontend");
        frontend.GetProperty("installed").GetBoolean().ShouldBeTrue();
        frontend.GetProperty("job").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Start_runs_npm_start_and_replays_the_ng_serve_banner()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/start", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("commandLine").GetString().ShouldBe("npm start");
        job.GetProperty("state").GetString().ShouldBe("Running");

        JsonElement view = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{job.GetProperty("id").GetString()}", Token);
        view.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).ShouldContain("  Local:   http://localhost:5173/");

        JsonElement stack = await client.GetFromJsonAsync<JsonElement>("/api/stack", Token);
        stack.GetProperty("frontend").GetProperty("job").GetProperty("state").GetString().ShouldBe("Running");
    }

    [Fact]
    public async Task A_second_start_while_running_is_a_conflict()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        (await client.PostAsync("/api/stack/frontend/start", null, Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage second = await client.PostAsync("/api/stack/frontend/start", null, Token);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Frontend already running");
        factory.Services.GetRequiredService<FakeProcessRunner>().Started.Count(s => s.FileName == "npm").ShouldBe(1);
    }

    [Fact]
    public async Task Stop_kills_the_job_and_a_second_stop_is_a_conflict()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();
        await client.PostAsync("/api/stack/frontend/start", null, Token);

        HttpResponseMessage stop = await client.PostAsync("/api/stack/frontend/stop", null, Token);

        stop.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement job = await stop.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("state").GetString().ShouldBe("Exited");
        job.GetProperty("exitCode").GetInt32().ShouldBe(-1);

        HttpResponseMessage again = await client.PostAsync("/api/stack/frontend/stop", null, Token);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Frontend not running");
    }

    [Fact]
    public async Task The_device_build_replays_the_sync_and_the_stack_reports_it_beside_the_dev_server()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/device-build", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement job = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        job.GetProperty("commandLine").GetString().ShouldBe("npm run emulator:android");

        JsonElement frontend = (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("frontend");
        frontend.GetProperty("deviceJob").GetProperty("id").GetString().ShouldBe(job.GetProperty("id").GetString());
        frontend.GetProperty("job").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_device_reads_answer_ok_three_times_against_the_fakes()
    {
        await using AdminHostFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/stack/frontend/device-reads", Token);

        JsonElement[] checks = [.. view.GetProperty("checks").EnumerateArray()];
        checks.Select(c => c.GetProperty("name").GetString()).ShouldBe(["Emulator attached", "Gateway admits the app's origin", "Realm redirect for mobile-app"]);
        checks.ShouldAllBe(c => c.GetProperty("state").GetString() == "Ok");
    }

    [Fact]
    public async Task Start_without_node_modules_is_refused_and_says_to_run_npm_ci()
    {
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> uninstalled = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => new FrontendSupervisor(
                sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(),
                _ => new InstallReading(InstallState.Missing, "the clone has no node_modules. Run npm ci there; the console does not.")))));
        HttpClient client = uninstalled.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/stack/frontend/start", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Frontend dependencies not installed");
        problem.GetProperty("detail").GetString()!.ShouldContain("npm ci");
        uninstalled.Services.GetRequiredService<FakeProcessRunner>().Started.ShouldNotContain(s => s.FileName == "npm");
        (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("frontend").GetProperty("installed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Start_with_a_node_modules_older_than_the_lock_is_refused_with_the_reason_on_both_routes()
    {
        const string reason = "node_modules does not match package-lock.json (installed 2026-09-12, lock changed 2026-10-20): @angular/core 22.1.6 is installed (the lock has 22.2.3). Run npm ci in /clone; the console does not.";
        await using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> stale = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => new FrontendSupervisor(
                sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<RepoPaths>(), _ => new InstallReading(InstallState.Stale, reason)))));
        HttpClient client = stale.CreateClient();

        foreach (string route in new[] { "/api/stack/frontend/start", "/api/stack/frontend/device-build" })
        {
            HttpResponseMessage response = await client.PostAsync(route, null, Token);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("detail").GetString().ShouldBe(reason);
        }

        stale.Services.GetRequiredService<FakeProcessRunner>().Started.ShouldNotContain(s => s.FileName == "npm");
        JsonElement frontend = (await client.GetFromJsonAsync<JsonElement>("/api/stack", Token)).GetProperty("frontend");
        frontend.GetProperty("installed").GetBoolean().ShouldBeFalse();
        frontend.GetProperty("installProblem").GetString().ShouldBe(reason);
    }
}
