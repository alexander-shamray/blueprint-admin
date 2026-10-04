using System.Text.RegularExpressions;

namespace Admin.Host.Fakes;

/// <summary>
/// What never reaches a fixture: a JWT, a bearer credential, an <c>Authorization</c> header's value, a token
/// response's token fields, and a password or client secret in a JSON body or a form. Every recorded byte passes
/// through <see cref="Scrub"/> before it is written, and FixtureGateTests runs <see cref="Findings"/> over every
/// file under <c>Fakes/</c>, so a fixture is clean exactly when scrubbing would leave it unchanged. Each rule
/// skips a value already scrubbed, which is what makes that equivalence hold.
/// </summary>
public static partial class FixtureScrubber
{
    public const string Scrubbed = "<scrubbed>";

    private static readonly (string Name, Regex Pattern, string Replacement)[] Rules =
    [
        ("a JWT", Jwt(), Scrubbed),
        ("a bearer credential", Bearer(), $"$1 {Scrubbed}"),
        ("an Authorization header", AuthorizationHeader(), $"$1\"{Scrubbed}\""),
        ("a token response's token", TokenField(), $"$1\"{Scrubbed}\""),
        ("a password or client secret", SecretField(), $"$1\"{Scrubbed}\""),
        ("a password or client secret in a form", SecretForm(), $"$1{Scrubbed}"),
    ];

    public static string Scrub(string text) =>
        Rules.Aggregate(text, (scrubbed, rule) => rule.Pattern.Replace(scrubbed, rule.Replacement));

    /// <summary>The rules that would still change <paramref name="text"/>, by name: empty when it is clean.</summary>
    public static IReadOnlyList<string> Findings(string text) =>
        [.. Rules.Where(rule => rule.Pattern.IsMatch(text)).Select(rule => rule.Name)];

    /// <summary>A JWT's header is base64url JSON, so it always opens <c>eyJ</c>; the signature may be empty.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    /// <summary>Sixteen characters at least, so that prose such as "Bearer token" in a document is left alone.</summary>
    [GeneratedRegex(@"\b(Bearer)\s+(?!<scrubbed>)[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    /// <summary>A JSON key, alone or holding an array as WireMock.Net's request log writes headers.</summary>
    [GeneratedRegex(@"(""authorization""\s*:\s*\[?\s*)""(?!<scrubbed>"")[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(""(?:access_token|refresh_token|id_token)""\s*:\s*)""(?!<scrubbed>"")[^""]*""")]
    private static partial Regex TokenField();

    [GeneratedRegex(@"(""(?:password|client_secret)""\s*:\s*)""(?!<scrubbed>"")[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    /// <summary>The password grant's own form, as a request log would hold it.</summary>
    [GeneratedRegex(@"\b((?:password|client_secret)=)(?!<scrubbed>)[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretForm();
}
