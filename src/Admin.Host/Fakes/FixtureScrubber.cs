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
        ("an Authorization header", AuthorizationHeader(), $"$1$2\"{Scrubbed}$2\""),
        ("a token response's token", TokenField(), $"$1$2\"{Scrubbed}$2\""),
        ("a password or client secret", SecretField(), $"$1$2\"{Scrubbed}$2\""),
        ("a password or client secret in a form", SecretForm(), $"$1{Scrubbed}"),
        ("a password in a connection URL", UrlPassword(), $"$1{Scrubbed}$2"),
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

    // A JSON string value is matched escape by escape, so a value holding \" is taken whole rather than cut short
    // with its tail left behind. The same rules read a document embedded as an escaped string, where every quote is
    // \" (WireMock.Net logs a body that way): the second group keeps the backslash, so the replacement stays escaped.

    /// <summary>A JSON key, alone or holding an array as WireMock.Net's request log writes headers.</summary>
    [GeneratedRegex(@"(\\?""authorization\\?""\s*:\s*\[?\s*)(?:(\\)""(?!<scrubbed>\\"")(?:(?!\\"").)*\\""|""(?!<scrubbed>"")(?:\\.|[^""\\])*"")", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(\\?""[A-Za-z0-9_]*(?:access_token|refresh_token|id_token)\\?""\s*:\s*)(?:(\\)""(?!<scrubbed>\\"")(?:(?!\\"").)*\\""|""(?!<scrubbed>"")(?:\\.|[^""\\])*"")")]
    private static partial Regex TokenField();

    [GeneratedRegex(@"(\\?""[A-Za-z0-9_]*(?:password|pwd|client_?secret|secret)\\?""\s*:\s*)(?:(\\)""(?!<scrubbed>\\"")(?:(?!\\"").)*\\""|""(?!<scrubbed>"")(?:\\.|[^""\\])*"")", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    /// <summary>
    /// The password grant's own form, as a request log would hold it, and the same keys in a connection string or a
    /// query string (<c>Pwd</c>, <c>secret</c>, a token), and as an environment variable names it. The key needs no boundary before
    /// it, so <c>Section__Key</c>, <c>UPPER_SNAKE</c> and <c>DbPassword</c> are covered; the cost is that a name merely
    /// ending in one, such as a pagination <c>page_token</c>, is scrubbed too, which fails closed. An empty value
    /// followed by a JSON string's closing quote (a quote then a line break or the end, or then a comma, brace,
    /// bracket or colon that is itself followed by structure rather than more value) is left alone rather than read as
    /// an opening quote; a space after the quote is not enough to say so, because a quoted password may begin with one. A bare value ends at <c>&amp;</c> or <c>;</c>, so a connection
    /// string keeps the options that follow its password: the line a person copies must still run. A quoted value, its
    /// quotes possibly JSON-escaped, is taken whole, so a <c>;</c> inside it does not leave the tail behind, and an
    /// unterminated quote takes the rest of the run. A bare value may hold <c>'</c> but not <c>"</c>, because in a
    /// JSON body that is where the string ends, so a bare value holding a double quote keeps the part after it.
    /// </summary>
    [GeneratedRegex(@"((?:password|pwd|client_?secret|secret|(?:access_|refresh_|id_)?token|api_?key)=)(?!\\?""?<scrubbed>)(?!""(?:[,}\]:](?:[\s""{}\[\],:]|$)|[\r\n]|$))(?:\\""(?:(?!\\"").)*\\""|""(?:""""|\\.|[^""\\])*""|'(?:''|\\.|[^'\\])*'|(?:[^&;\s""'\\]|\\\\|\\(?!""))(?:[^&;\s""\\]|\\\\|\\(?!""))*|\\?[""'][^&;\s]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretForm();

    /// <summary>
    /// The password in a URL's userinfo, the part between the second colon and the <c>@</c>, as an AMQP connection string
    /// carries it, raw <c>/</c>, <c>?</c> and <c>#</c> included: a valid URL percent-encodes them, but a configured value
    /// is not always valid. A URL with a port and no <c>@</c> after it, such as <c>http://localhost:5000/api</c>, has no
    /// userinfo and is left alone, and so is one whose "password" is a port followed by a path, query, fragment or the bracket, comma or angle that ends a link, such as
    /// <c>https://h:443?e=a@b</c>. The password runs to the last <c>@</c> before whitespace or a quote, so a raw
    /// <c>@</c> inside it leaves nothing behind; the cost is that a later <c>@</c> in the same URL's query is taken too,
    /// which over-scrubs rather than leaks. An IPv6 literal's brackets cannot be userinfo.
    /// </summary>
    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://[^:/\s@\[\]""']+:)(?!<scrubbed>@)(?!\d+[/?#\])>,])[^\s""']+(@)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPassword();
}
