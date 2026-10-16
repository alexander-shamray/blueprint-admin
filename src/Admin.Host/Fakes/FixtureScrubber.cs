using System.Text.RegularExpressions;

namespace Admin.Host.Fakes;

/// <summary>
/// What never reaches a fixture: a JWT, a bearer credential, an <c>Authorization</c> header's value or every value of its array, a token
/// response's token fields, a bare token or API key, and a password or client secret in a JSON body or a form. Every recorded byte passes
/// through <see cref="Scrub"/> before it is written, and FixtureGateTests runs <see cref="Findings"/> over every
/// file under <c>Fakes/</c>, so a fixture is clean exactly when scrubbing would leave it unchanged. Each rule
/// skips a value already scrubbed, which is what makes that equivalence hold.
/// <para>
/// Free text cannot say where a secret ends, and no parser changes that, so these shapes still leave part of one
/// behind, each chosen over mangling what is not a secret (#153): a URL password of digits and then <c>/</c>, <c>?</c>,
/// <c>#</c>, a bracket, a comma or an angle reads as a port; a bare form or connection-string value holding a double
/// quote keeps what follows it; an unterminated quote takes the run only to <c>;</c>, <c>&amp;</c> or whitespace; and a
/// quoted value closed by a quote and then a space and other characters reads as an opening quote. A test pins each of
/// the first three.
/// </para>
/// </summary>
public static partial class FixtureScrubber
{
    public const string Scrubbed = "<scrubbed>";

    private static readonly (string Name, Func<string, string> Apply)[] Rules =
    [
        ("a JWT", text => Jwt().Replace(text, Scrubbed)),
        ("a bearer credential", text => Bearer().Replace(text, $"$1 {Scrubbed}")),
        ("an Authorization header", text => AuthorizationHeader().Replace(text, AuthorizationValues)),
        ("a token or API key field", text => TokenField().Replace(text, JsonValue)),
        ("a password or client secret", text => SecretField().Replace(text, JsonValue)),
        ("a password or client secret in a form", text => SecretForm().Replace(text, $"$1{Scrubbed}")),
        ("a password in a connection URL", text => UrlPassword().Replace(text, $"$1{Scrubbed}$2")),
    ];

    public static string Scrub(string text) =>
        Rules.Aggregate(text, (scrubbed, rule) => rule.Apply(scrubbed));

    /// <summary>
    /// The rules that would still change <paramref name="text"/>, by name: empty when it is clean. A rule finds what it
    /// would change, which is what makes a fixture clean exactly when scrubbing leaves it unchanged.
    /// </summary>
    public static IReadOnlyList<string> Findings(string text) =>
        [.. Rules.Where(rule => rule.Apply(text) != text).Select(rule => rule.Name)];

    /// <summary>
    /// A key's value replaced, in the quotes the document it sits in writes: a plain pair in plain ones, a pair inside an
    /// escaped document, at whatever depth, in the same run of backslashes, which a bare number takes from its key's.
    /// </summary>
    private static string JsonValue(Match match)
    {
        string quote = match.Groups["r"].Success ? match.Groups["r"].Value + "\""
            : match.Groups["n"].Success ? match.Groups["kc"].Value + "\""
            : "\"";

        return match.Groups["k"].Value + quote + Scrubbed + quote;
    }

    /// <summary>An Authorization header's value, or every element of the array WireMock.Net writes it as.</summary>
    private static string AuthorizationValues(Match match) =>
        match.Groups["k"].Value + match.Groups["o"].Value + AuthorizationElement().Replace(match.Groups["v"].Value, element =>
        {
            if (element.Groups["c"].Value == Scrubbed)
            {
                return element.Value;
            }

            Group run = element.Groups["r"];

            return run.Success ? $"{run.Value}\"{Scrubbed}{run.Value}\"" : $"\"{Scrubbed}\"";
        });

