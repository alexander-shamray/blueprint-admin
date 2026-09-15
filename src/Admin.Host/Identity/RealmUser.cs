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
