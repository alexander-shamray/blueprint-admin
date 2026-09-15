# Blueprint admin, phase 3: API — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The API screen lists every platform operation (Catalog and Ordering OpenAPI rebased onto the gateway, the BFF quote, each host's `/health/ready`), sends a request as a chosen identity through the host with a correlation id, and shows status, timing, headers and body exactly as the platform returned them, with a history — in the real mode and in FakePlatform mode.

**Architecture:** Three host services in two new namespaces. `Admin.Host.Identity.TokenService` performs the Keycloak password grant and caches tokens. `Admin.Host.Api.ApiCatalog` fetches `/openapi/v1.json` from Catalog and Ordering with a `demo` token and turns each document into `ApiOperation`s through a pure `OpenApiReader` (path rebasing, example body synthesis, edge policy from a cited copy of the gateway route table). `Admin.Host.Api.RequestProxy` validates and sends one request to a configured surface and returns a discriminated `ProxyResult`. The three share one named `HttpClient` (`platform`, no redirects, no cookies) whose primary handler is `FakePlatformHandler` in FakePlatform mode; the fake grows a Keycloak token endpoint, two recorded OpenAPI documents and a gateway. The SPA gets an `IdentityState`, typed client calls and an API screen at `/requests`.

**Tech Stack:** as phase 2 — .NET SDK 10.0.302, C# 14, minimal APIs, `System.Text.Json` (no new packages; OpenAPI is read as JSON, not through `Microsoft.OpenApi`), xunit.v3, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`; Angular 22.1.x, Vitest via `ng test`, Playwright 1.63.

**Spec:** `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` — §1 (the citation rule), §2.2–§2.4, §5.1 (`Users`), §5.6 Identity, §5.7 ApiCatalog and RequestProxy, §5.10 (`/identity/*`, `/catalog/*`, `/proxy`), §6 API screen, §8 (proxy is not an open relay), §9 (proxy error shape), §10, §12 phase 3. "Trace this call" (phase 5) and the drained indicator (phase 4) are out of scope.

**Backend facts this plan relies on** (read from the sibling clone on 2026-09-15; cite, do not restate elsewhere):

- Gateway routes, `blueprint-backend/src/Gateway/Gateway.Api/appsettings.json` `ReverseProxy:Routes`: `catalog-public` GET `/api/v1/catalog/{**}` anonymous; `catalog-write` POST same path authenticated; `ordering` any `/api/v1/orders/{**}` authenticated; `inventory-admin` any `/api/v1/inventory/{**}` `inventory:admin`; `web-bff` any `/bff/{**}` authenticated. `/api` and `/bff` are stripped.
- `/openapi/v1.json` on Catalog (5102) and Ordering (5101) needs a bearer token (fallback policy) and has no examples and no declared error responses (`AddOpenApi()` with no options). .NET 10 emits OpenAPI 3.1: nullable types are `"type": ["null","string"]`, numbers readable from strings are `"type": ["number","string"]` or `["integer","string"]`, Guids are `"type":"string","format":"uuid"`.
- Operations: Catalog `POST /v1/catalog/products/` (`PublishProduct`, `catalog:write`), `GET /v1/catalog/products/?cursor&limit` (`GetProducts`, anonymous); Ordering `POST /v1/orders/` (`PlaceOrder`, `orders:write`), `POST /v1/orders/{id}/cancel` (`CancelOrder`, `orders:cancel`). BFF has no OpenAPI; its one endpoint is `POST /v1/checkout/quote`.
- Correlation id: header `X-Correlation-Id` (`Common.Web.CorrelationIdExtensions.Header`); adopted only if 1–128 chars of ASCII letters, digits, `-`, `_` (`IsAdoptable`, `MaxSuppliedLength`); anything else is silently replaced by the trace id.
- Keycloak realm `commerce` (`deploy/compose/keycloak/realm-export.json`): client `web-app` public with direct access grants; `accessTokenLifespan` 300; permissions ride in the multivalued `permission` claim; `demo`/`demo` holds `catalog:write`, `orders:write`, `orders:cancel`; `browser`/`browser` holds none. A bad password is 401 `{"error":"invalid_grant","error_description":"Invalid user credentials"}`.
- Request bodies: `run-locally.md` (workspace root) lines 108–113 (publish), 121–124 (quote), 130–135 (place order), 141 (cancel).

**Decisions this plan makes where the spec is silent:**

- **Edge policy is a cited copy of the route table** (`Api/GatewayRoutes.cs`), not a runtime read of the backend's `appsettings.json`: FakePlatform runs with no clone, and §1.2 allows a hard-coded platform fact that cites its owner.
- **Example bodies are synthesized from the schema**, then overlaid by the four bodies `run-locally.md` shows (by `operationId`), because the documents carry no examples. `commandId` in an example is the zero Guid; the SPA replaces it with a fresh UUID on every send when the operation's body has one.
- **The proxy admits only the gateway, Catalog, Ordering and BFF origins** (scheme, host and port equal to a configured URL). Keycloak, Grafana and the client are not API surfaces; the host talks to them itself.
- **A supplied correlation id that the backend would not adopt is refused with 400**, because the backend would silently replace it and the console would report an id that joins nothing. A correlation id inside `headers` is refused too; it goes in `correlationId`.
- **`ProxyResult` has three shapes** discriminated by `outcome`: `responded` (status, headers, body — untouched), `unreached` (the request never got an answer: connection refused, timeout after 30 s, Keycloak down), `tokenRejected` (Keycloak refused the identity; its status and body). Host validation failures are RFC 9457 problems with 400. Bodies over 1 MiB are truncated and flagged.
- **Identity on the wire** is `{ username, password }`: no username is anonymous; a username with no password is looked up in `Admin:Users`; both given is a custom identity. `GET /api/identity/users` returns usernames only. `Admin:Users` defaults to `demo`/`demo` and `browser`/`browser` when configuration supplies none (a list initializer would be appended to by the configuration binder).
- **The catalog** loads on first `GET /api/catalog/operations` and on `POST /api/catalog/reload`. A source that fails keeps its last good operations marked `available: false`; one that never loaded is listed in `sources` with its error and contributes no operations. Catalog is fetched as the realm user named `demo`, else the first configured user.
- **History is in memory** on the page (newest first, last 50); leaving the screen clears it.

## Global Constraints

- .NET SDK pinned to `10.0.302` with `rollForward: disable`. `TreatWarningsAsErrors`; IDE0055, IDE0065, IDE0161 fail the build. **No column alignment** of `=` or `=>`.
- Every `.cs` file is CRLF. The Write tool emits LF, so after creating or editing `.cs` files and before every `dotnet build`/`dotnet test`, run `dotnet format whitespace BlueprintAdmin.slnx` from the repo root. Run `dotnet format BlueprintAdmin.slnx --verify-no-changes` before each commit.
- Test names are sentences with underscores.
- The host listens on `127.0.0.1:5300` only; never add a listener, CORS header or credential store (spec §8). Passwords live only in `AdminOptions` and the in-memory token cache.
- Nothing is invented that the backend owns: every hard-coded platform fact carries a comment naming its owner file and symbol, as in "Backend facts" above.
- FakePlatform is first-class: every new endpoint works against the fakes and the Playwright smoke covers the API screen.
- All `dotnet` commands run from the repository root; all `npm` commands from `src/Admin.Web`. Before claiming a task done: `dotnet test` green, and for SPA tasks `npm run lint`, `npm test`, `npm run build` green.
- Commit messages use `feat:`/`fix:`/`test:`/`docs:` prefixes and end with the attribution trailer the session provides. Before every commit, `git branch --show-current` must print `feat/phase-3-api` (the checkout is shared with other agents).
- Nothing in `../blueprint-backend` or `../blueprint-frontend` is edited.

---

## File structure

```
src/Admin.Host/Config/AdminOptions.cs                 Task 1  + Users
src/Admin.Host/Identity/RealmUser.cs                  Task 1  bindable user + RealmUsers.Of defaults
src/Admin.Host/Identity/JwtPayload.cs                 Task 1  base64url payload decode
src/Admin.Host/Identity/TokenService.cs               Task 1  password grant, cache, outcomes
tests/Admin.Host.Tests/Identity/TokenServiceTests.cs  Task 1
tests/Admin.Host.Tests/TestSupport/ScriptedHandler.cs Task 1  shared fake HttpMessageHandler
src/Admin.Host/Api/ApiOperation.cs                    Task 2  wire records
src/Admin.Host/Api/GatewayRoutes.cs                   Task 2  cited route table
src/Admin.Host/Api/ExampleBuilder.cs                  Task 2  schema → example JSON
src/Admin.Host/Api/RunLocallyExamples.cs              Task 2  the four run-locally bodies
src/Admin.Host/Api/OpenApiReader.cs                   Task 2  document → operations
src/Admin.Host/Api/CuratedOperations.cs               Task 2  BFF quote + health
src/Admin.Host/Fakes/fixtures/openapi-catalog.json    Task 2  recorded-shape documents
src/Admin.Host/Fakes/fixtures/openapi-ordering.json   Task 2
src/Admin.Host/Admin.Host.csproj                      Task 2  embed the two fixtures
tests/Admin.Host.Tests/Api/{GatewayRoutes,ExampleBuilder,OpenApiReader}Tests.cs Task 2
src/Admin.Host/Api/ApiCatalog.cs                      Task 3  load, cache, unavailable
tests/Admin.Host.Tests/Api/ApiCatalogTests.cs         Task 3
src/Admin.Host/Api/ProxyContracts.cs                  Task 4  ProxyRequest, ProxyResult family
src/Admin.Host/Api/RequestProxy.cs                    Task 4  validate + send
tests/Admin.Host.Tests/Api/RequestProxyTests.cs       Task 4
src/Admin.Host/Identity/IdentityEndpoints.cs          Task 5
src/Admin.Host/Api/ApiEndpoints.cs                    Task 5
src/Admin.Host/Fakes/FakePlatformHandler.cs           Task 5  dispatch by origin
src/Admin.Host/Fakes/FakeKeycloak.cs                  Task 5
src/Admin.Host/Fakes/FakeOpenApi.cs                   Task 5
src/Admin.Host/Fakes/FakeGateway.cs                   Task 5
src/Admin.Host/Program.cs                             Task 5  registrations + Map*
tests/Admin.Host.Tests/Api/ApiEndpointTests.cs        Task 5
tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs Task 5
src/Admin.Web/src/app/core/host/host-types.ts         Task 6
src/Admin.Web/src/app/core/host/host-client.ts(+spec) Task 6
src/Admin.Web/src/app/core/identity/identity-state.ts(+spec) Task 6
src/Admin.Web/src/app/features/api/request-builder.ts(+spec) Task 7  pure helpers
src/Admin.Web/src/app/features/api/api-page.{ts,html,css,spec.ts} Task 7
src/Admin.Web/src/app/app.routes.ts, app.html         Task 7  route + nav
src/Admin.Web/e2e/api.spec.ts                         Task 8
README.md, CLAUDE.md, spec §5.6/§5.7 amendments       Task 8
```

## Task 0 (controller, not a subagent): branch

- [x] `git switch -c feat/phase-3-api` from `main` at `d287687`, then commit this plan: `git add docs/superpowers/plans/2026-09-15-blueprint-admin-phase-3-api.md && git commit -m "docs: phase 3 API implementation plan"`.

---

### Task 1: TokenService — the password grant, cached

**Files:**
- Create: `src/Admin.Host/Identity/RealmUser.cs`, `src/Admin.Host/Identity/JwtPayload.cs`, `src/Admin.Host/Identity/TokenService.cs`
- Modify: `src/Admin.Host/Config/AdminOptions.cs` (add `Users` after `ClientId`)
- Create: `tests/Admin.Host.Tests/TestSupport/ScriptedHandler.cs`, `tests/Admin.Host.Tests/Identity/TokenServiceTests.cs`

**Interfaces:**
- Consumes: `AdminOptions` (`KeycloakUrl`, `Realm`, `ClientId`), `TimeProvider`.
- Produces:
  - `public sealed class RealmUser { string Username; string Password; }` (settable, for binding); `public static class RealmUsers { IReadOnlyList<RealmUser> Of(AdminOptions options); }`
  - `AdminOptions.Users : List<RealmUser>` (empty by default; `RealmUsers.Of` supplies demo/browser when empty)
  - `public sealed record IdentityRequest(string? Username, string? Password)`
  - `public abstract record TokenOutcome` with `TokenIssued(string Username, string AccessToken, DateTimeOffset ExpiresAt, JsonElement Claims)`, `TokenRejected(int Status, string Body, string? ContentType)`, `KeycloakUnreachable(string Error)`, `UnknownUser(string Username)`
  - `TokenService(HttpClient http, IOptions<AdminOptions> options, TimeProvider time)` with `Task<TokenOutcome?> ForAsync(IdentityRequest? identity, CancellationToken)` (null = anonymous) and `Task<TokenOutcome> GetAsync(string username, string password, CancellationToken)`
  - `public static class JwtPayload { JsonElement Decode(string jwt); }` — throws `FormatException` on a malformed token
  - Test support: `ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)` with a `Requests` list of `(HttpRequestMessage Request, string? Body)` captured before responding, and a sync-lambda convenience constructor.

- [ ] **Step 1: Shared test handler**

`tests/Admin.Host.Tests/TestSupport/ScriptedHandler.cs`:

```csharp
namespace Admin.Host.Tests.TestSupport;

/// <summary>An HttpMessageHandler that answers from a script and records what it was sent, bodies included.</summary>
public sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        HttpResponseMessage response = await respond(request, cancellationToken);
        response.RequestMessage ??= request;

        return response;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Admin.Host.Tests/Identity/TokenServiceTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run to confirm they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~TokenServiceTests"`
Expected: build FAIL — `Admin.Host.Identity` does not exist.

- [ ] **Step 4: Implement**

`src/Admin.Host/Identity/RealmUser.cs`:

```csharp
using Admin.Host.Config;

namespace Admin.Host.Identity;

/// <summary>One entry of <c>Admin:Users</c>, the identity picker's realm users (spec §2.4, §5.1).</summary>
public sealed class RealmUser
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}

public static class RealmUsers
{
    // Owner: blueprint-backend deploy/compose/keycloak/realm-export.json, users "demo" and "browser".
    private static readonly IReadOnlyList<RealmUser> Defaults =
    [
        new RealmUser { Username = "demo", Password = "demo" },
        new RealmUser { Username = "browser", Password = "browser" },
    ];

    /// <summary>
    /// The configured users, or the realm export's two when configuration names none. The defaults
    /// are not a list initializer on <see cref="AdminOptions.Users"/> because the configuration
    /// binder appends to an existing list instead of replacing it.
    /// </summary>
    public static IReadOnlyList<RealmUser> Of(AdminOptions options) => options.Users.Count > 0 ? options.Users : Defaults;
}
```

In `src/Admin.Host/Config/AdminOptions.cs`, add `using Admin.Host.Identity;` at the top and after `ClientId`:

```csharp
    /// <summary>The realm users offered by the identity picker; empty means demo/demo and browser/browser (see <c>Identity/RealmUser.cs</c>).</summary>
    public List<RealmUser> Users { get; set; } = [];
```

`src/Admin.Host/Identity/JwtPayload.cs`:

```csharp
using System.Buffers.Text;
using System.Text.Json;

namespace Admin.Host.Identity;

/// <summary>Reads a JWT's claims for display. Nothing here validates a signature; the platform does that.</summary>
public static class JwtPayload
{
    public static JsonElement Decode(string jwt)
    {
        string[] parts = jwt.Split('.');

        if (parts.Length != 3)
        {
            throw new FormatException("A JWT has three dot-separated parts.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));

            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException("The JWT payload is not JSON.", e);
        }
    }
}
```

