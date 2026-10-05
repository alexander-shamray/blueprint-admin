using System.Net;
using System.Text;
using System.Text.Json;
using Admin.Host.Api;
using Admin.Host.Broker;
using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Fakes;
using Admin.Host.Identity;
using Admin.Host.Jobs;
using Admin.Host.Telemetry;
using Admin.Host.Tests.TestSupport;
using Admin.Host.Transcript;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FixtureRecorderTests : IDisposable
{
    private static readonly RepoPaths Paths = new("/repo/backend", "/repo/frontend", "/repo/backend/deploy/compose/docker-compose.yml");
    private static readonly AdminOptions Options = new();

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("admin-record-");

    public void Dispose() => root.Delete(recursive: true);

    private string Fixtures => Directory.CreateDirectory(Path.Combine(root.FullName, "fixtures")).FullName;

    private FixtureRecorder Recorder() =>
        FixtureRecorder.From(new AdminOptions { Record = true, RecordDir = Fixtures }, root.FullName, NullLogger.Instance);

    private string Recorded(string fixture) => File.ReadAllText(Path.Combine(Fixtures, fixture));

    private bool Exists(string fixture) => File.Exists(Path.Combine(Fixtures, fixture));

    [Fact]
    public void Recording_is_off_unless_asked_for()
    {
        FixtureRecorder.From(new AdminOptions(), root.FullName, NullLogger.Instance).On.ShouldBeFalse();
    }

    [Fact]
    public void Recording_the_fakes_is_refused_naming_both_keys()
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            FixtureRecorder.From(new AdminOptions { Record = true, FakePlatform = true, RecordDir = Fixtures }, root.FullName, NullLogger.Instance));

        error.Message.ShouldContain("Admin:Record");
        error.Message.ShouldContain("Admin:FakePlatform");
    }

    [Fact]
    public void A_record_directory_that_does_not_exist_is_refused_naming_the_key()
    {
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            FixtureRecorder.From(new AdminOptions { Record = true, RecordDir = "no-such-dir" }, root.FullName, NullLogger.Instance));

        error.Message.ShouldContain("Admin:RecordDir");
    }

    [Fact]
    public void The_default_record_directory_is_this_checkouts_fixtures()
    {
        FixtureRecorder recorder = FixtureRecorder.From(new AdminOptions { Record = true }, Drift.Backend.AdminRoot, NullLogger.Instance);

        recorder.Directory.ShouldBe(Path.Combine(Drift.Backend.AdminRoot, "src", "Admin.Host", "Fakes", "fixtures"));
    }

    [Fact]
    public void The_host_refuses_to_start_recording_the_fakes()
    {
        using AdminHostFactory factory = new();

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            factory.WithWebHostBuilder(b => b.UseSetting("Admin:Record", "true")).CreateClient());

        error.Message.ShouldContain("Admin:FakePlatform");
    }

    [Fact]
    public void A_write_is_scrubbed_before_it_reaches_the_disk()
    {
        Recorder().Write("openapi-catalog.json", """{"h":"Bearer 0123456789abcdef0123"}""");

        Recorded("openapi-catalog.json").ShouldBe("""{"h":"Bearer <scrubbed>"}""" + "\n");
    }

    [Fact]
    public void A_keyed_write_sets_its_key_and_keeps_the_others()
    {
        FixtureRecorder recorder = Recorder();
        File.WriteAllText(Path.Combine(Fixtures, "signals.json"), """{"ErrorRatio":{"old":true}}""");

        recorder.WriteKeyed("signals.json", "RequestRate", """{"status":"success"}""");

        using JsonDocument written = JsonDocument.Parse(Recorded("signals.json"));
        written.RootElement.GetProperty("ErrorRatio").GetProperty("old").GetBoolean().ShouldBeTrue();
        written.RootElement.GetProperty("RequestRate").GetProperty("status").GetString().ShouldBe("success");
    }

    [Fact]
    public async Task A_named_answer_is_recorded_and_its_caller_still_reads_the_whole_body()
    {
        const string document = """{"openapi":"3.1.1"}""";
        using HttpClient client = Client(Recorder(), _ => Answer(HttpStatusCode.OK, document));

        string body = await client.GetStringAsync(new Uri(Options.CatalogUrl + "/openapi/v1.json"), TestContext.Current.CancellationToken);

        body.ShouldBe(document);
        Recorded("openapi-catalog.json").ShouldBe(document + "\n");
    }

    [Fact]
    public async Task An_error_answer_another_host_or_another_path_is_not_recorded()
    {
        using HttpClient refused = Client(Recorder(), _ => Answer(HttpStatusCode.Unauthorized, "{}"));
        using HttpClient ok = Client(Recorder(), _ => Answer(HttpStatusCode.OK, "{}"));
        CancellationToken token = TestContext.Current.CancellationToken;

        (await refused.GetAsync(new Uri(Options.CatalogUrl + "/openapi/v1.json"), token)).Dispose();
        (await ok.GetAsync(new Uri(Options.GatewayUrl + "/openapi/v1.json"), token)).Dispose();
        (await ok.GetAsync(new Uri(Options.CatalogUrl + "/health/ready"), token)).Dispose();

        Directory.EnumerateFiles(Fixtures).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_recording_off_nothing_is_written()
    {
        FixtureRecorder off = FixtureRecorder.From(new AdminOptions { RecordDir = Fixtures }, root.FullName, NullLogger.Instance);
        using HttpClient client = Client(off, _ => Answer(HttpStatusCode.OK, "{}"));

        (await client.GetAsync(new Uri(Options.CatalogUrl + "/openapi/v1.json"), TestContext.Current.CancellationToken)).Dispose();

        Directory.EnumerateFiles(Fixtures).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_golden_signal_query_is_recorded_under_its_field_and_any_other_query_is_not()
    {
        using HttpClient client = Client(Recorder(), _ => Answer(HttpStatusCode.OK, """{"status":"success"}"""));
        string proxy = Options.GrafanaUrl + "/api/datasources/proxy/uid/prometheus/api/v1/query?query=";
        CancellationToken token = TestContext.Current.CancellationToken;

        (await client.GetAsync(new Uri(proxy + Uri.EscapeDataString("up")), token)).Dispose();
        Exists("grafana-prometheus-golden-signals.json").ShouldBeFalse();

        (await client.GetAsync(new Uri(proxy + Uri.EscapeDataString(GoldenSignals.RequestRate)), token)).Dispose();
        using JsonDocument written = JsonDocument.Parse(Recorded("grafana-prometheus-golden-signals.json"));
        written.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe([nameof(GoldenSignals.RequestRate)]);
    }

    /// <summary>
    /// Through the services that make these reads, so the table's arguments are proved to be the ones sent: a
    /// recording keyed on a command nothing runs would record nothing, silently.
    /// </summary>
    [Fact]
    public async Task The_compose_and_broker_reads_are_recorded_from_their_standard_output()
    {
        await using FakeProcessRunner fake = FakePlatformScripts.Script(new FakeProcessRunner(new JobRegistry(TimeProvider.System)), Paths);
        RecordingProcessRunner runner = new(fake, Recorder(), Paths);
        ComposeService compose = new(runner, Paths, TimeProvider.System);
        BrokerService broker = new(compose);
        CancellationToken token = TestContext.Current.CancellationToken;

        await compose.PsAsync(token);
        await broker.QueuesAsync(token);
        await broker.ExchangesAsync(token);
        await broker.PermissionsAsync(token);
        await runner.Settled();

        Recorded("compose-ps.jsonl").ShouldBe(string.Join('\n', FakePlatformScripts.ComposePsLines()) + "\n");
        Recorded("rabbitmq-exchanges.json").ShouldBe(string.Join('\n', FakePlatformScripts.FixtureLines("rabbitmq-exchanges.json")) + "\n");
        Exists("rabbitmq-permissions.json").ShouldBeTrue();

        // Hand-written, for its error queue: a recording run must leave it alone.
        Exists("rabbitmq-queues.json").ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_that_fails_is_not_recorded()
    {
        await using FakeProcessRunner fake = new(new JobRegistry(TimeProvider.System));
        fake.OnFailing("docker", $"compose -f {Paths.ComposeFile} ps -a --format json", 1, "Cannot connect to the Docker daemon");
        RecordingProcessRunner runner = new(fake, Recorder(), Paths);

        await new ComposeService(runner, Paths, TimeProvider.System).PsAsync(TestContext.Current.CancellationToken);
        await runner.Settled();

        Exists("compose-ps.jsonl").ShouldBeFalse();
    }

    /// <summary>
    /// Through the clients that make these reads, as the process side is: each document lands in its own service's
    /// fixture and the datasource list in its own, so a table row pointing at the wrong host cannot pass.
    /// </summary>
    [Fact]
    public async Task Each_document_the_catalog_fetches_and_the_datasource_list_land_in_their_own_fixture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        IOptions<AdminOptions> options = Microsoft.Extensions.Options.Options.Create(Options);
        using HttpClient http = Client(Recorder(), request =>
            request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, $$"""{"access_token":"{{Identity.TokenServiceTests.Jwt("{}")}}","expires_in":300}""")
                : Answer(HttpStatusCode.OK, $$"""{"answered":"{{request.RequestUri}}"}"""));
        ApiCatalog catalog = new(
            http,
            new TokenService(http, options, TimeProvider.System),
            options,
            new OpenApiBaselines(Path.Combine(root.FullName, "baselines"), TimeProvider.System));

        await catalog.GetAsync(token);
        await new GrafanaClient(http, options).UidsAsync(token);

        Recorded("openapi-catalog.json").ShouldContain(Options.CatalogUrl + "/openapi/v1.json");
        Recorded("openapi-ordering.json").ShouldContain(Options.OrderingUrl + "/openapi/v1.json");
        Recorded("openapi-inventory.json").ShouldContain(Options.InventoryUrl + "/openapi/v1.json");
        Recorded("openapi-payments.json").ShouldContain(Options.PaymentsUrl + "/openapi/v1.json");
        Recorded("grafana-datasources.json").ShouldContain(Options.GrafanaUrl + "/api/datasources");
        Directory.EnumerateFiles(Fixtures).Count().ShouldBe(5, "the token answer is never a fixture");
    }

    /// <summary>The seams are Program.cs's: deleting either would leave a recording run that records nothing.</summary>
    [Fact]
    public void A_host_asked_to_record_wraps_its_process_runner_and_its_platform_client()
    {
        string backend = Directory.CreateDirectory(Path.Combine(root.FullName, "backend", "deploy", "compose")).FullName;
        File.WriteAllText(Path.Combine(backend, "docker-compose.yml"), "name: commerce\n");
        string frontend = Directory.CreateDirectory(Path.Combine(root.FullName, "frontend")).FullName;
        File.WriteAllText(Path.Combine(frontend, "package.json"), "{}");
        using AdminHostFactory factory = new();
        using WebApplicationFactory<Program> recording = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:FakePlatform"] = "false",
                ["Admin:Record"] = "true",
                ["Admin:RecordDir"] = Fixtures,
                ["Admin:BackendDir"] = Path.Combine(root.FullName, "backend"),
                ["Admin:FrontendDir"] = frontend,
            })));

        recording.Services.GetRequiredService<IProcessRunner>().ShouldBeOfType<TranscribingProcessRunner>().Inner
            .ShouldBeOfType<RecordingProcessRunner>();

        HttpMessageHandler? handler = recording.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("platform");
        List<HttpMessageHandler> chain = [];
        while (handler is not null)
        {
            chain.Add(handler);
            handler = (handler as DelegatingHandler)?.InnerHandler;
        }

        chain.ShouldContain(h => h is RecordingHandler);
    }

    [Fact]
    public void A_held_keyed_fixture_that_is_not_an_object_is_started_afresh()
    {
        File.WriteAllText(Path.Combine(Fixtures, "signals.json"), "[]");

        Recorder().WriteKeyed("signals.json", "RequestRate", """{"status":"success"}""");

        using JsonDocument written = JsonDocument.Parse(Recorded("signals.json"));
        written.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["RequestRate"]);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_not_recorded_under_its_key_and_its_caller_still_reads_it()
    {
        using HttpClient client = Client(Recorder(), _ => Answer(HttpStatusCode.OK, "not json"));
        string query = Options.GrafanaUrl + "/api/datasources/proxy/uid/prometheus/api/v1/query?query="
            + Uri.EscapeDataString(GoldenSignals.RequestRate);

        (await client.GetStringAsync(new Uri(query), TestContext.Current.CancellationToken)).ShouldBe("not json");

        Exists("grafana-prometheus-golden-signals.json").ShouldBeFalse();
    }

    [Fact]
    public void A_write_that_cannot_land_is_dropped_and_leaves_no_temporary_file()
    {
        // A directory where the fixture should be: the move over it fails, as a locked file would.
        Directory.CreateDirectory(Path.Combine(Fixtures, "openapi-catalog.json"));

        Should.NotThrow(() => Recorder().Write("openapi-catalog.json", "{}"));

        Exists("openapi-catalog.json.recording").ShouldBeFalse();
    }

    private static HttpClient Client(FixtureRecorder recorder, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new RecordingHandler(recorder, Microsoft.Extensions.Options.Options.Create(Options)) { InnerHandler = new ScriptedHandler(respond) });

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
