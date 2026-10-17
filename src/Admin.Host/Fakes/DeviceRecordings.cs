namespace Admin.Host.Fakes;

/// <summary>
/// What the device lane's build and three reads (run-locally.md step 4) answer in FakePlatform mode. Not recorded from a
/// live run: this workstation had no Android SDK or emulator when they were written, so each is the shape the document
/// says to expect (an <c>emulator-5554  device</c> line, <c>Access-Control-Allow-Origin: https://localhost</c>, the one
/// redirect) and the build's lines are the client's two scripts' own banners. Exact text is not a contract; the shapes are.
/// </summary>
internal static class DeviceRecordings
{
    public static readonly string[] Build =
    [
        "> blueprint-frontend@0.0.0 emulator:android",
        "> ng build --configuration android && cap sync android",
        "Application bundle generation complete. [4.812 seconds]",
        "Copying web assets from android to android\\app\\src\\main\\assets\\public in 112.40ms",
        "update android in 61.25ms",
        "[info] Sync finished in 0.502s",
    ];

    public static readonly string[] AdbDevices =
    [
        "List of devices attached",
        "emulator-5554\tdevice",
    ];

    public static readonly string[] Preflight =
    [
        "HTTP/1.1 204 No Content",
        "Access-Control-Allow-Origin: https://localhost",
    ];

    public static readonly string[] Realm =
    [
        "blueprint://auth/callback",
        "--- webOrigins ---",
        "https://localhost,capacitor://localhost",
    ];
}