    /// <summary>A JWT's header is base64url JSON, so it always opens <c>eyJ</c>; the signature may be empty.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    /// <summary>Sixteen characters at least, so that prose such as "Bearer token" in a document is left alone.</summary>
    [GeneratedRegex(@"\b(Bearer)\s+(?!<scrubbed>)[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // A JSON string value is matched escape by escape, so a value holding \" is taken whole rather than cut short
    // with its tail left behind. The same rules read a document embedded as an escaped string, where every quote is
    // \" (WireMock.Net logs a body that way), and one embedded in that, at any depth: the run of backslashes before a
    // value's quote is captured and its closing quote is the same run, so the replacement stays in the document's own
    // escaping. A bare number is a value too, and a secret written as one is replaced by a string. The run of n
    // backslashes that closes a value at a depth is the only run of n before a quote; inside the value a literal
    // backslash is a block of 2n+2 of them, and an escaped quote is 2n+1 followed by the quote, which is how a value
    // ending in a backslash and a value holding a quote are both told from the end of the string.
    private const string PlainOrEscapedString = @"(?:(?<q>\\+)""(?:[^\\\n]|\k<q>\\\k<q>\\|\k<q>\k<q>\\""|\\(?!\\*""))*\k<q>""|""(?:\\.|[^""\\])*"")";

    /// <summary>A JSON key, alone or holding an array as WireMock.Net's request log writes headers: every element of it is a credential.</summary>
    [GeneratedRegex(
        @"(?<k>\\*""authorization\\*""\s*:\s*)(?:(?<o>\[)(?<v>\s*" + PlainOrEscapedString + @"(?:\s*,\s*" + PlainOrEscapedString + @")*)|(?<v>" + PlainOrEscapedString + @"))",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    /// <summary>
    /// One string of an Authorization array, scrubbed or not: every string is matched whole, so that one left alone is
    /// not misread as the gap before the next. <c>r</c> is its run of backslashes inside an escaped document, <c>c</c> its text.
    /// </summary>
    [GeneratedRegex(@"(?:(?<r>\\+)""(?<c>(?:[^\\\n]|\k<r>\\\k<r>\\|\k<r>\k<r>\\""|\\(?!\\*""))*)\k<r>""|""(?<c>(?:\\.|[^""\\])*)"")")]
    private static partial Regex AuthorizationElement();

    // A key ending in token or api_key, so access_token, id_token, a bare token and apiKey are all covered. As with
    // SecretForm's keys, a name merely ending in one (a pagination page_token) is scrubbed too, which fails closed.
    [GeneratedRegex(@"(?<k>\\*""[A-Za-z0-9_]*(?:token|api_?key)(?<kc>\\*)""\s*:\s*)(?:(?<r>\\+)""(?!<scrubbed>\k<r>"")(?:[^\\\n]|\k<r>\\\k<r>\\|\k<r>\k<r>\\""|\\(?!\\*""))*\k<r>""|""(?!<scrubbed>"")(?:\\.|[^""\\])*""|(?<n>-?\d[\d.eE+-]*))", RegexOptions.IgnoreCase)]
    private static partial Regex TokenField();

    [GeneratedRegex(@"(?<k>\\*""[A-Za-z0-9_]*(?:password|pwd|client_?secret|secret)(?<kc>\\*)""\s*:\s*)(?:(?<r>\\+)""(?!<scrubbed>\k<r>"")(?:[^\\\n]|\k<r>\\\k<r>\\|\k<r>\k<r>\\""|\\(?!\\*""))*\k<r>""|""(?!<scrubbed>"")(?:\\.|[^""\\])*""|(?<n>-?\d[\d.eE+-]*))", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    /// <summary>
    /// The password grant's own form, as a request log would hold it, and the same keys in a connection string or a
    /// query string (<c>Pwd</c>, <c>secret</c>, a token), and as an environment variable names it. The key needs no boundary before
    /// it, so <c>Section__Key</c>, <c>UPPER_SNAKE</c> and <c>DbPassword</c> are covered; the cost is that a name merely
    /// ending in one, such as a pagination <c>page_token</c>, is scrubbed too, which fails closed. Spaces or tabs after
    /// the <c>=</c> are not part of the value, so a key, an equals sign, a space and a word reads as a secret; the cost is
    /// prose that writes a key and an equals sign before a word, whose word is scrubbed, which fails closed too. An empty value
    /// followed by a JSON string's closing quote (a quote then a line break or the end, or then a comma, brace,
    /// bracket or colon that is itself followed by structure rather than more value) is left alone rather than read as
    /// an opening quote; a space after the quote is not enough to say so, because a quoted password may begin with one. A bare value ends at <c>&amp;</c> or <c>;</c>, so a connection
    /// string keeps the options that follow its password: the line a person copies must still run. A quoted value, its
    /// quotes possibly JSON-escaped, is taken whole, so a <c>;</c> inside it does not leave the tail behind, and an
    /// unterminated quote takes the rest of the run. A bare value may hold <c>'</c> but not <c>"</c>, because in a
    /// JSON body that is where the string ends, so a bare value holding a double quote keeps the part after it.
    /// </summary>
    [GeneratedRegex(@"((?:password|pwd|client_?secret|secret|(?:access_|refresh_|id_)?token|api_?key)=)[ \t]*(?!\\?""?<scrubbed>)(?!\\?""(?:[,}\]:](?:[\s""{}\[\],:\\]|$)|[\r\n]|$))(?:\\""(?:(?!\\"").)*\\""|""(?:""""|\\.|[^""\\])*""|'(?:''|\\.|[^'\\])*'|(?:[^&;\s""'\\]|\\\\|\\(?!""))(?:[^&;\s""\\]|\\\\|\\(?!""))*|\\?[""'][^&;\s]*)", RegexOptions.IgnoreCase)]
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