`src/Admin.Host/Identity/TokenService.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Identity;

/// <summary>Who a request is sent as: no username is anonymous; a username alone is looked up in <c>Admin:Users</c>.</summary>
public sealed record IdentityRequest(string? Username, string? Password);

public abstract record TokenOutcome;

public sealed record TokenIssued(string Username, string AccessToken, DateTimeOffset ExpiresAt, JsonElement Claims) : TokenOutcome;

/// <summary>Keycloak answered with a non-success status; its body is passed through untouched (spec §9).</summary>
public sealed record TokenRejected(int Status, string Body, string? ContentType) : TokenOutcome;

public sealed record KeycloakUnreachable(string Error) : TokenOutcome;

public sealed record UnknownUser(string Username) : TokenOutcome;

/// <summary>
/// The password grant against realm <c>Admin:Realm</c> for client <c>Admin:ClientId</c>, as
/// run-locally.md's "Mint a token" does. Tokens are reused until 30 seconds before expiry and then
/// re-minted, never refreshed (spec §2.4, §5.6).
/// </summary>
public sealed class TokenService(HttpClient http, IOptions<AdminOptions> options, TimeProvider time)
{
    internal static readonly TimeSpan ReuseMargin = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan GrantTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string Username, string Password), TokenIssued> cache = new();

    public async Task<TokenOutcome?> ForAsync(IdentityRequest? identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identity?.Username))
        {
            return null;
        }

        string? password = identity.Password
            ?? RealmUsers.Of(options.Value).FirstOrDefault(u => u.Username == identity.Username)?.Password;

        return password is null
            ? new UnknownUser(identity.Username)
            : await GetAsync(identity.Username, password, cancellationToken);
    }

    public async Task<TokenOutcome> GetAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((username, password), out TokenIssued? cached) && time.GetUtcNow() < cached.ExpiresAt - ReuseMargin)
        {
            return cached;
        }

        AdminOptions o = options.Value;
        string url = $"{o.KeycloakUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(o.Realm)}/protocol/openid-connect/token";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new KeycloakUnreachable($"Admin:KeycloakUrl '{o.KeycloakUrl}' is not an absolute http(s) URL.");
        }

        using FormUrlEncodedContent form = new(
        [
            new("grant_type", "password"),
            new("client_id", o.ClientId),
            new("username", username),
            new("password", password),
        ]);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GrantTimeout);
        DateTimeOffset requestedAt = time.GetUtcNow();

        try
        {
            using HttpResponseMessage response = await http.PostAsync(uri, form, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new TokenRejected((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
            }

            using JsonDocument document = JsonDocument.Parse(body);
            string accessToken = document.RootElement.GetProperty("access_token").GetString()!;
            int expiresIn = document.RootElement.GetProperty("expires_in").GetInt32();
            TokenIssued issued = new(username, accessToken, requestedAt.AddSeconds(expiresIn), JwtPayload.Decode(accessToken));
            cache[(username, password)] = issued;

            return issued;
        }
        catch (HttpRequestException e)
        {
            return new KeycloakUnreachable(e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new KeycloakUnreachable($"No answer within {GrantTimeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new KeycloakUnreachable($"Keycloak answered with a body that is not a token response: {e.Message}");
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~TokenServiceTests"`
Expected: PASS (13). Then `dotnet test` — every earlier test still passes.

