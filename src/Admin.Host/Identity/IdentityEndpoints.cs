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

        // The clock reads the token the host already holds and never sends it: the page sees when it renews and
        // what it grants, which is what explains a 403, and not the bearer.
        app.MapPost("/api/identity/clock", (IdentityRequest identity, TokenService tokens) =>
            TypedResults.Ok(tokens.Held(identity) is { } held
                ? new TokenClockView(held.Username, true, held.ExpiresAt, TokenService.RenewsAt(held), Permissions(held.Claims))
                : new TokenClockView(identity.Username, false, null, null, [])));

        return app;
    }

    /// <summary>
    /// Owner of the claim type: blueprint-backend <c>Common.Web.PermissionClaim.Type</c>. The realm's mapper writes it
    /// multivalued, so one grant may arrive as a bare string.
    /// </summary>
    internal const string PermissionClaim = "permission";

    internal static string[] Permissions(JsonElement claims) =>
        !claims.TryGetProperty(PermissionClaim, out JsonElement permission) ? []
        : permission.ValueKind == JsonValueKind.Array ? [.. permission.EnumerateArray().Select(p => p.GetString() ?? "")]
        : permission.ValueKind == JsonValueKind.String ? [permission.GetString()!]
        : [];
}

/// <summary>
/// The token clock for one identity (spec §5.6): whether the host holds a token for it, when that token expires
/// and when it stops being reused, and the permissions it carries. Never the token itself.
/// </summary>
public sealed record TokenClockView(string? Username, bool Held, DateTimeOffset? ExpiresAt, DateTimeOffset? RenewsAt, IReadOnlyList<string> Permissions);

public sealed record RealmUserView(string Username);

public sealed record TokenView(string Username, string AccessToken, DateTimeOffset ExpiresAt, JsonElement Claims);
