using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Admin.Host.Fakes;
using Admin.Host.Tests.Identity;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>
/// The paths of the fake store and gateway that the real <c>ProductImageUploader</c> cannot reach, because it only ever sends the
/// sample PNG: the store keeps any bytes and Catalog is what refuses them when it attaches.
/// </summary>
public sealed class FakeObjectStoreTests
{
    private const string Gateway = "http://gateway.test";

    private const string Store = "http://store.test";

    private readonly FakeOrders orders = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));

    private static string Token => TokenServiceTests.Jwt("""{"permission":["catalog:write"],"preferred_username":"demo"}""");

    private HttpResponseMessage Post(string path, string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Gateway + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        return FakeGateway.Send(request, orders);
    }

    private async Task<string> IssueAsync()
    {
        using HttpResponseMessage issued = Post("/bff/v1/product-images", """{"contentType":"image/png","sizeBytes":12}""");
        issued.StatusCode.ShouldBe(HttpStatusCode.OK);

        return JsonDocument.Parse(await issued.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("key").GetString()!;
    }

    private async Task<HttpStatusCode> PutAsync(string key, byte[] bytes, string query = "?X-Amz-Signature=fake")
    {
        using HttpRequestMessage put = new(HttpMethod.Put, $"{Store}/product-images/{key}{query}") { Content = new ByteArrayContent(bytes) };
        using HttpResponseMessage response = await FakeObjectStore.SendAsync(put, orders, TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_kept_by_the_store_and_refused_by_the_publish()
    {
        string key = await IssueAsync();

        (await PutAsync(key, "this is not a picture"u8.ToArray())).ShouldBe(HttpStatusCode.OK);
        orders.Stored(key).ShouldBe(false);

        using HttpResponseMessage published = Post("/api/v1/catalog/products", $$"""{"name":"x","imageKey":"{{key}}","amount":1,"currency":"EUR"}""");

        published.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await published.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("product.image_not_an_image");
    }

    [Fact]
    public async Task A_png_is_kept_as_an_image_and_an_unsigned_write_keeps_nothing()
    {
        string key = await IssueAsync();

        (await PutAsync(key, Admin.Host.Api.SampleImage.Bytes.ToArray(), query: "")).ShouldBe(HttpStatusCode.Forbidden);
        orders.Stored(key).ShouldBeNull();

        (await PutAsync(key, Admin.Host.Api.SampleImage.Bytes.ToArray())).ShouldBe(HttpStatusCode.OK);
        orders.Stored(key).ShouldBe(true);
    }
}
