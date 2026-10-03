using System.Net;
using System.Text;
using Admin.Host.Api;
using Admin.Host.Config;
using Admin.Host.Identity;
using Admin.Host.Tests.Identity;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class ApiCatalogTests : IDisposable
{
    private readonly string dataDir = Directory.CreateTempSubdirectory("admin-openapi-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(dataDir, recursive: true);

    private static string FixtureText(string name)
    {
        using Stream stream = typeof(AdminOptions).Assembly.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Keycloak grants anything; each document URL answers what <paramref name="documents"/> says for it.</summary>
    private static ScriptedHandler Platform(Func<string, HttpResponseMessage> documents) => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{TokenServiceTests.Jwt("{}")}}","expires_in":300}""")
            : documents(request.RequestUri.ToString()));

    private ApiCatalog Catalog(ScriptedHandler handler, AdminOptions? options = null)
    {
        IOptions<AdminOptions> wrapped = Options.Create(options ?? new AdminOptions());
        HttpClient http = new(handler);

        return new ApiCatalog(http, new TokenService(http, wrapped, TimeProvider.System), wrapped, new OpenApiBaselines(dataDir, TimeProvider.System));
    }

    private static HttpResponseMessage Both(string url) => url switch
    {
        "http://localhost:5102/openapi/v1.json" => Json(HttpStatusCode.OK, FixtureText("openapi-catalog.json")),
        "http://localhost:5101/openapi/v1.json" => Json(HttpStatusCode.OK, FixtureText("openapi-ordering.json")),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Both_documents_load_with_a_demo_token_and_curated_operations_follow()
    {
        ScriptedHandler handler = Platform(Both);

        ApiCatalogView view = await Catalog(handler).GetAsync(Token);

        view.Sources.Select(s => (s.Name, s.DocumentUrl, s.Available, s.Error)).ShouldBe(
        [
            ("catalog", "http://localhost:5102/openapi/v1.json", true, null),
            ("ordering", "http://localhost:5101/openapi/v1.json", true, null),
        ]);
        view.Sources.ShouldAllBe(s => s.Changes!.BaselineTaken);
        view.Operations.Select(o => o.Id).ShouldBe(
        [
            "catalog:PublishProduct", "catalog:GetProducts", "ordering:PlaceOrder", "ordering:CancelOrder",
            "bff:Quote", "health:gateway", "health:catalog", "health:ordering", "health:bff",
        ]);
        handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath == "/openapi/v1.json")
            .ShouldAllBe(r => r.Request.Headers.Authorization!.Scheme == "Bearer");
        handler.Requests.Single(r => r.Body != null).Body!.ShouldContain("username=demo");
    }

    [Fact]
    public async Task A_document_that_moved_since_its_baseline_reports_what_moved_until_it_is_accepted()
    {
        string catalogV2 = FixtureText("openapi-catalog.json").Replace("\"productId\":", "\"quantityAvailable\": { \"type\": \"integer\" }, \"productId\":", StringComparison.Ordinal);
        bool moved = false;
        ScriptedHandler handler = Platform(url => moved && url.Contains("5102", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, catalogV2) : Both(url));
        ApiCatalog catalog = Catalog(handler);

        await catalog.GetAsync(Token);
        moved = true;
        ApiSource changed = (await catalog.ReloadAsync(Token)).Sources.Single(s => s.Name == "catalog");

        changed.Changes!.BaselineTaken.ShouldBeFalse();
        changed.Changes.FieldsAdded.ShouldContain(f => f.EndsWith(".quantityAvailable", StringComparison.Ordinal));
        changed.Changes.OperationsAdded.ShouldBeEmpty();

        ApiSource accepted = (await catalog.AcceptAsync("catalog", Token)).View!.Sources.Single(s => s.Name == "catalog");

        accepted.Changes!.Any.ShouldBeFalse();
        (await catalog.AcceptAsync("../../etc", Token)).ShouldBe((null, null));
    }

    [Fact]
    public async Task The_catalog_is_cached_until_reloaded()
    {
        ScriptedHandler handler = Platform(Both);
        ApiCatalog catalog = Catalog(handler);

        await catalog.GetAsync(Token);
        await catalog.GetAsync(Token);
        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/openapi/v1.json").ShouldBe(2);

        await catalog.ReloadAsync(Token);
        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/openapi/v1.json").ShouldBe(4);
    }

    [Fact]
    public async Task Concurrent_first_reads_load_the_documents_once()
    {
        TaskCompletionSource answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedHandler handler = new(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $$"""{"access_token":"{{TokenServiceTests.Jwt("{}")}}","expires_in":300}""");
            }

            await answer.Task;

            return Both(request.RequestUri.ToString());
        });
        ApiCatalog catalog = Catalog(handler);

        Task<ApiCatalogView> first = catalog.GetAsync(Token);
        Task<ApiCatalogView> second = catalog.GetAsync(Token);
        answer.SetResult();
        await Task.WhenAll(first, second);

        handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/openapi/v1.json").ShouldBe(2);
        (await second).ShouldBeSameAs(await first);
    }

    [Fact]
    public async Task A_service_that_never_answered_is_listed_as_a_source_with_its_error_and_no_operations()
    {
        ScriptedHandler handler = Platform(url => url.Contains(":5101", StringComparison.Ordinal) ? throw new HttpRequestException("Connection refused") : Both(url));

        ApiCatalogView view = await Catalog(handler).GetAsync(Token);

        ApiSource ordering = view.Sources.Single(s => s.Name == "ordering");
        ordering.Available.ShouldBeFalse();
        ordering.Error.ShouldNotBeNull().ShouldContain("Connection refused");
        view.Operations.ShouldNotContain(o => o.Source == "ordering");
        view.Operations.Where(o => o.Source == "catalog").ShouldAllBe(o => o.Available);
    }

    [Fact]
    public async Task A_service_that_goes_down_keeps_its_operations_marked_unavailable()
    {
        bool orderingUp = true;
        ScriptedHandler handler = Platform(url => url.Contains(":5101", StringComparison.Ordinal) && !orderingUp ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Both(url));
        ApiCatalog catalog = Catalog(handler);

        await catalog.GetAsync(Token);
        orderingUp = false;
        ApiCatalogView view = await catalog.ReloadAsync(Token);

        view.Operations.Where(o => o.Source == "ordering").Select(o => (o.Name, o.Available)).ShouldBe([("PlaceOrder", false), ("CancelOrder", false)]);
        view.Sources.Single(s => s.Name == "ordering").Error.ShouldBe("http://localhost:5101/openapi/v1.json answered 503.");

        orderingUp = true;
        (await catalog.ReloadAsync(Token)).Operations.Where(o => o.Source == "ordering").ShouldAllBe(o => o.Available);
    }

    [Fact]
    public async Task A_refused_token_makes_both_sources_unavailable_with_keycloaks_status()
    {
        ScriptedHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Both(request.RequestUri.ToString()));

        ApiCatalogView view = await Catalog(handler).GetAsync(Token);

        view.Sources.ShouldAllBe(s => !s.Available && s.Error == "The token request for demo answered 401.");
        view.Operations.Select(o => o.Source).Distinct().ShouldBe(["bff", "health"]);
    }

    [Fact]
    public async Task A_document_that_is_not_json_is_an_unavailable_source()
    {
        ScriptedHandler handler = Platform(url => url.Contains(":5102", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, "<html>") : Both(url));

        ApiCatalogView view = await Catalog(handler).GetAsync(Token);

        view.Sources.Single(s => s.Name == "catalog").Error.ShouldNotBeNull().ShouldStartWith("http://localhost:5102/openapi/v1.json is not JSON");
    }

    [Fact]
    public async Task A_document_whose_shape_is_malformed_is_an_unavailable_source_and_the_other_still_loads()
    {
        ScriptedHandler handler = Platform(url => url.Contains(":5102", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, """{"paths":{"/products":"oops"}}""") : Both(url));

        ApiCatalogView view = await Catalog(handler).GetAsync(Token);

        ApiSource catalog = view.Sources.Single(s => s.Name == "catalog");
        catalog.Available.ShouldBeFalse();
        catalog.Error.ShouldNotBeNull().ShouldStartWith("http://localhost:5102/openapi/v1.json is not a readable OpenAPI document: ");
        view.Sources.Single(s => s.Name == "ordering").Available.ShouldBeTrue();
    }

    [Fact]
    public async Task The_catalog_identity_is_the_first_configured_user_when_there_is_no_demo()
    {
        ScriptedHandler handler = Platform(Both);
        AdminOptions options = new() { Users = [new RealmUser { Username = "ops", Password = "pw" }] };

        await Catalog(handler, options).GetAsync(Token);

        handler.Requests.Single(r => r.Body != null).Body!.ShouldContain("username=ops&password=pw");
    }
}