- [ ] **Step 6: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Identity src/Admin.Host/Config/AdminOptions.cs tests/Admin.Host.Tests/Identity tests/Admin.Host.Tests/TestSupport/ScriptedHandler.cs
git commit -m "feat(host): mint and cache realm tokens with the password grant"
```

---

### Task 2: OpenApiReader — documents become gateway operations

Pure functions and two fixture documents; no HTTP.

**Files:**
- Create: `src/Admin.Host/Api/ApiOperation.cs`, `GatewayRoutes.cs`, `ExampleBuilder.cs`, `RunLocallyExamples.cs`, `OpenApiReader.cs`, `CuratedOperations.cs` (all under `src/Admin.Host/Api/`)
- Create: `src/Admin.Host/Fakes/fixtures/openapi-catalog.json`, `src/Admin.Host/Fakes/fixtures/openapi-ordering.json`
- Modify: `src/Admin.Host/Admin.Host.csproj` (embed both fixtures)
- Create: `tests/Admin.Host.Tests/Api/GatewayRoutesTests.cs`, `ExampleBuilderTests.cs`, `OpenApiReaderTests.cs`

**Interfaces:**
- Consumes: `AdminOptions` (`GatewayUrl`, `CatalogUrl`, `OrderingUrl`, `BffUrl`).
- Produces:
  - `public sealed record ApiParameter(string Name, bool Required, string? Type)`
  - `public sealed record ApiOperation(string Id, string Source, string Name, string Method, string Url, IReadOnlyList<ApiParameter> PathParameters, IReadOnlyList<ApiParameter> QueryParameters, string? ExampleBody, bool HasCommandId, string EdgePolicy, bool Available)` — `Url` is absolute and may contain `{name}` path placeholders
  - `public sealed record ApiSource(string Name, string DocumentUrl, bool Available, string? Error)`
  - `public sealed record ApiCatalogView(IReadOnlyList<ApiSource> Sources, IReadOnlyList<ApiOperation> Operations)`
  - `GatewayRoutes.PolicyFor(string method, string gatewayPath) : string`; constants `GatewayRoutes.NoRoute = "no gateway route"`, `GatewayRoutes.Direct = "direct, anonymous"`
  - `ExampleBuilder.Build(JsonElement schema, JsonElement schemas) : JsonNode?` (`schemas` is `components.schemas`, or `default` when absent)
  - `RunLocallyExamples.For(string? operationId) : string?`; `RunLocallyExamples.Quote : string`
  - `OpenApiReader.Read(string source, JsonElement document, string gatewayUrl) : IReadOnlyList<ApiOperation>`
  - `CuratedOperations.All(AdminOptions options) : IReadOnlyList<ApiOperation>`
  - Embedded resources with logical names `openapi-catalog.json` and `openapi-ordering.json` (Task 5's fake serves them).

- [ ] **Step 1: The fixture documents**

They follow the shape `Microsoft.AspNetCore.OpenApi` 10 emits for the endpoints listed under "Backend facts" (OpenAPI 3.1, type arrays for nullable and string-readable numbers, no examples). Task 8 compares them with the live documents when the platform is up.

`src/Admin.Host/Fakes/fixtures/openapi-catalog.json`:

```json
{
  "openapi": "3.1.1",
  "info": { "title": "Catalog.Api | v1", "version": "1.0.0" },
  "servers": [ { "url": "http://localhost:5102/" } ],
  "paths": {
    "/v1/catalog/products/": {
      "post": {
        "tags": [ "Products" ],
        "operationId": "PublishProduct",
        "requestBody": {
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/PublishProductCommand" } } },
          "required": true
        },
        "responses": {
          "200": { "description": "OK", "content": { "application/json": { "schema": { "type": "string", "format": "uuid" } } } }
        }
      },
      "get": {
        "tags": [ "Products" ],
        "operationId": "GetProducts",
        "parameters": [
          { "name": "cursor", "in": "query", "schema": { "type": [ "null", "string" ] } },
          { "name": "limit", "in": "query", "schema": { "pattern": "^-?(?:0|[1-9]\\d*)$", "type": [ "integer", "string" ], "format": "int32", "default": 20 } }
        ],
        "responses": {
          "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CursorPageOfProductSummaryDto" } } } }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "CursorPageOfProductSummaryDto": {
        "required": [ "items", "nextCursor" ],
        "type": "object",
        "properties": {
          "items": { "type": "array", "items": { "$ref": "#/components/schemas/ProductSummaryDto" } },
          "nextCursor": { "type": [ "null", "string" ] }
        }
      },
      "ProductSummaryDto": {
        "required": [ "productId", "name", "thumbnailUrl", "amount", "currency", "publishedAt" ],
        "type": "object",
        "properties": {
          "productId": { "type": "string", "format": "uuid" },
          "name": { "type": "string" },
          "thumbnailUrl": { "type": [ "null", "string" ] },
          "amount": { "pattern": "^-?(?:0|[1-9]\\d*)(?:\\.\\d+)?$", "type": [ "number", "string" ], "format": "double" },
          "currency": { "type": "string" },
          "publishedAt": { "type": "string", "format": "date-time" }
        }
      },
      "PublishProductCommand": {
        "required": [ "commandId", "name", "thumbnailUrl", "amount", "currency" ],
        "type": "object",
        "properties": {
          "commandId": { "type": "string", "format": "uuid" },
          "name": { "type": "string" },
          "thumbnailUrl": { "type": [ "null", "string" ] },
          "amount": { "pattern": "^-?(?:0|[1-9]\\d*)(?:\\.\\d+)?$", "type": [ "null", "number", "string" ], "format": "double" },
          "currency": { "type": "string" }
        }
      }
    }
  },
  "tags": [ { "name": "Products" } ]
}
```

`src/Admin.Host/Fakes/fixtures/openapi-ordering.json`:

```json
{
  "openapi": "3.1.1",
  "info": { "title": "Ordering.Api | v1", "version": "1.0.0" },
  "servers": [ { "url": "http://localhost:5101/" } ],
  "paths": {
    "/v1/orders/": {
      "post": {
        "tags": [ "Orders" ],
        "operationId": "PlaceOrder",
        "requestBody": {
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/PlaceOrderCommand" } } },
          "required": true
        },
        "responses": {
          "200": { "description": "OK", "content": { "application/json": { "schema": { "type": "string", "format": "uuid" } } } }
        }
      }
    },
    "/v1/orders/{id}/cancel": {
      "post": {
        "tags": [ "Orders" ],
        "operationId": "CancelOrder",
        "parameters": [
          { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } }
        ],
        "requestBody": {
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CancelOrderRequest" } } },
          "required": true
        },
        "responses": { "200": { "description": "OK" } }
      }
    }
  },
  "components": {
    "schemas": {
      "AddressDto": {
        "required": [ "line1", "line2", "city", "postalCode", "country" ],
        "type": "object",
        "properties": {
          "line1": { "type": "string" },
          "line2": { "type": [ "null", "string" ] },
          "city": { "type": "string" },
          "postalCode": { "type": "string" },
          "country": { "type": "string" }
        }
      },
      "CancelOrderRequest": {
        "required": [ "reason" ],
        "type": "object",
        "properties": { "reason": { "type": "string" } }
      },
      "PlaceOrderCommand": {
        "required": [ "commandId", "items", "shippingAddress", "currency" ],
        "type": "object",
        "properties": {
          "commandId": { "type": "string", "format": "uuid" },
          "items": { "type": "array", "items": { "$ref": "#/components/schemas/PlaceOrderItem" } },
          "shippingAddress": { "$ref": "#/components/schemas/AddressDto" },
          "currency": { "type": "string" }
        }
      },
      "PlaceOrderItem": {
        "required": [ "productId", "quantity" ],
        "type": "object",
        "properties": {
          "productId": { "type": "string", "format": "uuid" },
          "quantity": { "pattern": "^-?(?:0|[1-9]\\d*)$", "type": [ "integer", "string" ], "format": "int32" }
        }
      }
    }
  },
  "tags": [ { "name": "Orders" } ]
}
```

In `src/Admin.Host/Admin.Host.csproj`, extend the existing `EmbeddedResource` item group:

```xml
    <EmbeddedResource Include="Fakes\fixtures\openapi-catalog.json" LogicalName="openapi-catalog.json" />
    <EmbeddedResource Include="Fakes\fixtures\openapi-ordering.json" LogicalName="openapi-ordering.json" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Admin.Host.Tests/Api/GatewayRoutesTests.cs`:

```csharp
using Admin.Host.Api;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class GatewayRoutesTests
{
    [Theory]
    [InlineData("GET", "/api/v1/catalog/products/", "anonymous")]
    [InlineData("GET", "/api/v1/catalog/products", "anonymous")]
    [InlineData("POST", "/api/v1/catalog/products/", "authenticated")]
    [InlineData("PUT", "/api/v1/catalog/products/", GatewayRoutes.NoRoute)]
    [InlineData("POST", "/api/v1/orders/", "authenticated")]
    [InlineData("DELETE", "/api/v1/orders/{id}/cancel", "authenticated")]
    [InlineData("GET", "/api/v1/inventory/items", "inventory:admin")]
    [InlineData("POST", "/bff/v1/checkout/quote", "authenticated")]
    [InlineData("GET", "/api/v1/unknown", GatewayRoutes.NoRoute)]
    [InlineData("GET", "/api/v1/catalogue", GatewayRoutes.NoRoute)]
    public void Each_method_and_path_gets_the_policy_of_the_route_that_matches(string method, string path, string policy)
    {
        GatewayRoutes.PolicyFor(method, path).ShouldBe(policy);
    }
}
```

`tests/Admin.Host.Tests/Api/ExampleBuilderTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Host.Api;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class ExampleBuilderTests
{
    private static string Build(string schema, string schemas = "{}")
    {
        using JsonDocument s = JsonDocument.Parse(schema);
        using JsonDocument c = JsonDocument.Parse(schemas);

        return ExampleBuilder.Build(s.RootElement, c.RootElement)?.ToJsonString() ?? "null";
    }

    [Theory]
    [InlineData("""{"type":"string"}""", "\"string\"")]
    [InlineData("""{"type":"string","format":"uuid"}""", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("""{"type":"string","format":"date-time"}""", "\"2026-01-01T00:00:00Z\"")]
    [InlineData("""{"type":["null","string"]}""", "\"string\"")]
    [InlineData("""{"type":["number","string"],"format":"double"}""", "0")]
    [InlineData("""{"type":["null","integer","string"]}""", "0")]
    [InlineData("""{"type":"boolean"}""", "false")]
    [InlineData("""{"type":"string","enum":["customer_request","out_of_stock"]}""", "\"customer_request\"")]
    [InlineData("""{"type":"integer","default":20}""", "20")]
    [InlineData("""{}""", "null")]
    public void Scalars_take_a_placeholder_of_their_type(string schema, string expected)
    {
        Build(schema).ShouldBe(expected);
    }

    [Fact]
    public void Objects_and_arrays_follow_references_in_property_order()
    {
        const string Schemas = """
        {
          "Order": { "type": "object", "properties": { "id": { "type": "string", "format": "uuid" }, "items": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } } } },
          "Item": { "type": "object", "properties": { "quantity": { "type": ["integer","string"] } } }
        }
        """;

        Build("""{"$ref":"#/components/schemas/Order"}""", Schemas)
            .ShouldBe("""{"id":"00000000-0000-0000-0000-000000000000","items":[{"quantity":0}]}""");
    }

    [Fact]
    public void A_reference_cycle_stops_at_null_instead_of_recursing()
    {
        const string Schemas = """{ "Node": { "type": "object", "properties": { "next": { "$ref": "#/components/schemas/Node" } } } }""";

        Build("""{"$ref":"#/components/schemas/Node"}""", Schemas).ShouldBe("""{"next":null}""");
    }

    [Fact]
    public void OneOf_with_a_null_branch_takes_the_other_branch()
    {
        Build("""{"oneOf":[{"type":"null"},{"type":"object","properties":{"a":{"type":"boolean"}}}]}""").ShouldBe("""{"a":false}""");
    }

    [Fact]
    public void AllOf_merges_the_properties_of_every_part()
    {
        Build("""{"allOf":[{"type":"object","properties":{"a":{"type":"boolean"}}},{"type":"object","properties":{"b":{"type":"string"}}}]}""")
            .ShouldBe("""{"a":false,"b":"string"}""");
    }

    [Fact]
    public void An_unresolvable_reference_is_null()
    {
        Build("""{"$ref":"#/components/schemas/Missing"}""").ShouldBe("null");

        using JsonDocument schema = JsonDocument.Parse("""{"$ref":"#/components/schemas/X"}""");
        JsonNode? withoutComponents = ExampleBuilder.Build(schema.RootElement, default);
        withoutComponents.ShouldBeNull();
    }
}
```

`tests/Admin.Host.Tests/Api/OpenApiReaderTests.cs`:

```csharp
using System.Text.Json;
using Admin.Host.Api;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class OpenApiReaderTests
{
    internal static JsonDocument Fixture(string name)
    {
        using Stream stream = typeof(AdminOptions).Assembly.GetManifestResourceStream(name)!;

        return JsonDocument.Parse(stream);
    }

    private static IReadOnlyList<ApiOperation> Read(string source, string fixture, string gateway = "http://localhost:5000")
    {
        using JsonDocument document = Fixture(fixture);

        return OpenApiReader.Read(source, document.RootElement, gateway);
    }

    [Fact]
    public void Catalog_operations_are_rebased_onto_the_gateway_with_their_edge_policy()
    {
        IReadOnlyList<ApiOperation> operations = Read("catalog", "openapi-catalog.json");

        operations.Select(o => (o.Id, o.Method, o.Url, o.EdgePolicy)).ShouldBe(
        [
            ("catalog:PublishProduct", "POST", "http://localhost:5000/api/v1/catalog/products/", "authenticated"),
            ("catalog:GetProducts", "GET", "http://localhost:5000/api/v1/catalog/products/", "anonymous"),
        ]);
        operations.ShouldAllBe(o => o.Source == "catalog" && o.Available);
    }

    [Fact]
    public void Query_and_path_parameters_are_listed_with_their_type()
    {
        ApiOperation list = Read("catalog", "openapi-catalog.json").Single(o => o.Name == "GetProducts");
        list.QueryParameters.ShouldBe([new ApiParameter("cursor", false, "string"), new ApiParameter("limit", false, "integer")]);
        list.PathParameters.ShouldBeEmpty();
        list.ExampleBody.ShouldBeNull();
        list.HasCommandId.ShouldBeFalse();

        ApiOperation cancel = Read("ordering", "openapi-ordering.json").Single(o => o.Name == "CancelOrder");
        cancel.Url.ShouldBe("http://localhost:5000/api/v1/orders/{id}/cancel");
        cancel.PathParameters.ShouldBe([new ApiParameter("id", true, "string")]);
    }

    [Fact]
    public void Run_locally_bodies_replace_the_synthesized_example_where_one_exists()
    {
        ApiOperation publish = Read("catalog", "openapi-catalog.json").Single(o => o.Name == "PublishProduct");

        JsonDocument.Parse(publish.ExampleBody!).RootElement.GetProperty("name").GetString().ShouldBe("Walnut desk");
        publish.HasCommandId.ShouldBeTrue();

        ApiOperation cancel = Read("ordering", "openapi-ordering.json").Single(o => o.Name == "CancelOrder");
        JsonDocument.Parse(cancel.ExampleBody!).RootElement.GetProperty("reason").GetString().ShouldBe("customer_request");
        cancel.HasCommandId.ShouldBeFalse();
    }

    [Fact]
    public void An_operation_without_a_run_locally_body_gets_a_synthesized_example()
    {
        const string Document = """
        { "paths": { "/v1/things/": { "put": { "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "commandId": { "type": "string", "format": "uuid" }, "count": { "type": ["integer","string"] } } } } } } } } } }
        """;
        using JsonDocument document = JsonDocument.Parse(Document);

        ApiOperation put = OpenApiReader.Read("catalog", document.RootElement, "http://localhost:5000/").ShouldHaveSingleItem();

        put.Name.ShouldBe("PUT /v1/things/");
        put.Id.ShouldBe("catalog:PUT /v1/things/");
        put.Url.ShouldBe("http://localhost:5000/api/v1/things/");
        put.EdgePolicy.ShouldBe(GatewayRoutes.NoRoute);
        JsonDocument.Parse(put.ExampleBody!).RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        put.HasCommandId.ShouldBeTrue();
    }

    [Fact]
    public void A_document_without_paths_has_no_operations()
    {
        using JsonDocument document = JsonDocument.Parse("""{"openapi":"3.1.1"}""");

        OpenApiReader.Read("catalog", document.RootElement, "http://localhost:5000").ShouldBeEmpty();
    }

    [Fact]
    public void Curated_operations_are_the_bff_quote_and_every_hosts_readiness()
    {
        IReadOnlyList<ApiOperation> curated = CuratedOperations.All(new AdminOptions());

        curated.Select(o => (o.Id, o.Method, o.Url, o.EdgePolicy)).ShouldBe(
        [
            ("bff:Quote", "POST", "http://localhost:5000/bff/v1/checkout/quote", "authenticated"),
            ("health:gateway", "GET", "http://localhost:5000/health/ready", GatewayRoutes.Direct),
            ("health:catalog", "GET", "http://localhost:5102/health/ready", GatewayRoutes.Direct),
            ("health:ordering", "GET", "http://localhost:5101/health/ready", GatewayRoutes.Direct),
            ("health:bff", "GET", "http://localhost:5200/health/ready", GatewayRoutes.Direct),
        ]);
        JsonDocument.Parse(curated[0].ExampleBody!).RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
    }
}
```

- [ ] **Step 3: Run to confirm they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Admin.Host.Tests.Api"`
Expected: build FAIL — `Admin.Host.Api` does not exist.

- [ ] **Step 4: Implement**

`src/Admin.Host/Api/ApiOperation.cs`:

```csharp
namespace Admin.Host.Api;

public sealed record ApiParameter(string Name, bool Required, string? Type);

/// <summary>
/// One call the API screen can make (spec §5.7). <see cref="Url"/> is absolute and may hold
/// <c>{name}</c> placeholders for <see cref="PathParameters"/>; <see cref="ExampleBody"/> is JSON text.
/// </summary>
public sealed record ApiOperation(
    string Id,
    string Source,
    string Name,
    string Method,
    string Url,
    IReadOnlyList<ApiParameter> PathParameters,
    IReadOnlyList<ApiParameter> QueryParameters,
    string? ExampleBody,
    bool HasCommandId,
    string EdgePolicy,
    bool Available);

public sealed record ApiSource(string Name, string DocumentUrl, bool Available, string? Error);

public sealed record ApiCatalogView(IReadOnlyList<ApiSource> Sources, IReadOnlyList<ApiOperation> Operations);
```

`src/Admin.Host/Api/GatewayRoutes.cs`:

```csharp
namespace Admin.Host.Api;

/// <summary>
/// The gateway's edge policy per route. Owner: blueprint-backend
/// <c>src/Gateway/Gateway.Api/appsettings.json</c>, <c>ReverseProxy:Routes</c> (catalog-public,
/// catalog-write, ordering, inventory-admin, web-bff). Update this table when a route changes there.
/// </summary>
public static class GatewayRoutes
{
    public const string NoRoute = "no gateway route";

    /// <summary>A call straight to a host's own port, such as <c>/health/ready</c>, which is anonymous on every host.</summary>
    public const string Direct = "direct, anonymous";

    // Method null is "any method". Prefixes end in '/' so /api/v1/catalogue does not match /api/v1/catalog.
    private static readonly (string? Method, string Prefix, string Policy)[] Routes =
    [
        ("GET", "/api/v1/catalog/", "anonymous"),
        ("POST", "/api/v1/catalog/", "authenticated"),
        (null, "/api/v1/orders/", "authenticated"),
        (null, "/api/v1/inventory/", "inventory:admin"),
        (null, "/bff/", "authenticated"),
    ];

    public static string PolicyFor(string method, string gatewayPath)
    {
        string path = gatewayPath.EndsWith('/') ? gatewayPath : gatewayPath + "/";

        foreach ((string? routeMethod, string prefix, string policy) in Routes)
        {
            if ((routeMethod is null || routeMethod == method) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return policy;
            }
        }

        return NoRoute;
    }
}
```

`src/Admin.Host/Api/ExampleBuilder.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Api;

/// <summary>
/// A placeholder value for an OpenAPI 3.1 schema, because the platform's documents carry no
/// examples. Understands <c>$ref</c> into <c>components.schemas</c>, type arrays (the null branch
/// and the string branch of a string-readable number are skipped), <c>enum</c>, <c>default</c>,
/// <c>oneOf</c>/<c>anyOf</c> (first non-null branch) and <c>allOf</c> (merged).
/// </summary>
public static class ExampleBuilder
{
    private const string RefPrefix = "#/components/schemas/";
    private const int MaxDepth = 10;

    public static JsonNode? Build(JsonElement schema, JsonElement schemas) => Build(schema, schemas, [], 0);

    private static JsonNode? Build(JsonElement schema, JsonElement schemas, HashSet<string> visiting, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > MaxDepth)
        {
            return null;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString() is string r && r.StartsWith(RefPrefix, StringComparison.Ordinal) ? r[RefPrefix.Length..] : "";

            if (name.Length == 0 || schemas.ValueKind != JsonValueKind.Object || !schemas.TryGetProperty(name, out JsonElement target) || !visiting.Add(name))
            {
                return null;
            }

            JsonNode? resolved = Build(target, schemas, visiting, depth + 1);
            visiting.Remove(name);

            return resolved;
        }

        if (schema.TryGetProperty("default", out JsonElement defaultValue))
        {
            return JsonNode.Parse(defaultValue.GetRawText());
        }

        if (schema.TryGetProperty("enum", out JsonElement values) && values.GetArrayLength() > 0)
        {
            return JsonNode.Parse(values[0].GetRawText());
        }

        foreach (string choice in (string[])["oneOf", "anyOf"])
        {
            if (schema.TryGetProperty(choice, out JsonElement branches))
            {
                JsonElement branch = branches.EnumerateArray().FirstOrDefault(b => !(b.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() == "null"));

                return Build(branch, schemas, visiting, depth + 1);
            }
        }

        if (schema.TryGetProperty("allOf", out JsonElement parts))
        {
            JsonObject merged = [];

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (Build(part, schemas, visiting, depth + 1) is JsonObject partObject)
                {
                    foreach ((string key, JsonNode? value) in partObject.ToList())
                    {
                        partObject.Remove(key);
                        merged[key] = value;
                    }
                }
            }

            return merged;
        }

        return TypeOf(schema) switch
        {
            "object" => BuildObject(schema, schemas, visiting, depth),
            "array" => new JsonArray(schema.TryGetProperty("items", out JsonElement items) ? Build(items, schemas, visiting, depth + 1) : null),
            "integer" or "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            "string" => JsonValue.Create(StringFor(schema)),
            _ => null,
        };
    }

    private static JsonObject BuildObject(JsonElement schema, JsonElement schemas, HashSet<string> visiting, int depth)
    {
        JsonObject result = [];

        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                result[property.Name] = Build(property.Value, schemas, visiting, depth + 1);
            }
        }

        return result;
    }

    /// <summary>The schema's type with <c>null</c> dropped and a numeric type preferred over the <c>string</c> the generator adds beside it.</summary>
    internal static string? TypeOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return schema.TryGetProperty("properties", out _) ? "object" : null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        string[] types = [.. type.EnumerateArray().Select(t => t.GetString()!).Where(t => t != "null")];

        return types.FirstOrDefault(t => t != "string") ?? types.FirstOrDefault();
    }

    private static string StringFor(JsonElement schema) =>
        (schema.TryGetProperty("format", out JsonElement format) ? format.GetString() : null) switch
        {
            "uuid" => "00000000-0000-0000-0000-000000000000",
            "date-time" => "2026-01-01T00:00:00Z",
            "date" => "2026-01-01",
            "uri" => "https://example.com/",
            _ => "string",
        };
}
```

Note on the `allOf` merge: a `JsonNode` can have one parent, so each value is removed from its part before it is added to `merged`.

`src/Admin.Host/Api/RunLocallyExamples.cs`:

```csharp
namespace Admin.Host.Api;

/// <summary>
/// The request bodies run-locally.md (workspace root, "Call the APIs") sends, keyed by operationId.
/// The zero Guids stand for "a fresh commandId" and "a product or order id you have"; the SPA
/// replaces <c>commandId</c> on every send. Update these when that document changes.
/// </summary>
public static class RunLocallyExamples
{
    /// <summary>run-locally.md, "Quote a basket".</summary>
    public const string Quote = """
        {
          "currency": "EUR",
          "lines": [ { "productId": "00000000-0000-0000-0000-000000000000", "quantity": 1 } ]
        }
        """;

    private static readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal)
    {
        // "Publish a product".
        ["PublishProduct"] = """
            {
              "commandId": "00000000-0000-0000-0000-000000000000",
              "name": "Walnut desk",
              "amount": 19.99,
              "currency": "EUR"
            }
            """,

        // "Place an order".
        ["PlaceOrder"] = """
            {
              "commandId": "00000000-0000-0000-0000-000000000000",
              "items": [ { "productId": "00000000-0000-0000-0000-000000000000", "quantity": 1 } ],
              "shippingAddress": { "line1": "1 Test Street", "city": "Almaty", "postalCode": "050000", "country": "KZ" },
              "currency": "EUR"
            }
            """,

        // "Cancel".
        ["CancelOrder"] = """
            {
              "reason": "customer_request"
            }
            """,
    };

    public static string? For(string? operationId) => operationId is not null && Bodies.TryGetValue(operationId, out string? body) ? body : null;
}
```

`src/Admin.Host/Api/OpenApiReader.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Host.Api;

/// <summary>
/// Turns a service's OpenAPI document into operations addressed through the gateway, which adds
/// <c>/api</c> in front of the service's own path (Gateway.Api appsettings.json, <c>PathRemovePrefix: /api</c>).
/// </summary>
public static class OpenApiReader
{
    private const string GatewayPrefix = "/api";

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<ApiOperation> Read(string source, JsonElement document, string gatewayUrl)
    {
        List<ApiOperation> operations = [];

        if (!document.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object)
        {
            return operations;
        }

        JsonElement schemas = document.TryGetProperty("components", out JsonElement components) && components.TryGetProperty("schemas", out JsonElement s) ? s : default;

        foreach (JsonProperty path in paths.EnumerateObject())
        {
            foreach (JsonProperty entry in path.Value.EnumerateObject().Where(e => Methods.Contains(e.Name)))
            {
                string method = entry.Name.ToUpperInvariant();
                string? operationId = entry.Value.TryGetProperty("operationId", out JsonElement id) ? id.GetString() : null;
                string name = operationId ?? $"{method} {path.Name}";
                string gatewayPath = GatewayPrefix + path.Name;
                List<(string In, ApiParameter Parameter)> parameters = [.. Parameters(path.Value), .. Parameters(entry.Value)];
                string? example = RunLocallyExamples.For(operationId) ?? Example(entry.Value, schemas);

                operations.Add(new ApiOperation(
                    $"{source}:{name}",
                    source,
                    name,
                    method,
                    gatewayUrl.TrimEnd('/') + gatewayPath,
                    [.. parameters.Where(p => p.In == "path").Select(p => p.Parameter)],
                    [.. parameters.Where(p => p.In == "query").Select(p => p.Parameter)],
                    example,
                    HasCommandId(example),
                    GatewayRoutes.PolicyFor(method, gatewayPath),
                    true));
            }
        }

        return operations;
    }

    private static IEnumerable<(string In, ApiParameter Parameter)> Parameters(JsonElement owner)
    {
        if (!owner.TryGetProperty("parameters", out JsonElement parameters))
        {
            yield break;
        }

        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("name", out JsonElement name) && parameter.TryGetProperty("in", out JsonElement location))
            {
                bool required = parameter.TryGetProperty("required", out JsonElement r) && r.ValueKind == JsonValueKind.True;
                string? type = parameter.TryGetProperty("schema", out JsonElement schema) ? ExampleBuilder.TypeOf(schema) : null;

                yield return (location.GetString()!, new ApiParameter(name.GetString()!, required, type));
            }
        }
    }

    private static string? Example(JsonElement operation, JsonElement schemas)
    {
        if (operation.TryGetProperty("requestBody", out JsonElement body)
            && body.TryGetProperty("content", out JsonElement content)
            && content.TryGetProperty("application/json", out JsonElement json)
            && json.TryGetProperty("schema", out JsonElement schema))
        {
            return ExampleBuilder.Build(schema, schemas)?.ToJsonString(Indented);
        }

        return null;
    }

    private static bool HasCommandId(string? example)
    {
        if (example is null)
        {
            return false;
        }

        return JsonNode.Parse(example) is JsonObject body && body.ContainsKey("commandId");
    }
}
```

`src/Admin.Host/Api/CuratedOperations.cs`:

```csharp
using Admin.Host.Config;

namespace Admin.Host.Api;

/// <summary>
/// Operations no OpenAPI document describes (spec §2.3): the Web BFF's quote, which has no
/// document (blueprint-backend <c>src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs</c>), and every
/// host's readiness check (<c>Common.Web.HealthCheckExtensions</c>), called on the host's own port.
/// </summary>
public static class CuratedOperations
{
    public static IReadOnlyList<ApiOperation> All(AdminOptions options) =>
    [
        new ApiOperation(
            "bff:Quote",
            "bff",
            "Quote",
            "POST",
            Join(options.GatewayUrl, "/bff/v1/checkout/quote"),
            [],
            [],
            RunLocallyExamples.Quote,
            false,
            GatewayRoutes.PolicyFor("POST", "/bff/v1/checkout/quote"),
            true),
        Health("gateway", options.GatewayUrl),
        Health("catalog", options.CatalogUrl),
        Health("ordering", options.OrderingUrl),
        Health("bff", options.BffUrl),
    ];

    private static ApiOperation Health(string host, string baseUrl) =>
        new($"health:{host}", "health", $"{host} ready", "GET", Join(baseUrl, "/health/ready"), [], [], null, false, GatewayRoutes.Direct, true);

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~Admin.Host.Tests.Api"`
Expected: PASS. Then `dotnet test` — all green.

- [ ] **Step 6: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Api src/Admin.Host/Fakes/fixtures/openapi-*.json src/Admin.Host/Admin.Host.csproj tests/Admin.Host.Tests/Api
git commit -m "feat(host): read service OpenAPI documents into gateway operations"
```

---

### Task 3: ApiCatalog — load, cache, and keep what went down

**Files:**
- Create: `src/Admin.Host/Api/ApiCatalog.cs`
- Create: `tests/Admin.Host.Tests/Api/ApiCatalogTests.cs`

**Interfaces:**
- Consumes: `TokenService.ForAsync`, `TokenIssued`, `TokenRejected`, `KeycloakUnreachable`, `UnknownUser`, `IdentityRequest`, `RealmUsers.Of` (Task 1); `OpenApiReader.Read`, `CuratedOperations.All`, `ApiCatalogView`, `ApiSource` (Task 2); `ScriptedHandler` (Task 1); `OpenApiReaderTests.Fixture(name)` (Task 2).
- Produces: `ApiCatalog(HttpClient http, TokenService tokens, IOptions<AdminOptions> options)` with `Task<ApiCatalogView> GetAsync(CancellationToken)` (loads on first call) and `Task<ApiCatalogView> ReloadAsync(CancellationToken)`. Sources are always listed in the order `catalog`, `ordering`; operations are catalog's, ordering's, then curated.

- [ ] **Step 1: Write the failing tests**

`tests/Admin.Host.Tests/Api/ApiCatalogTests.cs`:

```csharp
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

public sealed class ApiCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static ApiCatalog Catalog(ScriptedHandler handler, AdminOptions? options = null)
    {
        IOptions<AdminOptions> wrapped = Options.Create(options ?? new AdminOptions());
        HttpClient http = new(handler);

        return new ApiCatalog(http, new TokenService(http, wrapped, TimeProvider.System), wrapped);
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

        view.Sources.ShouldBe(
        [
            new ApiSource("catalog", "http://localhost:5102/openapi/v1.json", true, null),
            new ApiSource("ordering", "http://localhost:5101/openapi/v1.json", true, null),
        ]);
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
    public async Task The_catalog_identity_is_the_first_configured_user_when_there_is_no_demo()
    {
        ScriptedHandler handler = Platform(Both);
        AdminOptions options = new() { Users = [new RealmUser { Username = "ops", Password = "pw" }] };

        await Catalog(handler, options).GetAsync(Token);

        handler.Requests.Single(r => r.Body != null).Body!.ShouldContain("username=ops&password=pw");
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~ApiCatalogTests"`
Expected: build FAIL — `ApiCatalog` does not exist.

- [ ] **Step 3: Implement**

`src/Admin.Host/Api/ApiCatalog.cs`:

```csharp
using System.Text.Json;
using Admin.Host.Config;
using Admin.Host.Identity;
using Microsoft.Extensions.Options;

namespace Admin.Host.Api;

/// <summary>
/// The API screen's operation tree (spec §5.7): Catalog's and Ordering's OpenAPI documents, which
/// need a token (the services' fallback authorization policy), rebased onto the gateway, then the
/// curated operations. Cached until reloaded; a service that stops answering keeps its last
/// operations, marked unavailable, rather than vanishing from the tree.
/// </summary>
public sealed class ApiCatalog(HttpClient http, TokenService tokens, IOptions<AdminOptions> options)
{
    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<ApiOperation>> lastGood = [];
    private ApiCatalogView? current;

    public async Task<ApiCatalogView> GetAsync(CancellationToken cancellationToken) =>
        current ?? await ReloadAsync(cancellationToken);

    public async Task<ApiCatalogView> ReloadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            AdminOptions o = options.Value;
            (string Name, string BaseUrl)[] services = [("catalog", o.CatalogUrl), ("ordering", o.OrderingUrl)];

            // One grant for both documents: two concurrent loads would each miss the empty cache and mint twice.
            (string? bearer, string? tokenError) = await BearerAsync(o, cancellationToken);
            (ApiSource Source, IReadOnlyList<ApiOperation>? Operations)[] loads =
                await Task.WhenAll(services.Select(s => LoadAsync(s.Name, s.BaseUrl, o, bearer, tokenError, cancellationToken)));
            List<ApiOperation> operations = [];

            foreach ((ApiSource source, IReadOnlyList<ApiOperation>? loaded) in loads)
            {
                if (loaded is not null)
                {
                    lastGood[source.Name] = loaded;
                }

                if (lastGood.TryGetValue(source.Name, out IReadOnlyList<ApiOperation>? known))
                {
                    operations.AddRange(source.Available ? known : known.Select(op => op with { Available = false }));
                }
            }

            operations.AddRange(CuratedOperations.All(o));
            current = new ApiCatalogView([.. loads.Select(l => l.Source)], operations);

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A token for the realm user named demo, else the first configured user; or why there is none.</summary>
    private async Task<(string? Bearer, string? Error)> BearerAsync(AdminOptions o, CancellationToken cancellationToken)
    {
        IReadOnlyList<RealmUser> users = RealmUsers.Of(o);
        RealmUser? user = users.FirstOrDefault(u => u.Username == "demo") ?? users.FirstOrDefault();

        if (user is null)
        {
            return (null, "No realm user is configured to fetch the document with.");
        }

        return await tokens.GetAsync(user.Username, user.Password, cancellationToken) switch
        {
            TokenIssued issued => (issued.AccessToken, null),
            TokenRejected rejected => (null, $"The token request for {user.Username} answered {rejected.Status}."),
            KeycloakUnreachable unreachable => (null, $"Keycloak did not answer: {unreachable.Error}"),
            _ => (null, $"No token for {user.Username}."),
        };
    }

    private async Task<(ApiSource, IReadOnlyList<ApiOperation>?)> LoadAsync(
        string name, string baseUrl, AdminOptions o, string? bearer, string? tokenError, CancellationToken cancellationToken)
    {
        string documentUrl = baseUrl.TrimEnd('/') + "/openapi/v1.json";
        (ApiSource, IReadOnlyList<ApiOperation>?) Unavailable(string error) => (new ApiSource(name, documentUrl, false, error), null);

        if (!Uri.TryCreate(documentUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Unavailable($"{documentUrl} is not an absolute http(s) URL.");
        }

        if (bearer is null)
        {
            return Unavailable(tokenError ?? "No token.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DocumentTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Authorization = new("Bearer", bearer);
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"{documentUrl} answered {(int)response.StatusCode}.");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            return (new ApiSource(name, documentUrl, true, null), OpenApiReader.Read(name, document.RootElement, o.GatewayUrl));
        }
        catch (HttpRequestException e)
        {
            return Unavailable(e.Message);
        }
        catch (JsonException e)
        {
            return Unavailable($"{documentUrl} is not JSON: {e.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable($"{documentUrl} did not answer within {DocumentTimeout.TotalSeconds:0} s.");
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~ApiCatalogTests"`
Expected: PASS (7). Then `dotnet test` — all green.

- [ ] **Step 5: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Api/ApiCatalog.cs tests/Admin.Host.Tests/Api/ApiCatalogTests.cs
git commit -m "feat(host): cache the API catalog and keep a downed service's operations"
```

---

### Task 4: RequestProxy — one request, status and body untouched

**Files:**
- Create: `src/Admin.Host/Api/ProxyContracts.cs`, `src/Admin.Host/Api/RequestProxy.cs`
- Create: `tests/Admin.Host.Tests/Api/RequestProxyTests.cs`

**Interfaces:**
- Consumes: `TokenService.ForAsync`, `IdentityRequest`, the `TokenOutcome` family, `RealmUsers.Of` (Task 1); `ScriptedHandler`, `TokenServiceTests.Jwt` (Task 1).
- Produces:
  - `public sealed record ProxyRequest(string Method, string Url, IReadOnlyDictionary<string, string>? Headers, string? Body, IdentityRequest? Identity, string? CorrelationId)`
  - `[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")] public abstract record ProxyResult(string CorrelationId)` with
    `ProxyResponded(int Status, IReadOnlyDictionary<string, string[]> Headers, string Body, bool BodyTruncated, long ElapsedMs, string CorrelationId)` → `"responded"`,
    `ProxyUnreached(string Error, long ElapsedMs, string CorrelationId)` → `"unreached"`,
    `ProxyTokenRejected(int Status, string Body, string CorrelationId)` → `"tokenRejected"`
  - `RequestProxy(HttpClient http, TokenService tokens, IOptions<AdminOptions> options, TimeProvider time)` with `string? Validate(ProxyRequest request)` (null when sendable, else the problem detail) and `Task<ProxyResult> SendAsync(ProxyRequest request, CancellationToken)`
  - `RequestProxy.CorrelationHeader = "X-Correlation-Id"`, `RequestProxy.IsAdoptable(string id)`, `RequestProxy.MaxBodyBytes = 1_048_576`

- [ ] **Step 1: Write the failing tests**

`tests/Admin.Host.Tests/Api/RequestProxyTests.cs`:

```csharp
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

    private static RequestProxy Proxy(ScriptedHandler handler, AdminOptions? options = null)
    {
        IOptions<AdminOptions> wrapped = Options.Create(options ?? new AdminOptions());
        HttpClient http = new(handler);

        return new RequestProxy(http, new TokenService(http, wrapped, TimeProvider.System), wrapped, new FakeTimeProvider());
    }

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

    [Fact]
    public void The_result_serializes_with_an_outcome_discriminator()
    {
        JsonSerializerOptions web = new(JsonSerializerDefaults.Web);

        JsonSerializer.Serialize<ProxyResult>(new ProxyUnreached("refused", 3, "c1"), web)
            .ShouldBe("""{"outcome":"unreached","error":"refused","elapsedMs":3,"correlationId":"c1"}""");
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~RequestProxyTests"`
Expected: build FAIL — `RequestProxy` does not exist.

- [ ] **Step 3: Implement**

`src/Admin.Host/Api/ProxyContracts.cs`:

```csharp
using System.Text.Json.Serialization;
using Admin.Host.Identity;

namespace Admin.Host.Api;

/// <summary>One request for <c>POST /api/proxy</c> (spec §5.7). No identity, or one with no username, is anonymous.</summary>
public sealed record ProxyRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Headers,
    string? Body,
    IdentityRequest? Identity,
    string? CorrelationId);

/// <summary>
/// What became of a proxied request. <c>responded</c> carries the platform's answer untouched;
/// the other two are the "never reached the upstream" shape spec §9 asks the SPA to render differently.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(ProxyResponded), "responded")]
[JsonDerivedType(typeof(ProxyUnreached), "unreached")]
[JsonDerivedType(typeof(ProxyTokenRejected), "tokenRejected")]
public abstract record ProxyResult(string CorrelationId);

public sealed record ProxyResponded(int Status, IReadOnlyDictionary<string, string[]> Headers, string Body, bool BodyTruncated, long ElapsedMs, string CorrelationId)
    : ProxyResult(CorrelationId);

public sealed record ProxyUnreached(string Error, long ElapsedMs, string CorrelationId) : ProxyResult(CorrelationId);

/// <summary>Keycloak refused the identity; its status and body, and the request was not sent.</summary>
public sealed record ProxyTokenRejected(int Status, string Body, string CorrelationId) : ProxyResult(CorrelationId);
```

If the serialization test shows `correlationId` before the derived properties, reorder the expected JSON to match what System.Text.Json emits (base-record properties may serialize first) — the property set is what matters; do not restructure the records.

`src/Admin.Host/Api/RequestProxy.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using Admin.Host.Config;
using Admin.Host.Identity;
using Microsoft.Extensions.Options;

namespace Admin.Host.Api;

/// <summary>
/// Sends one request to the gateway, Catalog, Ordering or the BFF as a chosen identity with a
/// correlation id, and returns the answer without rewriting status or body (spec §5.7, §8).
/// </summary>
public sealed class RequestProxy(HttpClient http, TokenService tokens, IOptions<AdminOptions> options, TimeProvider time)
{
    /// <summary>Owner: blueprint-backend <c>Common.Web.CorrelationIdExtensions.Header</c>.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    public const int MaxBodyBytes = 1_048_576;

    /// <summary>Owner: <c>Common.Web.CorrelationIdExtensions.MaxSuppliedLength</c>.</summary>
    private const int MaxCorrelationIdLength = 128;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    // Set by HttpClient from the URL and the body, never by the caller.
    private static readonly HashSet<string> Dropped = new(["Host", "Content-Length", "Transfer-Encoding", "Connection"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The backend's adoption rule, <c>CorrelationIdExtensions.IsAdoptable</c>: 1–128 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsAdoptable(string id) =>
        id.Length is >= 1 and <= MaxCorrelationIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public string? Validate(ProxyRequest request)
    {
        if (!Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return $"Method '{request.Method}' is not one of {string.Join(", ", Methods)}.";
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"'{request.Url}' is not an absolute http(s) URL.";
        }

        string origin = uri.GetLeftPart(UriPartial.Authority);

        if (!Surfaces().Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return $"{origin} is not one of the configured API surfaces ({string.Join(", ", Surfaces())}).";
        }

        if (request.CorrelationId is { Length: > 0 } id && !IsAdoptable(id))
        {
            return "The correlation id must be 1 to 128 ASCII letters, digits, '-' or '_'; the platform would replace any other value.";
        }

        IReadOnlyDictionary<string, string> headers = request.Headers ?? new Dictionary<string, string>();

        if (headers.Keys.Any(k => k.Equals(CorrelationHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Send the correlation id in correlationId, not as an {CorrelationHeader} header.";
        }

        if (headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { Key: not null } contentType
            && !MediaTypeHeaderValue.TryParse(contentType.Value, out _))
        {
            return $"Content-Type '{contentType.Value}' is not a media type.";
        }

        if (request.Identity is { Username: { Length: > 0 } username, Password: null }
            && !RealmUsers.Of(options.Value).Any(u => u.Username == username))
        {
            return $"'{username}' is not a configured realm user; send a password to use it.";
        }

        return null;
    }

    public async Task<ProxyResult> SendAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        string correlationId = request.CorrelationId is { Length: > 0 } given ? given : Guid.NewGuid().ToString("N");
        long started = time.GetTimestamp();

        switch (await tokens.ForAsync(request.Identity, cancellationToken))
        {
            case TokenRejected rejected:
                return new ProxyTokenRejected(rejected.Status, rejected.Body, correlationId);
            case UnknownUser unknown:
                return new ProxyTokenRejected(400, $"'{unknown.Username}' is not a configured realm user.", correlationId);
            case KeycloakUnreachable unreachable:
                return new ProxyUnreached($"Keycloak did not answer: {unreachable.Error}", Elapsed(started), correlationId);
            case var outcome:
                using (HttpRequestMessage message = Build(request, outcome as TokenIssued, correlationId))
                {
                    return await SendAsync(message, started, correlationId, cancellationToken);
                }
        }
    }

    private static HttpRequestMessage Build(ProxyRequest request, TokenIssued? token, string correlationId)
    {
        HttpRequestMessage message = new(new HttpMethod(request.Method), request.Url);

        if (request.Body is { Length: > 0 } body)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        foreach ((string name, string value) in request.Headers ?? new Dictionary<string, string>())
        {
            if (Dropped.Contains(name))
            {
                continue;
            }

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content?.Headers.ContentType = MediaTypeHeaderValue.Parse(value);

                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        message.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        return message;
    }

    private async Task<ProxyResult> SendAsync(HttpRequestMessage message, long started, string correlationId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            (string body, bool truncated) = await ReadBodyAsync(response.Content, timeout.Token);
            Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
            {
                headers[name] = [.. values];
            }

            return new ProxyResponded((int)response.StatusCode, headers, body, truncated, Elapsed(started), correlationId);
        }
        catch (HttpRequestException e)
        {
            return new ProxyUnreached(e.Message, Elapsed(started), correlationId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyUnreached($"No answer within {SendTimeout.TotalSeconds:0} s.", Elapsed(started), correlationId);
        }
    }

    private static async Task<(string Body, bool Truncated)> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        int count;

        while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += count;
        }

        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxBodyBytes)), read > MaxBodyBytes);
    }

    private long Elapsed(long started) => (long)time.GetElapsedTime(started).TotalMilliseconds;

    private string[] Surfaces()
    {
        AdminOptions o = options.Value;

        return
        [
            .. new[] { o.GatewayUrl, o.CatalogUrl, o.OrderingUrl, o.BffUrl }
                .Select(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
                .OfType<string>(),
        ];
    }
}
```

`message.Content?.Headers.ContentType = ...` is C# 14 null-conditional assignment; it compiles on SDK 10.0.302. A `Content-Type` header with no body is dropped, since there is no content to carry it.

- [ ] **Step 4: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~RequestProxyTests"`
Expected: PASS. Then `dotnet test` — all green.

- [ ] **Step 5: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host/Api/ProxyContracts.cs src/Admin.Host/Api/RequestProxy.cs tests/Admin.Host.Tests/Api/RequestProxyTests.cs
git commit -m "feat(host): proxy one request to an API surface with identity and correlation id"
```

---

### Task 5: Endpoints, the fake platform's Keycloak, documents and gateway, and registration

**Files:**
- Create: `src/Admin.Host/Identity/IdentityEndpoints.cs`, `src/Admin.Host/Api/ApiEndpoints.cs`
- Create: `src/Admin.Host/Fakes/FakeKeycloak.cs`, `src/Admin.Host/Fakes/FakeOpenApi.cs`, `src/Admin.Host/Fakes/FakeGateway.cs`
- Modify: `src/Admin.Host/Fakes/FakePlatformHandler.cs` (dispatch by origin; constructor takes `AdminOptions`)
- Modify: `src/Admin.Host/Program.cs` (the `platform` HttpClient, three singletons, `MapIdentity()`, `MapApi()`; `PlatformProbe`'s fake handler gets options)
- Create: `tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs`, `tests/Admin.Host.Tests/Api/ApiEndpointTests.cs`

**Interfaces:**
- Consumes: everything Tasks 1–4 produce; `AdminHostFactory` (phase 1).
- Produces (HTTP, camelCase JSON, enums as strings):
  - `GET /api/identity/users` → 200 `[{ "username": "demo" }, { "username": "browser" }]`
  - `POST /api/identity/token` body `{ username, password }` → 200 `{ username, accessToken, expiresAt, claims }`; Keycloak's own status, content type and body when it refuses; 400 problem `Anonymous has no token` / `Unknown realm user`; 502 problem `Keycloak did not answer`
  - `GET /api/catalog/operations`, `POST /api/catalog/reload` → 200 `ApiCatalogView`
  - `POST /api/proxy` body `ProxyRequest` → 200 `ProxyResult` (with `outcome`); 400 problem `Request not sent` with the validation detail
  - Fake platform (for Tasks 7–8): users `demo`/`demo` (permissions `catalog:write`, `orders:write`, `orders:cancel`) and `browser`/`browser` (none); gateway answers `GET /api/v1/catalog/products` 200 with two products (`Walnut desk`, `Oak shelf`), `POST /api/v1/catalog/products` 401/403/200 `"0199a1b2-0000-7000-8000-000000000001"`, `POST /api/v1/orders` 401/403/200 `"0199a1b2-0000-7000-8000-000000000002"`, `POST /api/v1/orders/{guid}/cancel` 401/403/204, `POST /bff/v1/checkout/quote` 401/200, `/api/v1/inventory/*` 502, anything else 404; every gateway answer echoes `X-Correlation-Id`.

- [ ] **Step 1: Write the failing endpoint tests**

`tests/Admin.Host.Tests/Identity/IdentityEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Identity;

public sealed class IdentityEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Users_lists_usernames_and_never_passwords()
    {
        string body = await client.GetStringAsync("/api/identity/users", Token);

        body.ShouldBe("""[{"username":"demo"},{"username":"browser"}]""");
    }

    [Fact]
    public async Task A_named_user_gets_a_token_with_its_permission_claims()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/identity/token", new { username = "demo" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement token = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        token.GetProperty("username").GetString().ShouldBe("demo");
        token.GetProperty("accessToken").GetString()!.Split('.').Length.ShouldBe(3);
        token.GetProperty("claims").GetProperty("permission").EnumerateArray().Select(p => p.GetString()).ShouldBe(["catalog:write", "orders:write", "orders:cancel"]);
    }

    [Fact]
    public async Task A_wrong_password_returns_keycloaks_401_body_as_is()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/identity/token", new { username = "demo", password = "wrong" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(Token)).ShouldBe("""{"error":"invalid_grant","error_description":"Invalid user credentials"}""");
    }

    [Fact]
    public async Task Anonymous_and_unknown_users_are_problems()
    {
        HttpResponseMessage anonymous = await client.PostAsJsonAsync("/api/identity/token", new { username = (string?)null }, Token);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Anonymous has no token");

        HttpResponseMessage unknown = await client.PostAsJsonAsync("/api/identity/token", new { username = "nobody" }, Token);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().ShouldBe("Unknown realm user");
    }
}
```

`tests/Admin.Host.Tests/Api/ApiEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Admin.Host.Tests.TestSupport;
using Shouldly;

namespace Admin.Host.Tests.Api;

public sealed class ApiEndpointTests(AdminHostFactory factory) : IClassFixture<AdminHostFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly HttpClient client = factory.CreateClient();

    private async Task<JsonElement> ProxyAsync(object request)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/proxy", request, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    [Fact]
    public async Task Operations_list_the_fake_documents_rebased_onto_the_gateway_and_the_curated_calls()
    {
        JsonElement view = await client.GetFromJsonAsync<JsonElement>("/api/catalog/operations", Token);

        view.GetProperty("sources").EnumerateArray().ShouldAllBe(s => s.GetProperty("available").GetBoolean());
        view.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ShouldBe(
        [
            "catalog:PublishProduct", "catalog:GetProducts", "ordering:PlaceOrder", "ordering:CancelOrder",
            "bff:Quote", "health:gateway", "health:catalog", "health:ordering", "health:bff",
        ]);
        JsonElement publish = view.GetProperty("operations")[0];
        publish.GetProperty("url").GetString().ShouldBe("http://localhost:5000/api/v1/catalog/products/");
        publish.GetProperty("edgePolicy").GetString().ShouldBe("authenticated");
        publish.GetProperty("hasCommandId").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reload_answers_the_same_catalog()
    {
        HttpResponseMessage response = await client.PostAsync("/api/catalog/reload", null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("operations").GetArrayLength().ShouldBe(9);
    }

    [Fact]
    public async Task An_anonymous_product_listing_comes_back_with_its_status_body_and_correlation_id()
    {
        JsonElement result = await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/catalog/products/", correlationId = "e2e-list-1" });

        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBe(200);
        result.GetProperty("body").GetString()!.ShouldContain("Walnut desk");
        result.GetProperty("correlationId").GetString().ShouldBe("e2e-list-1");
        result.GetProperty("headers").GetProperty("X-Correlation-Id")[0].GetString().ShouldBe("e2e-list-1");
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("browser", 403)]
    [InlineData("demo", 200)]
    public async Task Publishing_passes_the_platforms_status_through_for_each_identity(string? username, int status)
    {
        JsonElement result = await ProxyAsync(new
        {
            method = "POST",
            url = "http://localhost:5000/api/v1/catalog/products/",
            body = """{"commandId":"0199a1b2-0000-7000-8000-00000000abcd","name":"Walnut desk","amount":19.99,"currency":"EUR"}""",
            identity = new { username },
        });

        result.GetProperty("outcome").GetString().ShouldBe("responded");
        result.GetProperty("status").GetInt32().ShouldBe(status);
    }

    [Fact]
    public async Task A_wrong_password_is_a_token_rejection()
    {
        JsonElement result = await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/orders", identity = new { username = "demo", password = "nope" } });

        result.GetProperty("outcome").GetString().ShouldBe("tokenRejected");
        result.GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Fact]
    public async Task A_url_off_the_surfaces_is_a_problem_and_not_sent()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/proxy", new { method = "GET", url = "http://localhost:8080/admin" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        problem.GetProperty("title").GetString().ShouldBe("Request not sent");
        problem.GetProperty("detail").GetString()!.ShouldContain("is not one of the configured API surfaces");
    }

    [Fact]
    public async Task Cancel_and_quote_and_inventory_answer_as_the_gateway_would()
    {
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/api/v1/orders/0199a1b2-0000-7000-8000-000000000002/cancel", body = """{"reason":"customer_request"}""", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(204);
        (await ProxyAsync(new { method = "POST", url = "http://localhost:5000/bff/v1/checkout/quote", body = "{}", identity = new { username = "browser" } }))
            .GetProperty("body").GetString()!.ShouldContain("\"total\"");
        (await ProxyAsync(new { method = "GET", url = "http://localhost:5000/api/v1/inventory/items", identity = new { username = "demo" } }))
            .GetProperty("status").GetInt32().ShouldBe(502);
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test --filter "FullyQualifiedName~EndpointTests&(FullyQualifiedName~Identity|FullyQualifiedName~Api)"`
Expected: FAIL — the routes answer 404 (the `/api/{**catchAll}` fallback).

- [ ] **Step 3: The fakes**

`src/Admin.Host/Fakes/FakeKeycloak.cs`:

```csharp
using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Admin.Host.Fakes;

/// <summary>
/// The realm's token endpoint in FakePlatform mode. Users and grants mirror blueprint-backend
/// <c>deploy/compose/keycloak/realm-export.json</c> (users → clientRoles.commerce-api; the
/// commerce-api scope's mapper puts them in the multivalued <c>permission</c> claim). The token is
/// unsigned: nothing in FakePlatform mode validates it, and FakeGateway reads its claims.
/// </summary>
internal static class FakeKeycloak
{
    private static readonly Dictionary<string, (string Password, string[] Permissions)> Users = new(StringComparer.Ordinal)
    {
        ["demo"] = ("demo", ["catalog:write", "orders:write", "orders:cancel"]),
        ["browser"] = ("browser", []),
    };

    public static async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> fields = form
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "", StringComparer.Ordinal);

        string username = fields.GetValueOrDefault("username") ?? "";

        if (!Users.TryGetValue(username, out (string Password, string[] Permissions) user) || user.Password != fields.GetValueOrDefault("password"))
        {
            return FakeHttp.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_grant","error_description":"Invalid user credentials"}""");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonObject payload = new()
        {
            ["exp"] = now + 300,
            ["iat"] = now,
            ["iss"] = "http://localhost:8080/realms/commerce",
            ["aud"] = "commerce-api",
            ["azp"] = "web-app",
            ["preferred_username"] = username,
            ["permission"] = new JsonArray([.. user.Permissions.Select(p => (JsonNode?)JsonValue.Create(p))]),
        };
        string token = $"{Segment("""{"alg":"none","typ":"JWT"}""")}.{Segment(payload.ToJsonString())}.fake";

        return FakeHttp.Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = token, ["expires_in"] = 300, ["token_type"] = "Bearer" }.ToJsonString());
    }

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
```

`src/Admin.Host/Fakes/FakeOpenApi.cs`:

```csharp
using System.Net;
using System.Reflection;

namespace Admin.Host.Fakes;

/// <summary>Catalog's and Ordering's <c>/openapi/v1.json</c>, which need a bearer token as the real ones do.</summary>
internal static class FakeOpenApi
{
    public static HttpResponseMessage Document(HttpRequestMessage request, string service)
    {
        if (request.Headers.Authorization?.Scheme != "Bearer")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"openapi-{service}.json")!;
        using StreamReader reader = new(stream);

        return FakeHttp.Json(HttpStatusCode.OK, reader.ReadToEnd());
    }
}
```

`src/Admin.Host/Fakes/FakeGateway.cs`:

```csharp
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Admin.Host.Identity;

namespace Admin.Host.Fakes;

/// <summary>
/// The gateway and the services behind it in FakePlatform mode, for the calls run-locally.md makes.
/// Authorization follows the real layering: the gateway's route policy (401 with no token), then
/// the service's permission (403 without it). Bodies are shaped like the platform's DTOs.
/// </summary>
internal static partial class FakeGateway
{
    public const string PublishedProductId = "0199a1b2-0000-7000-8000-000000000001";
    public const string PlacedOrderId = "0199a1b2-0000-7000-8000-000000000002";

    private const string Products = """
        {"items":[{"productId":"0199a1b2-0000-7000-8000-00000000000a","name":"Walnut desk","thumbnailUrl":null,"amount":19.99,"currency":"EUR","publishedAt":"2026-09-15T08:00:00+00:00"},{"productId":"0199a1b2-0000-7000-8000-00000000000b","name":"Oak shelf","thumbnailUrl":null,"amount":49.5,"currency":"EUR","publishedAt":"2026-09-15T08:01:00+00:00"}],"nextCursor":null}
        """;

    private const string Quote = """
        {"currency":"EUR","lines":[{"productId":"0199a1b2-0000-7000-8000-00000000000a","name":"Walnut desk","amount":19.99,"quantity":1,"lineTotal":19.99}],"total":19.99,"unpriced":[]}
        """;

    [GeneratedRegex("^/api/v1/orders/[0-9a-fA-F-]{36}/cancel$")]
    private static partial Regex CancelPath();

    public static HttpResponseMessage Send(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        string[]? permissions = Permissions(request);

        HttpResponseMessage response = (request.Method.Method, path) switch
        {
            ("GET", "/api/v1/catalog/products") => FakeHttp.Json(HttpStatusCode.OK, Products),
            ("POST", "/api/v1/catalog/products") => Authorize(permissions, "catalog:write", () => FakeHttp.Json(HttpStatusCode.OK, $"\"{PublishedProductId}\"")),
            ("POST", "/api/v1/orders") => Authorize(permissions, "orders:write", () => FakeHttp.Json(HttpStatusCode.OK, $"\"{PlacedOrderId}\"")),
            ("POST", var p) when CancelPath().IsMatch(p) => Authorize(permissions, "orders:cancel", () => new HttpResponseMessage(HttpStatusCode.NoContent)),
            ("POST", "/bff/v1/checkout/quote") => Authorize(permissions, null, () => FakeHttp.Json(HttpStatusCode.OK, Quote)),
            (_, var p) when p.StartsWith("/api/v1/inventory", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => FakeHttp.Problem(HttpStatusCode.NotFound, "Not Found"),
        };

        if (request.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? correlation))
        {
            response.Headers.TryAddWithoutValidation("X-Correlation-Id", correlation);
        }

        return response;
    }

    private static HttpResponseMessage Authorize(string[]? permissions, string? required, Func<HttpResponseMessage> allowed) =>
        permissions is null ? FakeHttp.Problem(HttpStatusCode.Unauthorized, "Unauthorized")
        : required is not null && !permissions.Contains(required) ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Forbidden")
        : allowed();

    /// <summary>The token's <c>permission</c> claim; null when there is no readable bearer token.</summary>
    private static string[]? Permissions(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: string token })
        {
            return null;
        }

        try
        {
            JsonElement claims = JwtPayload.Decode(token);

            return claims.TryGetProperty("permission", out JsonElement granted) ? [.. granted.EnumerateArray().Select(p => p.GetString()!)] : [];
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
```

Replace `src/Admin.Host/Fakes/FakePlatformHandler.cs`:

```csharp
using System.Net;
using System.Net.Mime;
using System.Text;
using Admin.Host.Config;

namespace Admin.Host.Fakes;

/// <summary>
/// Every outbound HTTP call in FakePlatform mode. Readiness and Grafana's health answer 200 on any
/// host; the realm token endpoint, the two OpenAPI documents and the gateway are recordings
/// (FakeKeycloak, FakeOpenApi, FakeGateway). Hosts are told apart by the configured URLs.
/// </summary>
public sealed class FakePlatformHandler(AdminOptions options) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string path = uri.AbsolutePath;

        HttpResponseMessage response =
            path == "/health/ready" ? FakeHttp.Json(HttpStatusCode.OK, """{"status":"Healthy"}""")
            : path == "/api/health" ? FakeHttp.Json(HttpStatusCode.OK, """{"database":"ok","version":"fake"}""")
            : Is(uri, options.KeycloakUrl) && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) ? await FakeKeycloak.TokenAsync(request, cancellationToken)
            : path == "/openapi/v1.json" && Is(uri, options.CatalogUrl) ? FakeOpenApi.Document(request, "catalog")
            : path == "/openapi/v1.json" && Is(uri, options.OrderingUrl) ? FakeOpenApi.Document(request, "ordering")
            : Is(uri, options.GatewayUrl) ? FakeGateway.Send(request)
            : FakeHttp.Json(HttpStatusCode.OK, """{"fake":true}""");

        response.RequestMessage = request;

        return response;
    }

    private static bool Is(Uri uri, string configured) =>
        Uri.TryCreate(configured, UriKind.Absolute, out Uri? baseUri)
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}

internal static class FakeHttp
{
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    /// <summary>The shape <c>UseStatusCodePages</c> gives a bare 401/403/404 on the platform's hosts.</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string title) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"https://tools.ietf.org/html/rfc9110","title":"{{title}}","status":{{(int)status}}}""",
                Encoding.UTF8,
                MediaTypeNames.Application.ProblemJson),
        };
}
```

- [ ] **Step 4: The endpoints**

`src/Admin.Host/Identity/IdentityEndpoints.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Admin.Host.Config;
using Microsoft.Extensions.Options;

namespace Admin.Host.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentity(this IEndpointRouteBuilder app)
    {
        // Usernames only: the SPA sends a username back and the host supplies the password.
        app.MapGet("/api/identity/users", (IOptions<AdminOptions> options) =>
            TypedResults.Ok(RealmUsers.Of(options.Value).Select(u => new RealmUserView(u.Username)).ToArray()));

        app.MapPost("/api/identity/token", async Task<IResult> (IdentityRequest identity, TokenService tokens, CancellationToken cancellationToken) =>
            await tokens.ForAsync(identity, cancellationToken) switch
            {
                null => TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Anonymous has no token",
                    detail: "Pick a realm user or enter a username and password."),
                TokenIssued issued => TypedResults.Ok(new TokenView(issued.Username, issued.AccessToken, issued.ExpiresAt, issued.Claims)),
                // Keycloak's refusal is returned as-is (spec §5.6, §9).
                TokenRejected rejected => TypedResults.Text(rejected.Body, rejected.ContentType, statusCode: rejected.Status),
                UnknownUser unknown => TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Unknown realm user",
                    detail: $"'{unknown.Username}' is not in Admin:Users; send a password to use it."),
                KeycloakUnreachable unreachable => TypedResults.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Keycloak did not answer",
                    detail: unreachable.Error),
                _ => throw new UnreachableException(),
            });

        return app;
    }
}

public sealed record RealmUserView(string Username);

public sealed record TokenView(string Username, string AccessToken, DateTimeOffset ExpiresAt, JsonElement Claims);
```

`src/Admin.Host/Api/ApiEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;

namespace Admin.Host.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/operations", async (ApiCatalog catalog, CancellationToken cancellationToken) =>
            TypedResults.Ok(await catalog.GetAsync(cancellationToken)));

        app.MapPost("/api/catalog/reload", async (ApiCatalog catalog, CancellationToken cancellationToken) =>
            TypedResults.Ok(await catalog.ReloadAsync(cancellationToken)));

        app.MapPost("/api/proxy", async Task<Results<Ok<ProxyResult>, ProblemHttpResult>> (ProxyRequest request, RequestProxy proxy, CancellationToken cancellationToken) =>
            proxy.Validate(request) is string problem
                ? TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Request not sent", detail: problem)
                : TypedResults.Ok(await proxy.SendAsync(request, cancellationToken)));

        return app;
    }
}
```

- [ ] **Step 5: Register**

In `src/Admin.Host/Program.cs`:

1. Add `using Admin.Host.Api;` and `using Admin.Host.Identity;`.
2. Change the `PlatformProbe` registration's fake branch from `new FakePlatformHandler()` to `new FakePlatformHandler(sp.GetRequiredService<IOptions<AdminOptions>>().Value)`.
3. After the `PlatformProbe` registration add:

```csharp
// One client for Keycloak, the OpenAPI documents and the proxy. No redirects and no cookies: a
// 302 or a Set-Cookie from the platform is part of the answer the proxy shows, not something to
// act on (spec §5.7). The token cache and the catalog cache live in singletons, so the client is
// created once for them rather than injected as a typed client.
builder.Services.AddHttpClient("platform").ConfigurePrimaryHttpMessageHandler(sp =>
    sp.GetRequiredService<IOptions<AdminOptions>>().Value is { FakePlatform: true } fake
        ? new FakePlatformHandler(fake)
        : new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

builder.Services.AddSingleton(sp => new TokenService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"),
    sp.GetRequiredService<IOptions<AdminOptions>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new ApiCatalog(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"),
    sp.GetRequiredService<TokenService>(),
    sp.GetRequiredService<IOptions<AdminOptions>>()));
builder.Services.AddSingleton(sp => new RequestProxy(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"),
    sp.GetRequiredService<TokenService>(),
    sp.GetRequiredService<IOptions<AdminOptions>>(),
    sp.GetRequiredService<TimeProvider>()));
```

4. After `app.MapFrontend();` add `app.MapIdentity();` and `app.MapApi();` (before the `/api/{**catchAll}` line).

- [ ] **Step 6: Run the tests**

Run: `dotnet format whitespace BlueprintAdmin.slnx && dotnet test`
Expected: all green, including the phase 1 `FakePlatformTests` (readiness and `/api/health` still answer 200 on every host, Keycloak's `/realms/commerce` still falls through to 200).

- [ ] **Step 7: Try it by hand**

```bash
dotnet run --project src/Admin.Host -- --Admin:FakePlatform=true
# in another shell
curl -s http://127.0.0.1:5300/api/catalog/operations | head -c 400
curl -s -X POST http://127.0.0.1:5300/api/proxy -H "Content-Type: application/json" -d "{\"method\":\"POST\",\"url\":\"http://localhost:5000/api/v1/catalog/products\",\"body\":\"{}\",\"identity\":{\"username\":\"browser\"}}"
```

Expected: nine operations; the proxy answers `{"outcome":"responded","status":403,...}`. Stop the host.

- [ ] **Step 8: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
dotnet format BlueprintAdmin.slnx --verify-no-changes
git add src/Admin.Host tests/Admin.Host.Tests
git commit -m "feat(host): identity, catalog and proxy endpoints with a fake realm and gateway"
```

---

### Task 6: SPA — types, host client calls and identity state

**Files:**
- Modify: `src/Admin.Web/src/app/core/host/host-types.ts`, `src/Admin.Web/src/app/core/host/host-client.ts`, `src/Admin.Web/src/app/core/host/host-client.spec.ts`
- Create: `src/Admin.Web/src/app/core/identity/identity-state.ts`, `src/Admin.Web/src/app/core/identity/identity-state.spec.ts`

**Interfaces:**
- Consumes: the HTTP contract from Task 5.
- Produces:
  - Types `RealmUserView`, `Identity`, `TokenView`, `ApiParameter`, `ApiOperation`, `ApiSource`, `ApiCatalogView`, `ProxyRequest`, `ProxyResult` (union on `outcome`)
  - `HostClient.identityUsers()`, `.token(identity: Identity)`, `.operations()`, `.reloadOperations()`, `.proxy(request: ProxyRequest)`
  - `IdentityState` (root service): `users: Signal<RealmUserView[]>`, `choice: Signal<IdentityChoice>`, `select(choice: IdentityChoice): void`, `request: Signal<Identity | null>`, `label: Signal<string>`, `load(): void`; `type IdentityChoice = { kind: 'anonymous' } | { kind: 'user'; username: string } | { kind: 'custom'; username: string; password: string }`

- [ ] **Step 1: Types**

Append to `src/Admin.Web/src/app/core/host/host-types.ts`:

```ts
/** A realm user the host can mint a token for; the host keeps the password (spec §5.6). */
export interface RealmUserView {
  username: string;
}

/** No username is anonymous; a username alone is a configured realm user; both is a custom identity. */
export interface Identity {
  username: string | null;
  password: string | null;
}

export interface TokenView {
  username: string;
  accessToken: string;
  expiresAt: string;
  claims: Record<string, unknown>;
}

export interface ApiParameter {
  name: string;
  required: boolean;
  type: string | null;
}

/** One call on the API screen (spec §5.7). `url` is absolute and may hold `{name}` path placeholders. */
export interface ApiOperation {
  id: string;
  source: string;
  name: string;
  method: string;
  url: string;
  pathParameters: ApiParameter[];
  queryParameters: ApiParameter[];
  exampleBody: string | null;
  hasCommandId: boolean;
  edgePolicy: string;
  available: boolean;
}

export interface ApiSource {
  name: string;
  documentUrl: string;
  available: boolean;
  error: string | null;
}

export interface ApiCatalogView {
  sources: ApiSource[];
  operations: ApiOperation[];
}

export interface ProxyRequest {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: string | null;
  identity: Identity | null;
  correlationId: string | null;
}

/** `responded` is the platform's answer untouched; the other two mean the request got no answer from it (spec §9). */
export type ProxyResult =
  | {
      outcome: 'responded';
      status: number;
      headers: Record<string, string[]>;
      body: string;
      bodyTruncated: boolean;
      elapsedMs: number;
      correlationId: string;
    }
  | { outcome: 'unreached'; error: string; elapsedMs: number; correlationId: string }
  | { outcome: 'tokenRejected'; status: number; body: string; correlationId: string };
```

- [ ] **Step 2: Failing client tests**

Append inside the `describe('HostClient', ...)` block of `host-client.spec.ts` (no new imports):

```ts
  it('lists realm users', () => {
    client.identityUsers().subscribe();

    const req = http.expectOne('/api/identity/users');
    expect(req.request.method).toBe('GET');
    req.flush([{ username: 'demo' }]);
  });

  it('mints a token for an identity', () => {
    client.token({ username: 'demo', password: null }).subscribe();

    const req = http.expectOne('/api/identity/token');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ username: 'demo', password: null });
    req.flush({ username: 'demo', accessToken: 'a.b.c', expiresAt: '', claims: {} });
  });

  it('reads and reloads the operation catalog', () => {
    client.operations().subscribe();
    const read = http.expectOne('/api/catalog/operations');
    expect(read.request.method).toBe('GET');
    read.flush({ sources: [], operations: [] });

    client.reloadOperations().subscribe();
    const reload = http.expectOne('/api/catalog/reload');
    expect(reload.request.method).toBe('POST');
    reload.flush({ sources: [], operations: [] });
  });

  it('proxies a request', () => {
    const request = {
      method: 'GET',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      headers: {},
      body: null,
      identity: null,
      correlationId: null,
    };
    client.proxy(request).subscribe();

    const req = http.expectOne('/api/proxy');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush({ outcome: 'unreached', error: 'refused', elapsedMs: 1, correlationId: 'c' });
  });
```

- [ ] **Step 3: Failing identity-state tests**

`src/Admin.Web/src/app/core/identity/identity-state.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../host/host-client';
import { IdentityState } from './identity-state';

describe('IdentityState', () => {
  function setup(users = of([{ username: 'demo' }, { username: 'browser' }])): IdentityState {
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: { identityUsers: () => users } }] });
    return TestBed.inject(IdentityState);
  }

  it('starts anonymous and sends no identity', () => {
    const state = setup();

    expect(state.choice()).toEqual({ kind: 'anonymous' });
    expect(state.request()).toBeNull();
    expect(state.label()).toBe('anonymous');
  });

  it('loads the realm users and selects demo when it exists', () => {
    const state = setup();
    state.load();

    expect(state.users().map((u) => u.username)).toEqual(['demo', 'browser']);
    expect(state.choice()).toEqual({ kind: 'user', username: 'demo' });
    expect(state.request()).toEqual({ username: 'demo', password: null });
  });

  it('keeps an explicit choice when users load later', () => {
    const state = setup();
    state.select({ kind: 'user', username: 'browser' });
    state.load();

    expect(state.choice()).toEqual({ kind: 'user', username: 'browser' });
  });

  it('sends a custom identity with its password and labels it without the password', () => {
    const state = setup();
    state.select({ kind: 'custom', username: 'alice', password: 's3cret' });

    expect(state.request()).toEqual({ username: 'alice', password: 's3cret' });
    expect(state.label()).toBe('alice (custom)');
  });

  it('stays usable when the users request fails', () => {
    const state = setup(throwError(() => new Error('offline')));
    state.load();

    expect(state.users()).toEqual([]);
    expect(state.choice()).toEqual({ kind: 'anonymous' });
  });
});
```

- [ ] **Step 4: Run to confirm they fail**

Run (in `src/Admin.Web`): `npm test`
Expected: FAIL — `identityUsers` is not a function / `./identity-state` not found.

- [ ] **Step 5: Implement**

In `host-client.ts`, extend the import to `import { ApiCatalogView, ConfigView, Identity, JobSummary, JobView, ProxyRequest, ProxyResult, RealmUserView, StackView, TokenView } from './host-types';` and add before `job(...)`:

```ts
  identityUsers(): Observable<RealmUserView[]> {
    return this.http.get<RealmUserView[]>('/api/identity/users');
  }

  token(identity: Identity): Observable<TokenView> {
    return this.http.post<TokenView>('/api/identity/token', identity);
  }

  operations(): Observable<ApiCatalogView> {
    return this.http.get<ApiCatalogView>('/api/catalog/operations');
  }

  reloadOperations(): Observable<ApiCatalogView> {
    return this.http.post<ApiCatalogView>('/api/catalog/reload', null);
  }

  proxy(request: ProxyRequest): Observable<ProxyResult> {
    return this.http.post<ProxyResult>('/api/proxy', request);
  }
