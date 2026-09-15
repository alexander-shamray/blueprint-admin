using System.Net;
using System.Text;
using System.Text.Json;
using Admin.Host.Api;
using Admin.Host.Config;
using Admin.Host.Identity;
using Admin.Host.Tests.Identity;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class RequestProxyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string DemoToken = TokenServiceTests.Jwt("""{"preferred_username":"demo"}""");

    private static bool IsToken(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal);

    private static HttpResponseMessage Granted() => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"access_token":"{{DemoToken}}","expires_in":300}""", Encoding.UTF8, "application/json"),
    };

    private static RequestProxy Proxy(ScriptedHandler handler, AdminOptions? options = null, FakeTimeProvider? time = null)
    {
        IOptions<AdminOptions> wrapped = Options.Create(options ?? new AdminOptions());
        HttpClient http = new(handler);

        return new RequestProxy(http, new TokenService(http, wrapped, TimeProvider.System), wrapped, time ?? new FakeTimeProvider());
    }

    /// <summary>A 200 whose body yields <paramref name="prefix"/>, then runs <paramref name="then"/>, which is expected to throw.</summary>
    private static HttpResponseMessage BreaksAfter(string prefix, Func<CancellationToken, Task> then) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes(prefix), then)) };

    private static ProxyRequest Get(string url = "http://localhost:5000/api/v1/catalog/products", IdentityRequest? identity = null, string? correlationId = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new("GET", url, headers, null, identity, correlationId);

    [Theory]
    [InlineData("http://localhost:5000/api/v1/orders")]
    [InlineData("http://localhost:5102/health/ready")]
    [InlineData("http://localhost:5101/openapi/v1.json")]
    [InlineData("http://LOCALHOST:5200/health/ready")]
    public void Urls_on_the_four_api_surfaces_are_accepted(string url)
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(url)).ShouldBeNull();
    }

    [Theory]
    [InlineData("http://localhost:8080/realms/commerce", "is not one of the configured API surfaces")]
    [InlineData("http://localhost:3000/api/datasources", "is not one of the configured API surfaces")]
    [InlineData("http://example.com/", "is not one of the configured API surfaces")]
    [InlineData("http://localhost:5001/api/v1/orders", "is not one of the configured API surfaces")]
    [InlineData("https://localhost:5000/api/v1/orders", "is not one of the configured API surfaces")]
    [InlineData("/api/v1/orders", "absolute http(s) URL")]
    [InlineData("file:///C:/secrets.txt", "absolute http(s) URL")]
    [InlineData("http://localhost:5000@example.com/", "is not one of the configured API surfaces")]
    [InlineData("http://127.0.0.1:5000/api/v1/orders", "is not one of the configured API surfaces")]
    [InlineData("http://[::1]:5000/api/v1/orders", "is not one of the configured API surfaces")]
    public void Urls_off_the_api_surfaces_are_refused_so_the_proxy_is_not_an_open_relay(string url, string reason)
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(url)).ShouldNotBeNull().ShouldContain(reason);
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("get")]
    [InlineData("")]
    public void Only_the_standard_upper_case_methods_are_sent(string method)
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get() with { Method = method }).ShouldNotBeNull().ShouldContain("Method");
    }

    [Theory]
    [InlineData("abc-DEF_123", true)]
    [InlineData("has space", false)]
    [InlineData("dots.are.out", false)]
    [InlineData("ümlaut", false)]
    public void A_correlation_id_the_backend_would_replace_is_refused(string id, bool adoptable)
    {
        RequestProxy.IsAdoptable(id).ShouldBe(adoptable);
        (Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(correlationId: id)) is null).ShouldBe(adoptable);
    }

    [Fact]
    public void A_correlation_id_of_129_characters_is_refused()
    {
        RequestProxy.IsAdoptable(new string('a', 128)).ShouldBeTrue();
        RequestProxy.IsAdoptable(new string('a', 129)).ShouldBeFalse();
    }

    [Fact]
    public void A_correlation_header_in_headers_is_refused_in_favour_of_the_field()
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(headers: new Dictionary<string, string> { ["x-correlation-id"] = "abc" }))
            .ShouldNotBeNull().ShouldContain("correlationId");
    }

    [Fact]
    public void An_unparseable_content_type_is_refused()
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(headers: new Dictionary<string, string> { ["Content-Type"] = "not a media type;;" }))
            .ShouldNotBeNull().ShouldContain("Content-Type");
    }

    [Fact]
    public void A_header_given_twice_in_different_case_is_refused_by_name()
    {
        Dictionary<string, string> headers = new() { ["Content-Type"] = "text/plain", ["content-type"] = "not a media type;;" };

        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(headers: headers))
            .ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.ShouldContain("more than once"), p => p.ShouldContain("content-type", Case.Insensitive));
    }

    [Fact]
    public void An_unknown_named_user_is_refused_before_sending()
    {
        Proxy(new ScriptedHandler(_ => Granted())).Validate(Get(identity: new IdentityRequest("nobody", null)))
            .ShouldNotBeNull().ShouldContain("nobody");
    }

    [Fact]
    public async Task An_anonymous_get_is_sent_with_a_generated_correlation_id_and_the_answer_is_returned_untouched()
    {
        ScriptedHandler handler = new(request =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Correlation-Id", request.Headers.GetValues("X-Correlation-Id"));

            return response;
        });

        ProxyResult result = await Proxy(handler).SendAsync(Get(), Token);

        ProxyResponded responded = result.ShouldBeOfType<ProxyResponded>();
        responded.Status.ShouldBe(200);
        responded.Body.ShouldBe("""{"items":[]}""");
        responded.BodyTruncated.ShouldBeFalse();
        RequestProxy.IsAdoptable(responded.CorrelationId).ShouldBeTrue();
        responded.Headers["X-Correlation-Id"].ShouldBe([responded.CorrelationId]);
        responded.Headers["Content-Type"].ShouldHaveSingleItem().ShouldStartWith("application/json");
        HttpRequestMessage sent = handler.Requests.ShouldHaveSingleItem().Request;
        sent.Headers.Authorization.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Error_and_redirect_statuses_pass_through_with_their_bodies(HttpStatusCode status)
    {
        const string Problem = """{"status":409,"code":"command.already_committed"}""";
        ScriptedHandler handler = new(_ => new HttpResponseMessage(status) { Content = new StringContent(Problem, Encoding.UTF8, "application/problem+json") });

        ProxyResponded responded = (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyResponded>();

        responded.Status.ShouldBe((int)status);
        responded.Body.ShouldBe(Problem);
    }

    [Fact]
    public async Task A_named_identity_attaches_its_bearer_token_and_the_given_correlation_id_and_body()
    {
        ScriptedHandler handler = new(request => IsToken(request) ? Granted() : new HttpResponseMessage(HttpStatusCode.OK));
        ProxyRequest request = new(
            "POST",
            "http://localhost:5000/api/v1/catalog/products",
            new Dictionary<string, string> { ["Accept-Language"] = "en", ["Host"] = "evil.example" },
            """{"name":"Walnut desk"}""",
            new IdentityRequest("demo", null),
            "my-trace_1");

        ProxyResult result = await Proxy(handler).SendAsync(request, Token);

        result.CorrelationId.ShouldBe("my-trace_1");
        (HttpRequestMessage sent, string? body) = handler.Requests.Single(r => !IsToken(r.Request));
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Headers.Authorization!.ToString().ShouldBe($"Bearer {DemoToken}");
        sent.Headers.GetValues("X-Correlation-Id").ShouldBe(["my-trace_1"]);
        sent.Headers.GetValues("Accept-Language").ShouldBe(["en"]);
        sent.Headers.Host.ShouldBeNull();
        sent.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        body.ShouldBe("""{"name":"Walnut desk"}""");
    }

    [Fact]
    public async Task A_callers_authorization_header_is_replaced_by_the_named_identitys_bearer_token()
    {
        ScriptedHandler handler = new(request => IsToken(request) ? Granted() : new HttpResponseMessage(HttpStatusCode.OK));
        Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer pasted" };

        await Proxy(handler).SendAsync(Get(identity: new IdentityRequest("demo", null), headers: headers), Token);

        HttpRequestMessage sent = handler.Requests.Single(r => !IsToken(r.Request)).Request;
        sent.Headers.GetValues("Authorization").ShouldBe([$"Bearer {DemoToken}"]);
    }

    [Fact]
    public async Task A_given_content_type_replaces_the_json_default()
    {
        ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        ProxyRequest request = new("POST", "http://localhost:5000/bff/x", new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, "hi", null, null);

        await Proxy(handler).SendAsync(request, Token);

        handler.Requests.ShouldHaveSingleItem().Request.Content!.Headers.ContentType!.MediaType.ShouldBe("text/plain");
    }

    [Fact]
    public async Task A_refused_identity_is_a_token_rejection_and_nothing_reaches_the_platform()
    {
        ScriptedHandler handler = new(request => IsToken(request)
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_grant"}""") }
            : new HttpResponseMessage(HttpStatusCode.OK));

        ProxyResult result = await Proxy(handler).SendAsync(Get(identity: new IdentityRequest("demo", "wrong")), Token);

        ProxyTokenRejected rejected = result.ShouldBeOfType<ProxyTokenRejected>();
        rejected.Status.ShouldBe(401);
        rejected.Body.ShouldBe("""{"error":"invalid_grant"}""");
        handler.Requests.ShouldAllBe(r => IsToken(r.Request));
    }

    [Fact]
    public async Task A_refused_connection_is_unreached_with_the_reason()
    {
        ScriptedHandler handler = new(_ => throw new HttpRequestException("No connection could be made"));

        ProxyUnreached unreached = (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyUnreached>();

        unreached.Error.ShouldContain("No connection could be made");
    }

    [Fact]
    public async Task An_unreached_error_names_the_inner_reason_behind_a_generic_message()
    {
        ScriptedHandler handler = new(_ => throw new HttpRequestException("An error occurred while sending the request.", new IOException("Connection reset by peer")));

        ProxyUnreached unreached = (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyUnreached>();

        unreached.Error.ShouldContain("An error occurred while sending the request.");
        unreached.Error.ShouldContain("Connection reset by peer");
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    public async Task A_body_that_breaks_after_the_headers_is_responded_with_what_arrived_and_the_reason(string failure)
    {
        ScriptedHandler handler = new(_ => BreaksAfter("""{"items":[""", _ => failure == "io"
            ? throw new IOException("The response ended prematurely.")
            : throw new HttpRequestException("Error while copying content to a stream.")));

        ProxyResponded responded = (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyResponded>();

        responded.Status.ShouldBe(200);
        responded.Body.ShouldBe("""{"items":[""");
        responded.BodyTruncated.ShouldBeFalse();
        responded.BodyError.ShouldNotBeNull().ShouldContain(failure == "io" ? "ended prematurely" : "copying content");
    }

    [Fact]
    public async Task The_timeout_firing_during_the_body_read_is_responded_not_unreached()
    {
        FakeTimeProvider time = new();
        ScriptedHandler handler = new(_ => BreaksAfter("partial", async token =>
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(Timeout.Infinite, token);
        }));

        ProxyResponded responded = (await Proxy(handler, time: time).SendAsync(Get(), Token)).ShouldBeOfType<ProxyResponded>();

        responded.Body.ShouldBe("partial");
        responded.BodyError.ShouldNotBeNull().ShouldContain("30 s");
    }

    [Fact]
    public async Task A_body_read_completely_has_no_body_error()
    {
        ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") });

        (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyResponded>().BodyError.ShouldBeNull();
    }

    [Fact]
    public async Task The_callers_own_cancellation_during_the_body_read_still_throws()
    {
        using CancellationTokenSource caller = new();
        ScriptedHandler handler = new(_ => BreaksAfter("partial", async token =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        }));

        await Should.ThrowAsync<OperationCanceledException>(() => Proxy(handler).SendAsync(Get(), caller.Token));
    }

    [Fact]
    public async Task Elapsed_time_counts_from_after_the_token_grant()
    {
        FakeTimeProvider time = new();
        ScriptedHandler handler = new(request =>
        {
            time.Advance(IsToken(request) ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(40));

            return IsToken(request) ? Granted() : new HttpResponseMessage(HttpStatusCode.OK);
        });

        ProxyResult result = await Proxy(handler, time: time).SendAsync(Get(identity: new IdentityRequest("demo", null)), Token);

        result.ShouldBeOfType<ProxyResponded>().ElapsedMs.ShouldBe(40);
    }

    [Fact]
    public async Task Keycloak_down_is_unreached_too()
    {
        ScriptedHandler handler = new(request => IsToken(request) ? throw new HttpRequestException("refused") : new HttpResponseMessage(HttpStatusCode.OK));

        (await Proxy(handler).SendAsync(Get(identity: new IdentityRequest("demo", null)), Token))
            .ShouldBeOfType<ProxyUnreached>().Error.ShouldStartWith("Keycloak did not answer");
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_truncated_and_flagged()
    {
        ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', RequestProxy.MaxBodyBytes + 10)) });

        ProxyResponded responded = (await Proxy(handler).SendAsync(Get(), Token)).ShouldBeOfType<ProxyResponded>();

        responded.Body.Length.ShouldBe(RequestProxy.MaxBodyBytes);
        responded.BodyTruncated.ShouldBeTrue();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_result_serializes_with_an_outcome_discriminator()
    {
        JsonSerializer.Serialize<ProxyResult>(new ProxyUnreached("refused", 3, "c1"), Web)
            .ShouldBe("""{"outcome":"unreached","error":"refused","elapsedMs":3,"correlationId":"c1"}""");
    }

    private sealed class BreakingStream(byte[] prefix, Func<CancellationToken, Task> then) : Stream
    {
        private bool prefixSent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!prefixSent)
            {
                prefixSent = true;
                prefix.CopyTo(buffer);

                return prefix.Length;
            }

            await then(cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
