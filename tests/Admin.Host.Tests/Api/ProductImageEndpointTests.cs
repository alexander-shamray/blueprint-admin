using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Api;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Api;

/// <summary>
/// "Attach a product image by hand" end to end against the fakes: the signed request, the bytes to the store, the publish with the
/// key, the product read back and the thumbnail read through the edge, and each place the platform refuses it.
/// </summary>
public sealed class ProductImageEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private const string Gateway = "http://localhost:5000";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly HttpClient client = factory.CreateClient();

    private async Task<JsonElement> ProxyAsync(string method, string url, string? body, string? username = "demo")
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/proxy", new { method, url, body, identity = new { username } }, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private async Task<JsonElement> IssueAsync(string? contentType = "image/png", long? size = null)
    {
        JsonElement issued = await ProxyAsync("POST", $"{Gateway}/bff/v1/product-images", JsonSerializer.Serialize(new { contentType, sizeBytes = size ?? SampleImage.SizeBytes }));
        issued.GetProperty("status").GetInt32().ShouldBe(200, issued.ToString());

        return JsonDocument.Parse(issued.GetProperty("body").GetString()!).RootElement.Clone();
    }

    private async Task<JsonElement> UploadAsync(JsonElement issued)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/product-images/upload", new { uploadUrl = issued.GetProperty("uploadUrl").GetString(), headers = issued.GetProperty("headers") }, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private Task<JsonElement> PublishAsync(string imageKey, string username = "demo") =>
        ProxyAsync("POST", $"{Gateway}/api/v1/catalog/products", JsonSerializer.Serialize(new { commandId = Guid.NewGuid(), name = "Walnut desk", imageKey, amount = 19.99, currency = "EUR" }), username);

    [Fact]
    public async Task A_product_published_with_the_uploaded_key_is_read_back_with_a_thumbnail_the_edge_serves()
    {
        JsonElement issued = await IssueAsync();
        issued.GetProperty("headers").GetProperty("Content-Length").GetString().ShouldBe(SampleImage.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await UploadAsync(issued)).GetProperty("status").GetInt32().ShouldBe(200);

        JsonElement published = await PublishAsync(issued.GetProperty("key").GetString()!);
        published.GetProperty("status").GetInt32().ShouldBe(200, published.ToString());
        string id = JsonDocument.Parse(published.GetProperty("body").GetString()!).RootElement.GetString()!;

        JsonElement read = await ProxyAsync("GET", $"{Gateway}/api/v1/catalog/products/{id}", null, username: null);
        read.GetProperty("status").GetInt32().ShouldBe(200);
        string thumbnail = JsonDocument.Parse(read.GetProperty("body").GetString()!).RootElement.GetProperty("thumbnailUrl").GetString()!;
        thumbnail.ShouldStartWith("http://127.0.0.1:9000/products/");

        JsonElement head = await ProxyAsync("HEAD", thumbnail, null, username: null);
        head.GetProperty("status").GetInt32().ShouldBe(200);
        head.GetProperty("headers").GetProperty("X-Content-Type-Options")[0].GetString().ShouldBe("nosniff");
        head.GetProperty("headers").GetProperty("Content-Type")[0].GetString().ShouldBe("image/png");
    }

    [Fact]
    public async Task A_publish_whose_bytes_never_reached_the_store_is_refused_as_not_uploaded()
    {
        JsonElement issued = await IssueAsync();

        JsonElement published = await PublishAsync(issued.GetProperty("key").GetString()!);

        published.GetProperty("status").GetInt32().ShouldBe(422);
        published.GetProperty("body").GetString()!.ShouldContain("product.image_not_uploaded");
    }

    [Fact]
    public async Task A_key_that_is_not_the_callers_is_refused_as_invalid()
    {
        JsonElement forged = await PublishAsync($"uploads/{Guid.NewGuid():D}/{Guid.NewGuid():D}.png");
        forged.GetProperty("status").GetInt32().ShouldBe(422);
        forged.GetProperty("body").GetString()!.ShouldContain("product.image_key_invalid");
    }

    [Fact]
    public async Task The_edge_serves_nothing_that_was_not_published()
    {
        JsonElement head = await ProxyAsync("HEAD", $"http://127.0.0.1:9000/products/{Guid.NewGuid():D}/{Guid.NewGuid():D}.png", null, username: null);

        head.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Theory]
    [InlineData("image/svg+xml", 69)]
    [InlineData("image/png", 0)]
    [InlineData("image/png", 5242881)]
    public async Task The_bff_signs_nothing_for_a_type_or_size_it_does_not_allow(string contentType, long size)
    {
        JsonElement refused = await ProxyAsync("POST", $"{Gateway}/bff/v1/product-images", JsonSerializer.Serialize(new { contentType, sizeBytes = size }));

        refused.GetProperty("status").GetInt32().ShouldBe(400);
    }

    [Fact]
    public async Task A_caller_without_the_publish_permission_is_refused_the_upload_url_and_one_without_a_token_is_unauthorised()
    {
        string body = JsonSerializer.Serialize(new { contentType = "image/png", sizeBytes = SampleImage.SizeBytes });

        (await ProxyAsync("POST", $"{Gateway}/bff/v1/product-images", body, username: "browser")).GetProperty("status").GetInt32().ShouldBe(403);
        (await ProxyAsync("POST", $"{Gateway}/bff/v1/product-images", body, username: null)).GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Theory]
    [InlineData("http://localhost:5000/product-images/uploads/x/y.png?X-Amz-Signature=a")]
    [InlineData("http://example.com/product-images/uploads/x/y.png?X-Amz-Signature=a")]
    [InlineData("not a url")]
    public async Task The_upload_goes_to_the_configured_object_store_alone(string uploadUrl)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/product-images/upload", new { uploadUrl, headers = new Dictionary<string, string>() }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_store_refuses_a_write_with_no_signature()
    {
        JsonElement unsigned = await UploadAsync(JsonDocument.Parse("""{"uploadUrl":"http://127.0.0.1:8333/product-images/uploads/a/b.png","headers":{}}""").RootElement);

        unsigned.GetProperty("status").GetInt32().ShouldBe(403);
    }

    [Fact]
    public async Task The_saved_address_is_put_normalised_and_read_back_for_the_caller_alone()
    {
        const string address = """{"line1":" 1 High Street ","line2":null,"city":"Leeds","postalCode":"LS1 1AA","country":"gb"}""";

        (await ProxyAsync("GET", $"{Gateway}/bff/v1/saved-address", null, username: "browser")).GetProperty("status").GetInt32().ShouldBe(404);

        JsonElement put = await ProxyAsync("PUT", $"{Gateway}/bff/v1/saved-address", address, username: "browser");
        put.GetProperty("status").GetInt32().ShouldBe(200);
        JsonDocument.Parse(put.GetProperty("body").GetString()!).RootElement.GetProperty("country").GetString().ShouldBe("GB");

        JsonElement read = await ProxyAsync("GET", $"{Gateway}/bff/v1/saved-address", null, username: "browser");
        JsonDocument.Parse(read.GetProperty("body").GetString()!).RootElement.GetProperty("line1").GetString().ShouldBe("1 High Street");
        (await ProxyAsync("GET", $"{Gateway}/bff/v1/saved-address", null, username: "demo")).GetProperty("status").GetInt32().ShouldBe(404);
        (await ProxyAsync("PUT", $"{Gateway}/bff/v1/saved-address", """{"line1":"","city":"Leeds","postalCode":"LS1","country":"GB"}""", username: "demo"))
            .GetProperty("status").GetInt32().ShouldBe(400);
    }
}
