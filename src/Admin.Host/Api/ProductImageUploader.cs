using System.Net.Http.Headers;
using Admin.Host.Config;
using Admin.Host.Transcript;
using Microsoft.Extensions.Options;

namespace Admin.Host.Api;

/// <summary>One request for <c>POST /api/product-images/upload</c>: the pre-signed URL and the headers the BFF returned with it.</summary>
public sealed record ProductImageUploadRequest(string UploadUrl, IReadOnlyDictionary<string, string>? Headers);

/// <summary>
/// The second call of run-locally.md's "Attach a product image by hand": <c>PUT</c> the image's bytes to the pre-signed
/// URL the BFF returned. The proxy sends a string body and so cannot carry these bytes, which is why this is a call of
/// its own, and it goes only where the URL names the configured object store, so it is no way to send a body anywhere
/// else. It signs nothing and holds no credential: the signature is in the URL the BFF issued, and the store judges it.
/// The answer is the store's, untouched, in the proxy's shape (spec §5.7), and the request is kept in the transcript as the
/// curl that would send it (spec §5.11).
/// </summary>
public sealed class ProductImageUploader(HttpClient http, IOptions<AdminOptions> options, TimeProvider time, OperatorTranscript transcript)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    public string? Validate(ProductImageUploadRequest request)
    {
        if (!Uri.TryCreate(request.UploadUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{request.UploadUrl}' is not an absolute http(s) URL.";
        }

        string origin = uri.GetLeftPart(UriPartial.Authority);

        if (!Uri.TryCreate(options.Value.ObjectStoreUrl, UriKind.Absolute, out Uri? store)
            || !origin.Equals(store.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return $"{origin} is not the configured object store ({options.Value.ObjectStoreUrl}); the upload goes there alone.";
        }

        if (ContentTypeOf(request) is { } type && !MediaTypeHeaderValue.TryParse(type, out _))
        {
            return $"Content-Type '{type}' is not a media type.";
        }

        return null;
    }

    public async Task<ProxyResult> SendAsync(ProductImageUploadRequest request, CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        long started = time.GetTimestamp();

        using HttpRequestMessage message = new(HttpMethod.Put, request.UploadUrl)
        {
            Content = new ReadOnlyMemoryContent(SampleImage.Bytes),
        };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentTypeOf(request) ?? SampleImage.ContentType);

        // The bytes are a file in the document's line, so the transcript says which, as curl does.
        Action<int?> answered = transcript.Request($"{ShellLine.Curl(message, null)} --data-binary @{SampleImage.FileName}", null);
        int? status = null;

        try
        {
            using CancellationTokenSource deadline = new(SendTimeout, time);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

            try
            {
                using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                status = (int)response.StatusCode;
                Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

                foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
                {
                    headers[name] = [.. values];
                }

                // From here the store has answered; a body that breaks off is still its answer, as the proxy treats one.
                string body = "";
                string? bodyError = null;

                try
                {
                    body = await response.Content.ReadAsStringAsync(timeout.Token);
                }
                catch (Exception e) when (e is IOException or HttpRequestException)
                {
                    bodyError = e.Message;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    bodyError = $"The body did not finish within {SendTimeout.TotalSeconds:0} s.";
                }

                bool truncated = body.Length > RequestProxy.MaxBodyBytes;

                return new ProxyResponded(status.Value, headers, truncated ? body[..RequestProxy.MaxBodyBytes] : body,
                    truncated, bodyError, Elapsed(started), correlationId);
            }
            catch (HttpRequestException e)
            {
                return new ProxyUnreached(e.InnerException is { } inner ? $"{e.Message} {inner.Message}" : e.Message, Elapsed(started), correlationId, true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProxyUnreached($"No answer within {SendTimeout.TotalSeconds:0} s.", Elapsed(started), correlationId, true);
            }
        }
        finally
        {
            answered(status);
        }
    }

    private static string? ContentTypeOf(ProductImageUploadRequest request) =>
        request.Headers?.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

    private long Elapsed(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;
}
