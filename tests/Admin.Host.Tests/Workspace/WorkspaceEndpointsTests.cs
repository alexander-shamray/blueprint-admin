using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Admin.Host.Tests.TestSupport;
using Admin.Host.Workspace;
using Shouldly;

namespace Admin.Host.Tests.Workspace;

public sealed class WorkspaceEndpointsTests : IClassFixture<AdminHostFactory>
{
    private readonly HttpClient client;

    public WorkspaceEndpointsTests(AdminHostFactory factory) => client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Doc(string widgetId, string type = "note", JsonObject? config = null) => new()
    {
        ["version"] = 1,
        ["activePageId"] = "mine",
        ["pages"] = new JsonArray(new JsonObject
        {
            ["id"] = "mine",
            ["title"] = "Mine",
            ["builtIn"] = null,
            ["hidden"] = false,
            ["widgets"] = new JsonArray(new JsonObject
            {
                ["id"] = widgetId,
                ["type"] = type,
                ["x"] = 0,
                ["y"] = 0,
                ["cols"] = 6,
                ["rows"] = 4,
                ["config"] = config ?? new JsonObject(),
            }),
        }),
    };

    private async Task<string> CurrentEtag()
    {
        using HttpResponseMessage response = await client.GetAsync("/api/workspace", Ct);

        return response.Headers.ETag!.ToString();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string body, string? ifMatch = null)
    {
        HttpRequestMessage request = new(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    [Fact]
    public async Task Get_serves_a_workspace_with_an_etag()
    {
        using HttpResponseMessage response = await client.GetAsync("/api/workspace", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.ETag.ShouldNotBeNull();
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["version"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task A_put_with_the_current_etag_saves_and_answers_the_new_one()
    {
        string etag = await CurrentEtag();
        JsonObject doc = Doc("saved");

        using HttpResponseMessage put = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", doc.ToJsonString(), etag), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        put.Headers.ETag!.ToString().ShouldNotBe(etag);
        (await CurrentEtag()).ShouldBe(put.Headers.ETag.ToString());
        using HttpResponseMessage get = await client.GetAsync("/api/workspace", Ct);
        JsonNode.DeepEquals(JsonNode.Parse(await get.Content.ReadAsStringAsync(Ct)), doc).ShouldBeTrue();
    }

    [Fact]
    public async Task A_put_with_a_stale_etag_is_412_with_the_current_document_and_etag()
    {
        string stale = await CurrentEtag();
        using HttpResponseMessage first = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", Doc("first").ToJsonString(), stale), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage second = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", Doc("second").ToJsonString(), stale), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        second.Headers.ETag!.ToString().ShouldBe(first.Headers.ETag!.ToString());
        (await second.Content.ReadAsStringAsync(Ct)).ShouldContain("\"first\"");
        (await second.Content.ReadAsStringAsync(Ct)).ShouldNotContain("\"second\"");
    }

    [Fact]
    public async Task A_put_without_if_match_is_428()
    {
        using HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", Doc("w").ToJsonString()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_put_that_is_not_a_workspace_is_400(string body)
    {
        using HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", body, await CurrentEtag()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_put_outside_the_grid_is_400_naming_the_widget()
    {
        JsonObject doc = Doc("wide");
        doc["pages"]![0]!["widgets"]![0]!["cols"] = 30;

        using HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", doc.ToJsonString(), await CurrentEtag()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("pages[0].widgets[0]");
    }

    [Fact]
    public async Task A_put_with_a_secret_named_key_is_400_and_saves_nothing()
    {
        string etag = await CurrentEtag();
        JsonObject doc = Doc("leaky", config: new JsonObject { ["password"] = "hunter2" });

        using HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", doc.ToJsonString(), etag), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("hunter2");
        (await CurrentEtag()).ShouldBe(etag);
    }

    [Fact]
    public async Task A_put_past_one_mebibyte_is_413_and_saves_nothing()
    {
        string etag = await CurrentEtag();
        string body = new(' ', WorkspaceSchema.MaxDocumentBytes + 1);

        using HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", body, etag), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CurrentEtag()).ShouldBe(etag);
    }

    [Fact]
    public async Task A_body_without_a_content_length_is_still_bounded()
    {
        string etag = await CurrentEtag();
        using HttpRequestMessage request = new(HttpMethod.Put, "/api/workspace")
        {
            Content = new StreamContent(new MemoryStream(new byte[WorkspaceSchema.MaxDocumentBytes + 1])),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task An_unknown_widget_type_round_trips_untouched_through_the_endpoints()
    {
        JsonObject doc = Doc("future", type: "from-the-future", config: new JsonObject { ["knob"] = new JsonArray(1, 2.50, "x") });
        using HttpResponseMessage put = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", doc.ToJsonString(), await CurrentEtag()), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage get = await client.GetAsync("/api/workspace", Ct);

        JsonNode.DeepEquals(JsonNode.Parse(await get.Content.ReadAsStringAsync(Ct)), doc).ShouldBeTrue();
    }

    [Fact]
    public async Task Export_downloads_the_saved_document()
    {
        JsonObject doc = Doc("exported");
        using HttpResponseMessage put = await client.SendAsync(Request(HttpMethod.Put, "/api/workspace", doc.ToJsonString(), await CurrentEtag()), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage export = await client.GetAsync("/api/workspace/export", Ct);

        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentDisposition!.FileName.ShouldBe("blueprint-admin-workspace.json");
        JsonNode.DeepEquals(JsonNode.Parse(await export.Content.ReadAsStringAsync(Ct)), doc).ShouldBeTrue();
    }

    [Fact]
    public async Task Import_replaces_the_workspace_with_a_valid_document_and_needs_no_etag()
    {
        JsonObject doc = Doc("imported");

        using HttpResponseMessage import = await client.SendAsync(Request(HttpMethod.Post, "/api/workspace/import", doc.ToJsonString()), Ct);

        import.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CurrentEtag()).ShouldBe(import.Headers.ETag!.ToString());
        using HttpResponseMessage get = await client.GetAsync("/api/workspace", Ct);
        JsonNode.DeepEquals(JsonNode.Parse(await get.Content.ReadAsStringAsync(Ct)), doc).ShouldBeTrue();
    }

    [Fact]
    public async Task Import_is_validated_as_put_is()
    {
        string etag = await CurrentEtag();
        JsonObject doc = Doc("leaky", config: new JsonObject { ["apiToken"] = "x" });

        using HttpResponseMessage secret = await client.SendAsync(Request(HttpMethod.Post, "/api/workspace/import", doc.ToJsonString()), Ct);
        using HttpResponseMessage huge = await client.SendAsync(Request(HttpMethod.Post, "/api/workspace/import", new string(' ', WorkspaceSchema.MaxDocumentBytes + 1)), Ct);

        secret.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        huge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CurrentEtag()).ShouldBe(etag);
    }

    [Theory]
    [InlineData("PUT", "/api/workspace")]
    [InlineData("POST", "/api/workspace/import")]
    public async Task A_cross_site_write_is_forbidden_and_saves_nothing(string method, string path)
    {
        string etag = await CurrentEtag();
        using HttpRequestMessage request = Request(new HttpMethod(method), path, Doc("evil").ToJsonString(), etag);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CurrentEtag()).ShouldBe(etag);
    }

    [Theory]
    [InlineData("PUT", "/api/workspace")]
    [InlineData("POST", "/api/workspace/import")]
    public async Task A_foreign_origin_write_is_forbidden_and_saves_nothing(string method, string path)
    {
        string etag = await CurrentEtag();
        using HttpRequestMessage request = Request(new HttpMethod(method), path, Doc("evil").ToJsonString(), etag);
        request.Headers.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CurrentEtag()).ShouldBe(etag);
    }

    [Theory]
    [InlineData("PUT", "/api/workspace")]
    [InlineData("POST", "/api/workspace/import")]
    public async Task A_same_origin_write_is_accepted(string method, string path)
    {
        using HttpRequestMessage request = Request(new HttpMethod(method), path, Doc("ok").ToJsonString(), await CurrentEtag());
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "http://localhost:5300");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
