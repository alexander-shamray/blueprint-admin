using System.Net;
using Admin.Host.Api;
using Admin.Host.Config;
using Admin.Host.Tests.TestSupport;
using Admin.Host.Transcript;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Api;

/// <summary>What the uploader returns when the store answers and then its body breaks off: the store's status, as the proxy keeps one.</summary>
public sealed class ProductImageUploaderTests
{
    private static readonly ProductImageUploadRequest Request =
        new("http://127.0.0.1:8333/product-images/uploads/a/b.png?X-Amz-Signature=x", new Dictionary<string, string> { ["Content-Type"] = "image/png" });

    private static ProductImageUploader Uploader(Func<HttpResponseMessage> respond, FakeTimeProvider time) =>
        new(new HttpClient(new ScriptedHandler(_ => respond())), Options.Create(new AdminOptions()), time, new OperatorTranscript(TimeProvider.System));

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    public async Task A_body_that_breaks_off_keeps_the_stores_status_and_says_why(string failure)
    {
        Exception error = failure == "io" ? new IOException("The response ended prematurely.") : new HttpRequestException("Error while copying content to a stream.");

        ProxyResponded responded = (await Uploader(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new BreakingContent(error, null) }, new FakeTimeProvider())
            .SendAsync(Request, TestContext.Current.CancellationToken)).ShouldBeOfType<ProxyResponded>();

        responded.Status.ShouldBe(403);
        responded.BodyError.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_timeout_firing_during_the_body_read_is_responded_not_unreached()
    {
        FakeTimeProvider time = new();

        ProxyResponded responded = (await Uploader(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BreakingContent(null, time) }, time)
            .SendAsync(Request, TestContext.Current.CancellationToken)).ShouldBeOfType<ProxyResponded>();

        responded.Status.ShouldBe(200);
        responded.BodyError.ShouldNotBeNull().ShouldContain("30 s");
    }

    [Fact]
    public async Task A_complete_body_has_no_body_error()
    {
        ProxyResponded responded = (await Uploader(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") }, new FakeTimeProvider())
            .SendAsync(Request, TestContext.Current.CancellationToken)).ShouldBeOfType<ProxyResponded>();

        responded.BodyError.ShouldBeNull();
        responded.Body.ShouldBe("done");
    }

    [Fact]
    public async Task An_unknown_charset_is_read_as_utf8_not_a_500()
    {
        ByteArrayContent content = new("<Error>naïve</Error>"u8.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "application/xml; charset=no-such-charset");

        ProxyResponded responded = (await Uploader(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content }, new FakeTimeProvider())
            .SendAsync(Request, TestContext.Current.CancellationToken)).ShouldBeOfType<ProxyResponded>();

        responded.Status.ShouldBe(403);
        responded.Body.ShouldBe("<Error>naïve</Error>");
    }

    [Fact]
    public async Task A_long_body_is_cut_in_bytes_and_never_splits_a_character()
    {
        // One ASCII byte first puts the cut, at an even byte count, inside a two-byte é.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("a" + new string('é', RequestProxy.MaxBodyBytes));

        ProxyResponded responded = (await Uploader(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }, new FakeTimeProvider())
            .SendAsync(Request, TestContext.Current.CancellationToken)).ShouldBeOfType<ProxyResponded>();

        responded.BodyTruncated.ShouldBeTrue();
        responded.Body.Length.ShouldBe(1 + ((RequestProxy.MaxBodyBytes - 1) / 2));
        responded.Body[1..].ShouldAllBe(c => c == 'é');
    }

    /// <summary>Content whose read fails with <paramref name="failure"/>, or, with none, advances the clock past the deadline and waits.</summary>
    private sealed class BreakingContent(Exception? failure, FakeTimeProvider? time) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            time!.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        // The uploader reads a stream, as off the wire, so the read is given the deadline's token.
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            MemoryStream stream = new();
            await SerializeToStreamAsync(stream, null, cancellationToken);

            return stream;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
