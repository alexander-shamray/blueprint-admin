using System.Net;
using System.Net.Http.Headers;
using Admin.Host.Api;

namespace Admin.Host.Fakes;

/// <summary>
/// The object store and the edge in front of it in FakePlatform mode, for the two calls run-locally.md makes by hand
/// ("Attach a product image by hand"): the pre-signed <c>PUT</c> to the store, and the read of the published image from
/// <c>image-edge</c>. Written by hand rather than recorded, as <see cref="FakeOrders"/> is: whether an upload happened is state,
/// and a recording holds one instant. The fakes' rules follow the platform's: the store takes a write that carries a
/// signature and refuses one that does not, and keeps whatever bytes arrive (Catalog is what sniffs them when it attaches
/// the upload); the edge serves <c>products/</c> to a read alone, with <c>nosniff</c> (blueprint-backend
/// <c>deploy/compose/object-store/edge.conf</c>).
/// </summary>
internal static class FakeObjectStore
{
    /// <summary>The bucket the store holds product images in; the edge maps <c>/products/</c> onto <c>/product-images/products/</c>.</summary>
    public const string Bucket = "product-images";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, FakeOrders orders, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string prefix = $"/{Bucket}/";

        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/status")
        {
            return FakeHttp.Json(HttpStatusCode.OK, """{"Version":"fake"}""");
        }

        if (request.Method != HttpMethod.Put || !uri.AbsolutePath.StartsWith(prefix + "uploads/", StringComparison.Ordinal))
        {
            return Denied();
        }

        // A write with no signature in the URL is the anonymous one the policies refuse (§10.8).
        if (!uri.Query.Contains("X-Amz-Signature=", StringComparison.Ordinal))
        {
            return Denied();
        }

        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        orders.Store(uri.AbsolutePath[prefix.Length..], body.AsSpan().StartsWith(PngSignature) || IsJpegOrWebp(body));

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    public static HttpResponseMessage Edge(HttpRequestMessage request, FakeOrders orders)
    {
        string path = request.RequestUri!.AbsolutePath;

        if (path == "/healthz")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }

        if (!path.StartsWith("/products/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        if (!orders.Serves(path.TrimStart('/')))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ReadOnlyMemoryContent(SampleImage.Bytes),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(SampleImage.ContentType);
        response.Content.Headers.ContentLength = SampleImage.SizeBytes;
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");

        return response;
    }

    private static HttpResponseMessage Denied() =>
        new(HttpStatusCode.Forbidden) { Content = new StringContent("<Error><Code>AccessDenied</Code></Error>") };

    private static bool IsJpegOrWebp(byte[] body) =>
        (body.Length > 2 && body[0] == 0xFF && body[1] == 0xD8)
        || (body.Length > 12 && body[0] == (byte)'R' && body[8] == (byte)'W');
}