```

`src/Admin.Web/src/app/core/identity/identity-state.ts`:

```ts
import { Injectable, computed, inject, signal } from '@angular/core';
import { HostClient } from '../host/host-client';
import { Identity, RealmUserView } from '../host/host-types';

export type IdentityChoice =
  | { kind: 'anonymous' }
  | { kind: 'user'; username: string }
  | { kind: 'custom'; username: string; password: string };

/** Who the API screen sends as. Root-scoped so the choice survives leaving and returning to the screen. */
@Injectable({ providedIn: 'root' })
export class IdentityState {
  private readonly host = inject(HostClient);
  private chosen = false;
  private readonly selected = signal<IdentityChoice>({ kind: 'anonymous' });

  readonly users = signal<RealmUserView[]>([]);
  readonly choice = this.selected.asReadonly();

  /** An explicit choice, which a users load that answers later does not overwrite. */
  select(choice: IdentityChoice): void {
    this.chosen = true;
    this.selected.set(choice);
  }

  readonly request = computed<Identity | null>(() => {
    const c = this.selected();
    switch (c.kind) {
      case 'anonymous':
        return null;
      case 'user':
        return { username: c.username, password: null };
      case 'custom':
        return { username: c.username, password: c.password };
    }
  });

  readonly label = computed(() => {
    const c = this.selected();
    return c.kind === 'anonymous' ? 'anonymous' : c.kind === 'user' ? c.username : `${c.username} (custom)`;
  });

