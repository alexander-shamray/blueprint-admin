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
    // Owner: blueprint-backend deploy/compose/keycloak/realm-export.json, users "demo", "browser" and "privacy".
    private static readonly IReadOnlyList<RealmUser> Defaults =
    [
        new RealmUser { Username = "demo", Password = "demo" },
        new RealmUser { Username = "browser", Password = "browser" },
        new RealmUser { Username = "privacy", Password = "privacy" },
    ];

    /// <summary>
    /// The configured users, or the realm export's three when configuration names none. The defaults
    /// are not a list initializer on <see cref="AdminOptions.Users"/> because the configuration
    /// binder appends to an existing list instead of replacing it.
    /// </summary>
    public static IReadOnlyList<RealmUser> Of(AdminOptions options) => options.Users.Count > 0 ? options.Users : Defaults;

    /// <summary>
    /// Whether <paramref name="user"/> holds the password the realm export publishes for that username. It is in the
    /// backend's public repository and its run-locally.md, so it is a development default, not a secret.
    /// </summary>
    public static bool HasPublishedPassword(RealmUser user) =>
        Defaults.Any(d => d.Username == user.Username && d.Password == user.Password);
}
