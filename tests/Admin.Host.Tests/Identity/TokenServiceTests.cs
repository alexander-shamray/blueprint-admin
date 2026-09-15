using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Identity;
using Admin.Host.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Identity;

public sealed class TokenServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));

    internal static string Jwt(string payloadJson) =>
        $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"none"}"""))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson))}.sig";

    private static HttpResponseMessage Granted(string username, int expiresIn = 300) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{Jwt($$"""{"preferred_username":"{{username}}","permission":["catalog:write"]}""")}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""",
            Encoding.UTF8,
            "application/json"),
    };

    private TokenService Service(ScriptedHandler handler, AdminOptions? options = null) =>
        new(new HttpClient(handler), Options.Create(options ?? new AdminOptions()), time);

    [Fact]
    public async Task A_grant_posts_the_password_form_to_the_realm_token_endpoint_and_decodes_the_claims()
    {
        ScriptedHandler handler = new(_ => Granted("demo"));

        TokenOutcome outcome = await Service(handler).GetAsync("demo", "demo", Token);

        TokenIssued issued = outcome.ShouldBeOfType<TokenIssued>();
        issued.Username.ShouldBe("demo");
        issued.ExpiresAt.ShouldBe(time.GetUtcNow().AddSeconds(300));
        issued.Claims.GetProperty("permission")[0].GetString().ShouldBe("catalog:write");
        (HttpRequestMessage request, string? body) = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://localhost:8080/realms/commerce/protocol/openid-connect/token");
        body.ShouldBe("grant_type=password&client_id=web-app&username=demo&password=demo");
    }

    [Fact]
    public async Task A_token_is_reused_until_thirty_seconds_before_it_expires()
    {
        ScriptedHandler handler = new(_ => Granted("demo"));
        TokenService service = Service(handler);

        await service.GetAsync("demo", "demo", Token);
        time.Advance(TimeSpan.FromSeconds(269));
        await service.GetAsync("demo", "demo", Token);
        handler.Requests.Count.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await service.GetAsync("demo", "demo", Token);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_different_password_for_the_same_user_is_not_served_from_the_cache()
    {
        ScriptedHandler handler = new(_ => Granted("demo"));
        TokenService service = Service(handler);

        await service.GetAsync("demo", "demo", Token);
        await service.GetAsync("demo", "other", Token);

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refused_grant_returns_keycloaks_status_and_body_as_is()
    {
        const string Refusal = """{"error":"invalid_grant","error_description":"Invalid user credentials"}""";
        ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(Refusal, Encoding.UTF8, "application/json"),
        });

        TokenOutcome outcome = await Service(handler).GetAsync("demo", "wrong", Token);

        TokenRejected rejected = outcome.ShouldBeOfType<TokenRejected>();
        rejected.Status.ShouldBe(401);
        rejected.Body.ShouldBe(Refusal);
        rejected.ContentType.ShouldNotBeNull().ShouldStartWith("application/json");
    }

    [Fact]
    public async Task A_refused_grant_is_not_cached()
    {
        int calls = 0;
        ScriptedHandler handler = new(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Granted("demo"));
        TokenService service = Service(handler);

        await service.GetAsync("demo", "demo", Token);

        (await service.GetAsync("demo", "demo", Token)).ShouldBeOfType<TokenIssued>();
    }

    [Fact]
    public async Task Keycloak_not_answering_is_unreachable_not_an_exception()
    {
        ScriptedHandler handler = new(_ => throw new HttpRequestException("Connection refused"));

        TokenOutcome outcome = await Service(handler).GetAsync("demo", "demo", Token);

        outcome.ShouldBeOfType<KeycloakUnreachable>().Error.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task A_malformed_keycloak_url_is_unreachable()
    {
        ScriptedHandler handler = new(_ => Granted("demo"));

        TokenOutcome outcome = await Service(handler, new AdminOptions { KeycloakUrl = "localhost:8080" }).GetAsync("demo", "demo", Token);

        outcome.ShouldBeOfType<KeycloakUnreachable>();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_username_is_anonymous_and_mints_nothing()
    {
        ScriptedHandler handler = new(_ => Granted("demo"));

        (await Service(handler).ForAsync(null, Token)).ShouldBeNull();
        (await Service(handler).ForAsync(new IdentityRequest("", null), Token)).ShouldBeNull();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_named_user_without_a_password_uses_the_configured_password()
    {
        ScriptedHandler handler = new(_ => Granted("browser"));

        await Service(handler).ForAsync(new IdentityRequest("browser", null), Token);

        handler.Requests.ShouldHaveSingleItem().Body.ShouldBe("grant_type=password&client_id=web-app&username=browser&password=browser");
    }

    [Fact]
    public async Task An_unconfigured_user_without_a_password_is_unknown()
    {
        ScriptedHandler handler = new(_ => Granted("nobody"));

        (await Service(handler).ForAsync(new IdentityRequest("nobody", null), Token)).ShouldBeOfType<UnknownUser>().Username.ShouldBe("nobody");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_custom_identity_sends_the_given_password()
    {
        ScriptedHandler handler = new(_ => Granted("alice"));

        await Service(handler).ForAsync(new IdentityRequest("alice", "s3cret"), Token);

        handler.Requests.ShouldHaveSingleItem().Body.ShouldBe("grant_type=password&client_id=web-app&username=alice&password=s3cret");
    }

    [Fact]
    public void Configured_users_replace_the_defaults()
    {
        RealmUsers.Of(new AdminOptions()).Select(u => u.Username).ShouldBe(["demo", "browser"]);
        RealmUsers.Of(new AdminOptions { Users = [new RealmUser { Username = "ops", Password = "x" }] }).Select(u => u.Username).ShouldBe(["ops"]);
    }

    [Fact]
    public void A_token_that_is_not_three_base64url_parts_is_a_format_error()
    {
        Should.Throw<FormatException>(() => JwtPayload.Decode("not-a-jwt"));
        JwtPayload.Decode(Jwt("""{"a":1}""")).GetProperty("a").GetInt32().ShouldBe(1);
    }
}