  load(): void {
    this.host.identityUsers().subscribe({
      next: (users) => {
        this.users.set(users);
        const demo = users.find((u) => u.username === 'demo');
        if (!this.chosen && demo) {
          this.selected.set({ kind: 'user', username: demo.username });
        }
      },
      error: () => this.users.set([]),
    });
  }
}
```

- [ ] **Step 6: Run the checks**

Run (in `src/Admin.Web`): `npm run lint && npm test && npm run build`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
git add src/Admin.Web/src/app/core
git commit -m "feat(web): host client calls for identity, catalog and proxy, and identity state"
```

---

### Task 7: SPA — the API screen

**Files:**
- Create: `src/Admin.Web/src/app/features/api/request-builder.ts`, `request-builder.spec.ts`
- Create: `src/Admin.Web/src/app/features/api/api-page.ts`, `api-page.html`, `api-page.css`, `api-page.spec.ts`
- Modify: `src/Admin.Web/src/app/app.routes.ts` (route `api`), `src/Admin.Web/src/app/app.html` (nav link `API` after `Logs`)

**Interfaces:**
- Consumes: `HostClient.operations/reloadOperations/proxy/token`, `IdentityState` (`users`, `choice`, `select`, `request`, `label`, `load`), the types from Task 6.
- Produces: `ApiPage` at `/api`; `UUID` injection token (`() => string`); pure helpers `buildUrl`, `parseHeaders`, `withFreshCommandId`, `pretty`. DOM hooks the Playwright smoke (Task 8) uses: operation buttons have class `op` and their text is the operation name; the identity `<select>` has `aria-label="Identity"`; the Send button's name is `Send`; the response section is `.response` with `.status`, `.correlation` and `pre.body`; the unreached shape is `.response.unreached`; history rows are `.history li`.

