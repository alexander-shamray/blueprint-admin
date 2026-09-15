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
