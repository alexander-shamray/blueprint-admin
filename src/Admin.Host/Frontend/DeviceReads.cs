using Admin.Host.Compose;
using Admin.Host.Config;
using Admin.Host.Jobs;
using Admin.Host.Stack;

namespace Admin.Host.Frontend;

/// <summary>
/// The three reads run-locally.md step 4 makes before a first device run, each a line of that document run as
/// written: an emulator is attached, the gateway admits the packaged app's origin, and the running realm's
/// <c>mobile-app</c> client redirects where the client declares. None changes anything. A mismatch is reported here and
/// raised in the repository that owns it, never fixed from the console (spec §5.4). A read that did not answer is
/// <see cref="DoctorState.Unknown"/> where the stack may simply be down, and a problem where the silence is the cause
/// (no <c>adb</c>, so no device run).
/// </summary>
public sealed class DeviceReads(IProcessRunner runner, RepoPaths paths, TimeProvider time)
{
    /// <summary>Owner: run-locally.md step 4, first read. Android Studio's default SDK place, as the document spells it.</summary>
    internal const string AdbDevicesLine = """& "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" devices""";

    /// <summary>Owner: run-locally.md step 4, second read. A device run needs this origin only; iOS's is not asked.</summary>
    internal const string CorsLine =
        """curl.exe -s -i -X OPTIONS http://localhost:5000/api/v1/catalog/products -H "Origin: https://localhost" -H "Access-Control-Request-Method: GET" | Select-String 'HTTP/|Access-Control-Allow-Origin'""";

    /// <summary>
    /// Owner: run-locally.md step 4, third read: three lines, the admin token, the client, then what it redirects to, the
    /// marker line, and its web origins.
    /// </summary>
    internal static readonly string[] RealmLines =
    [
        """$t = (Invoke-RestMethod http://localhost:8080/realms/master/protocol/openid-connect/token -Method Post -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = 'admin' }).access_token""",
        """$c = (Invoke-RestMethod 'http://localhost:8080/admin/realms/commerce/clients?clientId=mobile-app' -Headers @{ Authorization = "Bearer $t" })[0]""",
        """$c.redirectUris; '--- webOrigins ---'; $c.webOrigins -join ','""",
    ];

    /// <summary>Owner: run-locally.md step 4, the line the realm read prints between the redirects and the web origins.</summary>
    internal const string WebOriginsMarker = "--- webOrigins ---";

    /// <summary>The origin the packaged Android app sends (the client's Capacitor <c>androidScheme</c>), which the gateway must admit.</summary>
    internal const string AppOrigin = "https://localhost";

    /// <summary>Owner: the client's <c>nativeRedirectUri</c>, which the realm's <c>mobile-app</c> client must list and nothing else.</summary>
    internal const string NativeRedirectUri = "blueprint://auth/callback";

    public async Task<DoctorView> ReadAsync(CancellationToken cancellationToken)
    {
        Task<CommandOutput> adb = PowerShellAsync(AdbDevicesLine, cancellationToken);
        Task<CommandOutput> cors = PowerShellAsync(CorsLine, cancellationToken);
        Task<CommandOutput> realm = PowerShellAsync(string.Join('\n', RealmLines), cancellationToken);

        await Task.WhenAll(adb, cors, realm);

        return new DoctorView([Emulator(adb.Result), Origin(cors.Result), Redirect(realm.Result)]);
    }

    internal static DoctorCheck Emulator(CommandOutput adb)
    {
        const string name = "Emulator attached";

        if (adb.Error is { } error)
        {
            return new(name, DoctorState.Problem, $"adb did not answer, so no device run can start: {error}");
        }

        // A line of devices is "serial<TAB>state"; the daemon's own notices have no tab.
        string[][] devices = [.. adb.Stdout.Select(l => l.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 2)];
        string[] unusable = [.. devices.Where(d => d[1] != "device").Select(d => $"{d[0]} is {d[1]}")];

        if (unusable.Length > 0)
        {
            return new(name, DoctorState.Problem, $"{string.Join(", ", unusable)}; a device run needs a line ending device.");
        }

        return devices.Length == 0
            ? new(name, DoctorState.Problem, "No emulator is attached. Start the AVD from Android Studio's Device Manager; the console does not.")
            : new(name, DoctorState.Ok, $"{string.Join(", ", devices.Select(d => d[0]))} attached.");
    }

    internal static DoctorCheck Origin(CommandOutput cors)
    {
        const string name = "Gateway admits the app's origin";

        if (cors.Error is { } error)
        {
            return new(name, DoctorState.Unknown, $"The preflight did not run: {error}");
        }

        if (!cors.Stdout.Any(l => l.TrimStart().StartsWith("HTTP/", StringComparison.Ordinal)))
        {
            return new(name, DoctorState.Unknown, "The gateway did not answer the preflight. Start the stack first.");
        }

        string? allowed = cors.Stdout
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Access-Control-Allow-Origin:", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1].Trim();

        return allowed switch
        {
            null => new(name, DoctorState.Problem, $"The gateway answered the preflight with no Access-Control-Allow-Origin, so it does not admit {AppOrigin}. Its CORS list is the backend's to change."),
            AppOrigin => new(name, DoctorState.Ok, $"The gateway admits {AppOrigin}."),
            _ => new(name, DoctorState.Problem, $"The gateway answered Access-Control-Allow-Origin: {allowed}, not {AppOrigin}. Its CORS list is the backend's to change."),
        };
    }

    internal static DoctorCheck Redirect(CommandOutput realm)
    {
        const string name = "Realm redirect for mobile-app";

        if (realm.Error is { } error)
        {
            return new(name, DoctorState.Unknown, $"Keycloak's admin API did not answer: {error}");
        }

        // The output is the redirects, one a line, the marker, then webOrigins joined (blank when the client lists none).
        // A redirect is every line before the marker; what follows it is not read.
        string[] lines = [.. realm.Stdout.Select(l => l.Trim()).Where(l => l.Length > 0)];
        int marker = Array.IndexOf(lines, WebOriginsMarker);

        if (marker < 0 && lines.Length > 0)
        {
            return new(name, DoctorState.Problem, $"Keycloak answered with no {WebOriginsMarker} line, so the redirects cannot be told from the web origins. run-locally.md step 4 prints it; the document and RealmLines have drifted.");
        }

        string[] redirects = marker < 0 ? [] : lines[..marker];

        return redirects switch
        {
            [] => new(name, DoctorState.Problem, "The running realm's mobile-app client has no redirect (or the client is absent). The realm is imported only into an empty database; it is the backend's to change."),
            [NativeRedirectUri] => new(name, DoctorState.Ok, $"mobile-app redirects to exactly {NativeRedirectUri}."),
            _ => new(name, DoctorState.Problem, $"mobile-app redirects to {string.Join(", ", redirects)}, not exactly {NativeRedirectUri}, which the client sends. The realm is the backend's to change."),
        };
    }

    private Task<CommandOutput> PowerShellAsync(string line, CancellationToken cancellationToken) =>
        OneShot.CompleteAsync(
            runner,
            runner.Start(new ProcessSpec("powershell", ["-NoProfile", "-Command", line], paths.BackendDir) { Listed = false }),
            "powershell",
            time,
            cancellationToken);
}