- [ ] **Step 1: Failing helper tests**

`src/Admin.Web/src/app/features/api/request-builder.spec.ts`:

```ts
import { buildUrl, parseHeaders, pretty, withFreshCommandId } from './request-builder';

describe('request-builder', () => {
  it('fills path placeholders encoded and appends only non-empty query values', () => {
    expect(
      buildUrl('http://localhost:5000/api/v1/orders/{id}/cancel', { id: 'a b' }, {}),
    ).toBe('http://localhost:5000/api/v1/orders/a%20b/cancel');
    expect(
      buildUrl('http://localhost:5000/api/v1/catalog/products/', {}, { cursor: '', limit: '5' }),
    ).toBe('http://localhost:5000/api/v1/catalog/products/?limit=5');
  });

  it('leaves an unfilled placeholder in place so the platform answers for it', () => {
    expect(buildUrl('http://h/{id}', {}, {})).toBe('http://h/{id}');
  });

  it('parses one Name: value header per line and reports the lines that are not headers', () => {
    expect(parseHeaders('Accept: application/json\n\n  X-Thing:  a:b  \nnonsense')).toEqual({
      headers: { Accept: 'application/json', 'X-Thing': 'a:b' },
      invalid: ['nonsense'],
    });
  });

  it('replaces commandId in a JSON object body and leaves anything else untouched', () => {
    expect(withFreshCommandId('{"commandId":"0","name":"x"}', 'new-id')).toBe('{\n  "commandId": "new-id",\n  "name": "x"\n}');
    expect(withFreshCommandId('{"name":"x"}', 'new-id')).toBe('{"name":"x"}');
    expect(withFreshCommandId('not json', 'new-id')).toBe('not json');
  });

  it('pretty-prints JSON and returns other text as-is', () => {
    expect(pretty('{"a":1}')).toBe('{\n  "a": 1\n}');
    expect(pretty('<html>')).toBe('<html>');
    expect(pretty('')).toBe('');
  });
});
```

