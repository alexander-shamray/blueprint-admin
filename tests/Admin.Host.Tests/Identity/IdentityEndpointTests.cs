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