- [ ] **Step 2: Failing page tests**

`src/Admin.Web/src/app/features/api/api-page.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyResult } from '../../core/host/host-types';
import { ApiPage, UUID } from './api-page';

function op(partial: Partial<ApiOperation>): ApiOperation {
  return {
    id: 'catalog:GetProducts', source: 'catalog', name: 'GetProducts', method: 'GET',
    url: 'http://localhost:5000/api/v1/catalog/products/', pathParameters: [], queryParameters: [],
    exampleBody: null, hasCommandId: false, edgePolicy: 'anonymous', available: true, ...partial,
  };
}

const catalog: ApiCatalogView = {
  sources: [
    { name: 'catalog', documentUrl: 'http://localhost:5102/openapi/v1.json', available: true, error: null },
    { name: 'ordering', documentUrl: 'http://localhost:5101/openapi/v1.json', available: false, error: 'answered 503.' },
  ],
  operations: [
    op({}),
    op({
      id: 'catalog:PublishProduct', name: 'PublishProduct', method: 'POST', edgePolicy: 'authenticated',
      exampleBody: '{"commandId":"00000000-0000-0000-0000-000000000000","name":"Walnut desk"}', hasCommandId: true,
    }),
    op({
      id: 'ordering:CancelOrder', source: 'ordering', name: 'CancelOrder', method: 'POST', available: false,
      url: 'http://localhost:5000/api/v1/orders/{id}/cancel', pathParameters: [{ name: 'id', required: true, type: 'string' }],
    }),
  ],
};

const responded: ProxyResult = {
  outcome: 'responded', status: 200, headers: { 'Content-Type': ['application/json'] },
  body: '{"items":[]}', bodyTruncated: false, elapsedMs: 12, correlationId: 'corr-1',
};

describe('ApiPage', () => {
  let host: {
    operations: ReturnType<typeof vi.fn>;
    reloadOperations: ReturnType<typeof vi.fn>;
    proxy: ReturnType<typeof vi.fn>;
    token: ReturnType<typeof vi.fn>;
    identityUsers: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    host = {
      operations: vi.fn(() => of(catalog)),
      reloadOperations: vi.fn(() => of(catalog)),
      proxy: vi.fn(() => of(responded)),
      token: vi.fn(() => of({ username: 'demo', accessToken: 'a.b.c', expiresAt: '2026-09-15T08:05:00Z', claims: { permission: ['catalog:write'] } })),
      identityUsers: vi.fn(() => of([{ username: 'demo' }, { username: 'browser' }])),
    };
    TestBed.configureTestingModule({
      imports: [ApiPage],
      providers: [{ provide: HostClient, useValue: host }, { provide: UUID, useValue: () => 'fresh-uuid' }],
    });
  });

  function render() {
    const fixture = TestBed.createComponent(ApiPage);
    fixture.detectChanges();
    return fixture;
  }

  function click(fixture: ReturnType<typeof render>, name: string): void {
    const button = (Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).find((b) => b.textContent?.trim().startsWith(name));
    button!.click();
    fixture.detectChanges();
  }

  it('lists operations by source and shows an unavailable source with its error', () => {
    const fixture = render();
    const text = fixture.nativeElement.textContent as string;

    expect(Array.from(fixture.nativeElement.querySelectorAll('button.op')).map((b) => (b as HTMLElement).textContent?.trim()))
      .toEqual([expect.stringContaining('GetProducts'), expect.stringContaining('PublishProduct'), expect.stringContaining('CancelOrder')]);
    expect(text).toContain('answered 503.');
    expect(fixture.nativeElement.querySelector('button.op.unavailable')).not.toBeNull();
  });

  it('selecting an operation fills the method, url and example body', () => {
    const fixture = render();
    click(fixture, 'POST PublishProduct');

    const page = fixture.componentInstance;
    expect(page.method()).toBe('POST');
    expect(page.url()).toBe('http://localhost:5000/api/v1/catalog/products/');
    expect(page.body()).toContain('Walnut desk');
  });

  it('sends as the chosen identity with a fresh commandId and shows the response', () => {
    const fixture = render();
    click(fixture, 'POST PublishProduct');
    fixture.componentInstance.correlationId.set('my-corr');
    click(fixture, 'Send');

    expect(host.proxy).toHaveBeenCalledWith({
      method: 'POST',
      url: 'http://localhost:5000/api/v1/catalog/products/',
      headers: {},
      body: '{\n  "commandId": "fresh-uuid",\n  "name": "Walnut desk"\n}',
      identity: { username: 'demo', password: null },
      correlationId: 'my-corr',
    });
    const response = fixture.nativeElement.querySelector('.response') as HTMLElement;
    expect(response.querySelector('.status')?.textContent).toContain('200');
    expect(response.querySelector('.correlation')?.textContent).toContain('corr-1');
    expect(response.querySelector('pre.body')?.textContent).toContain('"items": []');
  });

  it('fills path parameters into the url', () => {
    const fixture = render();
    click(fixture, 'POST CancelOrder');
    fixture.componentInstance.setPathValue('id', 'abc');
    click(fixture, 'Send');

    expect(host.proxy.mock.calls[0][0].url).toBe('http://localhost:5000/api/v1/orders/abc/cancel');
  });

  it('renders an unreached result differently from a response', () => {
    host.proxy.mockReturnValue(of({ outcome: 'unreached', error: 'Connection refused', elapsedMs: 3, correlationId: 'c' }));
    const fixture = render();
    click(fixture, 'Send');

    const response = fixture.nativeElement.querySelector('.response.unreached') as HTMLElement;
    expect(response.textContent).toContain('Connection refused');
  });

  it('shows the problem detail when the host refuses to send', () => {
    host.proxy.mockReturnValue(throwError(() => ({ error: { title: 'Request not sent', detail: 'not one of the configured API surfaces' } })));
    const fixture = render();
    click(fixture, 'Send');

    expect(fixture.nativeElement.querySelector('.error')?.textContent).toContain('not one of the configured API surfaces');
  });

  it('keeps a history, newest first, and restores a past response when clicked', () => {
    host.proxy.mockReturnValueOnce(of(responded)).mockReturnValueOnce(of({ ...responded, status: 403, body: '', correlationId: 'corr-2' }));
    const fixture = render();
    click(fixture, 'Send');
    click(fixture, 'Send');

    const rows = Array.from(fixture.nativeElement.querySelectorAll('.history li button')) as HTMLButtonElement[];
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('403'), expect.stringContaining('200')]);

    rows[1].click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.response .correlation')?.textContent).toContain('corr-1');
  });

  it('shows the token claims for the chosen identity', () => {
    const fixture = render();
    click(fixture, 'Show token');

    expect(host.token).toHaveBeenCalledWith({ username: 'demo', password: null });
    expect(fixture.nativeElement.querySelector('.claims')?.textContent).toContain('catalog:write');
  });

  it('reloads the catalog', () => {
    const fixture = render();
    click(fixture, 'Reload');

    expect(host.reloadOperations).toHaveBeenCalled();
  });
});
```

Note: the "sends as the chosen identity" test relies on `IdentityState.load()` selecting `demo` (Task 6) when the page is created.

- [ ] **Step 3: Run to confirm they fail**

Run (in `src/Admin.Web`): `npm test`
Expected: FAIL — `./request-builder` and `./api-page` not found.

- [ ] **Step 4: Implement the helpers**

`src/Admin.Web/src/app/features/api/request-builder.ts`:

```ts
/** Fills `{name}` placeholders (URL-encoded) and appends the query values that are not empty. */
export function buildUrl(template: string, path: Record<string, string>, query: Record<string, string>): string {
  const filled = template.replace(/\{([^}]+)\}/g, (whole, name: string) =>
    path[name] ? encodeURIComponent(path[name]) : whole,
  );
  const search = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== ''));
  const qs = search.toString();
  return qs ? `${filled}${filled.includes('?') ? '&' : '?'}${qs}` : filled;
}

/** One `Name: value` per line; blank lines are skipped and any other line is reported. */
export function parseHeaders(text: string): { headers: Record<string, string>; invalid: string[] } {
  const headers: Record<string, string> = {};
  const invalid: string[] = [];
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line) continue;
    const colon = line.indexOf(':');
    if (colon <= 0) {
      invalid.push(line);
      continue;
    }
    headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return { headers, invalid };
}

/**
 * The platform keys idempotency on `commandId` (Common.Application IdempotencyBehavior), so a
 * resend with the same one is the same intent. Each Send is a new intent (spec §6).
 */
export function withFreshCommandId(body: string, uuid: string): string {
  try {
    const parsed: unknown = JSON.parse(body);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed) && 'commandId' in parsed) {
      return JSON.stringify({ ...parsed, commandId: uuid }, null, 2);
    }
  } catch {
    // Not JSON: sent as typed.
  }
  return body;
}

export function pretty(body: string): string {
  try {
    return body ? JSON.stringify(JSON.parse(body), null, 2) : body;
  } catch {
    return body;
  }
}
```

- [ ] **Step 5: Implement the page**

`src/Admin.Web/src/app/features/api/api-page.ts`:

```ts
import { Component, DestroyRef, InjectionToken, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { ApiCatalogView, ApiOperation, ProxyRequest, ProxyResult, TokenView } from '../../core/host/host-types';
import { IdentityState } from '../../core/identity/identity-state';
import { buildUrl, parseHeaders, pretty, withFreshCommandId } from './request-builder';

export const UUID = new InjectionToken<() => string>('UUID', { factory: () => () => crypto.randomUUID() });

export interface HistoryEntry {
  seq: number;
  at: Date;
  name: string;
  identity: string;
  request: ProxyRequest;
  result: ProxyResult;
}

const MAX_HISTORY = 50;
const CUSTOM = 'custom';

@Component({
  selector: 'app-api-page',
  imports: [FormsModule],
  templateUrl: './api-page.html',
  styleUrl: './api-page.css',
})
export class ApiPage {
  private readonly host = inject(HostClient);
  private readonly uuid = inject(UUID);
  private sending?: Subscription;
  private seq = 0;

  readonly identity = inject(IdentityState);
  readonly catalog = signal<ApiCatalogView | null>(null);
  readonly selectedId = signal<string | null>(null);
  readonly method = signal('GET');
  readonly url = signal('');
  readonly pathValues = signal<Record<string, string>>({});
  readonly queryValues = signal<Record<string, string>>({});
  readonly headersText = signal('');
  readonly body = signal('');
  readonly correlationId = signal('');
  readonly pending = signal(false);
  readonly result = signal<ProxyResult | null>(null);
  readonly history = signal<HistoryEntry[]>([]);
  readonly token = signal<TokenView | null>(null);
  readonly error = signal<string | null>(null);
  readonly customUsername = signal('');
  readonly customPassword = signal('');

  readonly methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];

  readonly operation = computed(() => this.catalog()?.operations.find((o) => o.id === this.selectedId()) ?? null);

  readonly groups = computed(() => {
    const view = this.catalog();
    if (!view) return [];
    const names = [...new Set([...view.sources.map((s) => s.name), ...view.operations.map((o) => o.source)])];
    return names.map((name) => ({
      name,
      source: view.sources.find((s) => s.name === name) ?? null,
      operations: view.operations.filter((o) => o.source === name),
    }));
  });

  /** The identity `<select>`'s value: `anonymous`, `user:<name>` or `custom`. */
  readonly identityValue = computed(() => {
    const c = this.identity.choice();
    return c.kind === 'user' ? `user:${c.username}` : c.kind;
  });

  readonly prettyBody = computed(() => {
    const r = this.result();
    return r && r.outcome !== 'unreached' ? pretty(r.body) : '';
  });

  readonly headerLines = computed(() => {
    const r = this.result();
    return r?.outcome === 'responded' ? Object.entries(r.headers).map(([name, values]) => `${name}: ${values.join(', ')}`) : [];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.sending?.unsubscribe());
    this.identity.load();
    this.host.operations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  reload(): void {
    this.host.reloadOperations().subscribe({
      next: (view) => this.catalog.set(view),
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  select(operation: ApiOperation): void {
    this.selectedId.set(operation.id);
    this.method.set(operation.method);
    this.url.set(operation.url);
    this.pathValues.set({});
    this.queryValues.set({});
    this.body.set(operation.exampleBody ?? '');
  }

  setPathValue(name: string, value: string): void {
    this.pathValues.update((all) => ({ ...all, [name]: value }));
  }

  setQueryValue(name: string, value: string): void {
    this.queryValues.update((all) => ({ ...all, [name]: value }));
  }

  chooseIdentity(value: string): void {
    this.token.set(null);
    if (value === 'anonymous') {
      this.identity.select({ kind: 'anonymous' });
    } else if (value === CUSTOM) {
      this.identity.select({ kind: 'custom', username: this.customUsername(), password: this.customPassword() });
    } else {
      this.identity.select({ kind: 'user', username: value.slice('user:'.length) });
    }
  }

  setCustom(username: string, password: string): void {
    this.customUsername.set(username);
    this.customPassword.set(password);
    this.identity.select({ kind: 'custom', username, password });
  }

  send(): void {
    const operation = this.operation();
    const { headers, invalid } = parseHeaders(this.headersText());
    if (invalid.length > 0) {
      this.error.set(`Not a "Name: value" header line: ${invalid[0]}`);
      return;
    }

    let body = this.body();
    if (operation?.hasCommandId && body.trim()) {
      body = withFreshCommandId(body, this.uuid());
      this.body.set(body);
    }

    const request: ProxyRequest = {
      method: this.method(),
      url: buildUrl(this.url(), this.pathValues(), this.queryValues()),
      headers,
      body: body.trim() ? body : null,
      identity: this.identity.request(),
      correlationId: this.correlationId().trim() || null,
    };
    const name = operation?.name ?? `${request.method} ${request.url}`;
    const identity = this.identity.label();

    this.error.set(null);
    this.pending.set(true);
    this.sending?.unsubscribe();
    this.sending = this.host.proxy(request).subscribe({
      next: (result) => {
        this.result.set(result);
        this.history.update((all) => [{ seq: ++this.seq, at: new Date(), name, identity, request, result }, ...all].slice(0, MAX_HISTORY));
        this.pending.set(false);
      },
      error: (e: unknown) => {
        this.error.set(this.describe(e));
        this.pending.set(false);
      },
    });
  }

  restore(entry: HistoryEntry): void {
    this.result.set(entry.result);
    this.method.set(entry.request.method);
    this.url.set(entry.request.url);
    this.pathValues.set({});
    this.queryValues.set({});
    this.body.set(entry.request.body ?? '');
    this.correlationId.set(entry.request.correlationId ?? '');
  }

  showToken(): void {
    const request = this.identity.request();
    this.token.set(null);
    if (!request) {
      this.error.set('Anonymous has no token.');
      return;
    }
    this.host.token(request).subscribe({
      next: (token) => {
        this.error.set(null);
        this.token.set(token);
      },
      error: (e: unknown) => this.error.set(this.describe(e)),
    });
  }

  claims(token: TokenView): string {
    return JSON.stringify(token.claims, null, 2);
  }

  historyLabel(entry: HistoryEntry): string {
    const r = entry.result;
    const outcome = r.outcome === 'responded' ? String(r.status) : r.outcome === 'tokenRejected' ? `token ${r.status}` : 'unreached';
    return `${outcome} ${entry.name} as ${entry.identity}`;
  }

  /** A problem's `detail`/`title`, Keycloak's `error_description`, else the error's message. */
  private describe(e: unknown): string {
    const err = e as { error?: { detail?: string; title?: string; error_description?: string }; message?: string } | null;
    return err?.error?.detail ?? err?.error?.error_description ?? err?.error?.title ?? err?.message ?? 'The host did not answer.';
  }
}
```

`src/Admin.Web/src/app/features/api/api-page.html`:

```html
<div class="layout">
  <aside class="tree">
    <div class="tree-head">
      <strong>Operations</strong>
      <button (click)="reload()">Reload</button>
    </div>
    @for (g of groups(); track g.name) {
      <section>
        <h3>{{ g.name }}</h3>
        @if (g.source && !g.source.available) {
          <p class="source-error" [title]="g.source.documentUrl">unavailable: {{ g.source.error }}</p>
        }
        <ul>
          @for (o of g.operations; track o.id) {
            <li>
              <button class="op" [class.selected]="o.id === selectedId()" [class.unavailable]="!o.available" [title]="o.edgePolicy" (click)="select(o)">
                <span class="method">{{ o.method }}</span> {{ o.name }}
              </button>
            </li>
          }
        </ul>
      </section>
    }
  </aside>

  <section class="editor">
    <div class="identity">
      <label>
        Send as
        <select aria-label="Identity" [ngModel]="identityValue()" (ngModelChange)="chooseIdentity($event)">
          <option value="anonymous">anonymous</option>
          @for (u of identity.users(); track u.username) {
            <option [value]="'user:' + u.username">{{ u.username }}</option>
          }
          <option value="custom">custom…</option>
        </select>
      </label>
      @if (identity.choice().kind === 'custom') {
        <input aria-label="Custom username" placeholder="username" [ngModel]="customUsername()" (ngModelChange)="setCustom($event, customPassword())" />
        <input aria-label="Custom password" placeholder="password" type="password" [ngModel]="customPassword()" (ngModelChange)="setCustom(customUsername(), $event)" />
      }
      <button (click)="showToken()">Show token</button>
    </div>

    @if (token(); as t) {
      <pre class="claims">{{ claims(t) }}</pre>
    }

    @if (operation(); as o) {
      <p class="policy">edge policy: {{ o.edgePolicy }}@if (!o.available) { — its service did not answer the last catalog load}</p>
    }

    <div class="line">
      <select aria-label="Method" [ngModel]="method()" (ngModelChange)="method.set($event)">
        @for (m of methods; track m) {
          <option [value]="m">{{ m }}</option>
        }
      </select>
      <input class="url" aria-label="URL" [ngModel]="url()" (ngModelChange)="url.set($event)" />
    </div>

    @if (operation(); as o) {
      @for (p of o.pathParameters; track p.name) {
        <label class="param">{{ p.name }} <input [attr.aria-label]="'Path ' + p.name" [ngModel]="pathValues()[p.name] ?? ''" (ngModelChange)="setPathValue(p.name, $event)" /></label>
      }
      @for (q of o.queryParameters; track q.name) {
        <label class="param">?{{ q.name }} <input [attr.aria-label]="'Query ' + q.name" [ngModel]="queryValues()[q.name] ?? ''" (ngModelChange)="setQueryValue(q.name, $event)" /></label>
      }
    }

    <label class="block">Headers, one per line
      <textarea aria-label="Headers" rows="2" placeholder="Accept-Language: en" [ngModel]="headersText()" (ngModelChange)="headersText.set($event)"></textarea>
    </label>
    <label class="block">Body
      <textarea aria-label="Body" rows="8" [ngModel]="body()" (ngModelChange)="body.set($event)"></textarea>
    </label>
    <div class="line">
      <input aria-label="Correlation id" placeholder="correlation id (generated if empty)" [ngModel]="correlationId()" (ngModelChange)="correlationId.set($event)" />
      <button class="send" [disabled]="pending()" (click)="send()">Send</button>
    </div>

    @if (error(); as e) {
      <p class="error">{{ e }}</p>
    }

    @if (result(); as r) {
      @switch (r.outcome) {
        @case ('responded') {
          <section class="response">
            <p>
              <span class="status" [class.ok]="r.status < 400" [class.bad]="r.status >= 400">{{ r.status }}</span>
              <span class="elapsed">{{ r.elapsedMs }} ms</span>
              <span class="correlation">correlation id {{ r.correlationId }}</span>
            </p>
            <details><summary>Headers</summary><pre class="headers">{{ headerLines().join('\n') }}</pre></details>
            <pre class="body">{{ prettyBody() }}</pre>
            @if (r.bodyTruncated) {<p class="note">Body truncated at 1 MiB.</p>}
          </section>
        }
        @case ('tokenRejected') {
          <section class="response rejected">
            <p><span class="status bad">Keycloak refused the identity: {{ r.status }}</span> <span class="correlation">correlation id {{ r.correlationId }} (not sent)</span></p>
            <pre class="body">{{ prettyBody() }}</pre>
          </section>
        }
        @case ('unreached') {
          <section class="response unreached">
            <p><span class="status bad">No answer from the platform</span> <span class="elapsed">{{ r.elapsedMs }} ms</span> <span class="correlation">correlation id {{ r.correlationId }}</span></p>
            <p>{{ r.error }}</p>
          </section>
        }
      }
    }

    @if (history().length > 0) {
      <section class="history">
        <h3>History</h3>
        <ul>
          @for (h of history(); track h.seq) {
            <li><button (click)="restore(h)">{{ historyLabel(h) }}</button></li>
          }
        </ul>
      </section>
    }
  </section>
</div>
```

`src/Admin.Web/src/app/features/api/api-page.css`:

```css
.layout { display: grid; grid-template-columns: minmax(14rem, 18rem) 1fr; gap: 1rem; }
.tree { border-right: 1px solid #ddd; padding-right: 0.75rem; }
.tree-head { display: flex; justify-content: space-between; align-items: center; }
.tree h3 { font-size: 0.85rem; margin: 0.75rem 0 0.25rem; text-transform: uppercase; color: #555; }
.tree ul, .history ul { list-style: none; margin: 0; padding: 0; }
.op { width: 100%; text-align: left; background: none; border: 0; padding: 0.2rem 0.25rem; cursor: pointer; font: inherit; }
.op.selected { background: #e6eefc; }
.op.unavailable { color: #767676; font-style: italic; }
.method { display: inline-block; min-width: 3.5rem; font-family: monospace; font-weight: 600; }
.source-error, .error { color: #b00; }
.editor { display: flex; flex-direction: column; gap: 0.5rem; min-width: 0; }
.identity, .line { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
.url { flex: 1; min-width: 20rem; font-family: monospace; }
.block { display: flex; flex-direction: column; font-size: 0.85rem; }
.block textarea { font-family: monospace; }
.param { font-family: monospace; font-size: 0.85rem; }
.policy { color: #555; font-size: 0.85rem; margin: 0; }
.claims, .body, .headers { background: #111; color: #ddd; padding: 0.5rem; overflow: auto; max-height: 40vh; font-size: 0.8rem; }
.status { font-weight: 700; margin-right: 0.75rem; }
.status.ok { color: #080; }
.status.bad { color: #b00; }
.elapsed, .correlation { margin-right: 0.75rem; font-family: monospace; }
.response.unreached, .response.rejected { border-left: 4px solid #b00; padding-left: 0.5rem; }
.history button { background: none; border: 0; cursor: pointer; font-family: monospace; padding: 0.1rem 0; }
```

In `app.routes.ts` add after the `logs` route:

```ts
  { path: 'api', loadComponent: () => import('./features/api/api-page').then((m) => m.ApiPage) },
```

In `app.html` add after the Logs link:

```html
    <a routerLink="/api" routerLinkActive="active">API</a>
```

- [ ] **Step 6: Run the checks**

Run (in `src/Admin.Web`): `npm run lint && npm test && npm run build`
Expected: all pass. Fix lint findings in the new files rather than disabling rules.

- [ ] **Step 7: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
git add src/Admin.Web/src/app
git commit -m "feat(web): API screen with operation tree, identity picker, response pane and history"
```

---

### Task 8: Playwright smoke, docs, and a check against the live documents

**Files:**
- Create: `src/Admin.Web/e2e/api.spec.ts`
- Modify: `README.md` ("What it does today", "Known limits"), `CLAUDE.md` (owners), `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md` (§5.6, §5.7 amendments)

**Interfaces:**
- Consumes: the DOM hooks listed in Task 7 and the fake platform's answers listed in Task 5.
- Produces: nothing later tasks use.

- [ ] **Step 1: The smoke**

`src/Admin.Web/e2e/api.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

// Against the fake platform (src/Admin.Host/Fakes/FakeGateway.cs, FakeKeycloak.cs): the product
// listing is anonymous and names "Walnut desk"; publishing is 401 with no token, 403 for browser
// and 200 for demo, as run-locally.md's "Publish a product" says of the real platform.
test('the api screen lists operations and sends as each identity', async ({ page }) => {
  await page.goto('/api');

  await expect(page.locator('button.op')).toHaveCount(9);

  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByLabel('Identity').selectOption('anonymous');
  await page.getByLabel('Correlation id').fill('e2e-list-1');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  await expect(page.locator('.response .correlation')).toContainText('e2e-list-1');
  await expect(page.locator('.response pre.body')).toContainText('Walnut desk');

  await page.locator('button.op', { hasText: 'PublishProduct' }).click();
  await expect(page.getByLabel('Body')).toHaveValue(/Walnut desk/);
  await page.getByLabel('Correlation id').fill('');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('401');

  await page.getByLabel('Identity').selectOption('user:browser');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('403');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.response .status')).toHaveText('200');
  // Each send replaced the example's zero commandId with a fresh one.
  await expect(page.getByLabel('Body')).not.toHaveValue(/00000000-0000-0000-0000-000000000000/);

  await expect(page.locator('.history li')).toHaveCount(4);
  await expect(page.locator('.history li').first()).toContainText('200 PublishProduct as demo');
});

test('a wrong password is shown as keycloak refusing, not as a platform response', async ({ page }) => {
  await page.goto('/api');

  await page.getByLabel('Identity').selectOption('custom');
  await page.getByLabel('Custom username').fill('demo');
  await page.getByLabel('Custom password').fill('wrong');
  await page.locator('button.op', { hasText: 'GetProducts' }).click();
  await page.getByRole('button', { name: 'Send' }).click();

  await expect(page.locator('.response.rejected')).toContainText('Keycloak refused the identity: 401');
  await expect(page.locator('.response.rejected pre.body')).toContainText('invalid_grant');
});

test('show token displays the demo permissions', async ({ page }) => {
  await page.goto('/api');

  await page.getByLabel('Identity').selectOption('user:demo');
  await page.getByRole('button', { name: 'Show token' }).click();

  await expect(page.locator('.claims')).toContainText('orders:cancel');
});
```

- [ ] **Step 2: Run it**

Run from the repo root: `dotnet build`, then in `src/Admin.Web`: `npm run build && npm run e2e`
Expected: every smoke test passes (the three existing spec files and `api.spec.ts`). Run `npm run e2e` three times in a row; all three runs must pass, as the phase 1 and 2 smokes were held to.

- [ ] **Step 3: Docs**

`README.md`, replace the "What it does today" paragraph with:

```markdown
Phases 0 to 3 of the spec: the Stack screen (Compose services, reachability,
up, down, down with a typed `down -v` confirmation, live output; the reference
client's `npm start` with start, stop and its output), the Logs screen
(follow, service filter, text filter, correlation-id highlight) and the API
screen (Catalog's and Ordering's OpenAPI operations through the gateway, the
BFF quote and every host's readiness; send as anonymous, a realm user or a
custom username and password, with a correlation id; status, timing, headers
and body as the platform returned them; a history of this visit's calls).
Broker inspection and the event trace are the spec's later phases.
```

In "Known limits", replace `- No API console, broker inspection or event trace yet.` with:

```markdown
- The proxy sends only to the gateway, Catalog, Ordering and BFF URLs in
  `Admin:*Url`, and refuses a correlation id the platform would replace
  (anything but 1 to 128 ASCII letters, digits, `-` and `_`).
- Operation examples are the bodies `run-locally.md` sends where there is one,
  otherwise placeholders built from the schema; the documents carry none.
- API history is kept only while the screen is open.
- No broker inspection or event trace yet.
```

`CLAUDE.md`, in the "Owners of facts" paragraph, after the sentence about the fake platform's recordings, add:

```markdown
The gateway's edge policies are copied, with their owner cited, in
`src/Admin.Host/Api/GatewayRoutes.cs`, and the example request bodies in
`src/Admin.Host/Api/RunLocallyExamples.cs`; change them when the backend's
route table or `run-locally.md` changes, not otherwise.
```

Spec `docs/superpowers/specs/2026-09-14-blueprint-admin-design.md`:

- In §5.6, after "Keycloak's error body on a failed grant is returned as-is.", add: "The wire identity is `{ username, password }`: no username is anonymous, a username alone is looked up in `Users`, both is a custom identity; `GET /identity/users` returns usernames only."
- In §5.7, replace "the request schema with an example body" with "an example body (the one `run-locally.md` sends for that operation where there is one, otherwise placeholders built from the request schema, since the documents carry no examples)", and replace "the edge policy from the gateway's route table" with "the edge policy from a cited copy of the gateway's route table (`Api/GatewayRoutes.cs`)".
- In §5.7, after "an absolute URL restricted to the configured surfaces", add "(the gateway, Catalog, Ordering and BFF origins)"; after "generated if absent;", add "a supplied id that breaks those rules is refused, because the backend would silently replace it;".
- In §9, replace the proxy bullet's "with a distinct shape the SPA renders differently" with "with a distinct shape the SPA renders differently: `outcome` is `responded`, `unreached` (no answer, including Keycloak down) or `tokenRejected` (Keycloak refused the identity; nothing was sent)".

- [ ] **Step 4: Compare the fixtures with the live documents (when the platform is up)**

Run: `docker compose -f ../blueprint-backend/deploy/compose/docker-compose.yml ps --format json` from the repo root.

If `catalog-api` and `ordering-api` are running and healthy: start the host in real mode (`dotnet run --project src/Admin.Host`), then `curl -s http://127.0.0.1:5300/api/catalog/operations` and check that both sources are available and that the four operations `PublishProduct`, `GetProducts`, `PlaceOrder`, `CancelOrder` appear with the URLs, parameters and edge policies the fake produces. Also fetch one live document through a token and diff its `paths` keys and each operation's schema `type` shapes against `src/Admin.Host/Fakes/fixtures/openapi-*.json`. Where the live shape differs (for example a path without the trailing slash), update the fixture and any test expectation that encodes it, and commit that as `test: align the OpenAPI fixtures with the live documents`. Stop the host.

If the platform is not running, do not start it; write "live comparison skipped: platform not running" in the task report.

- [ ] **Step 5: Final verification**

Run from the repo root: `dotnet format BlueprintAdmin.slnx --verify-no-changes && dotnet test`; in `src/Admin.Web`: `npm run lint && npm test && npm run build && npm run e2e`.
Expected: all green. Report the counts.

- [ ] **Step 6: Commit**

```bash
git branch --show-current   # must print feat/phase-3-api
git add src/Admin.Web/e2e/api.spec.ts README.md CLAUDE.md docs/superpowers/specs/2026-09-14-blueprint-admin-design.md
git commit -m "test(web): API screen smoke; docs: phase 3 in README, CLAUDE.md and the spec"
```

---

## Self-review against the spec

| Spec item | Task |
|---|---|
| §5.1 `Users` | 1 |
| §5.6 password grant, named or custom, cached until 30 s before expiry, claims decoded, anonymous yields no token, Keycloak's error body as-is | 1, 5 |
| §5.7 ApiCatalog: demo token, rebase onto gateway, curated BFF quote and `/health/ready`, method/path/params/example/edge policy, cached, reload, unavailable not dropped | 2, 3, 5 |
| §5.7 RequestProxy: method, restricted absolute URL, headers, body, identity, correlation id rules, status/headers/body/elapsed/id returned untouched | 4, 5 |
| §5.10 `/identity/users`, `/identity/token`, `/catalog/operations`, `/catalog/reload`, `/proxy` | 5 |
| §6 API screen: tree, editor with path params, headers, example body, `commandId` per send, identity picker, response pane, history | 6, 7 |
| §8 proxy is not an open relay | 4 |
| §9 distinct shape for a request that never reached the upstream; host errors as problem details | 4, 5, 7 |
| §10 fake handlers for Keycloak, OpenAPI and gateway; endpoint tests; Playwright smoke for the new screen | 5, 8 |
| §12 phase 3 | all |

