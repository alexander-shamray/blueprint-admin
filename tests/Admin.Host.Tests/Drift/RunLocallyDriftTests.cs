using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Admin.Host.Api;
using Admin.Host.Config;
using Shouldly;

namespace Admin.Host.Tests.Drift;

/// <summary>
/// The request bodies <see cref="RunLocallyExamples"/> copies, against the <c>-Body (@{ … })</c>
/// hashtables run-locally.md sends under each heading. A fresh <c>[guid]::NewGuid()</c> and a
/// <c>$variable</c> both stand for the zero Guid there, as the examples' own summary says.
/// </summary>
public sealed class RunLocallyDriftTests
{
    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    public static TheoryData<string, string> Bodies => new()
    {
        { "Publish a product", RunLocallyExamples.For("PublishProduct")! },
        { "Quote a basket", RunLocallyExamples.Quote },
        { "Place an order", RunLocallyExamples.For("PlaceOrder")! },
        { "Cancel (", RunLocallyExamples.For("CancelOrder")! },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void The_example_body_is_the_one_run_locally_sends(string heading, string example)
    {
        string document = File.ReadAllText(Backend.RunLocally);
        int at = document.IndexOf("\n" + heading, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"run-locally.md has no paragraph starting '{heading}'");

        int body = document.IndexOf("-Body (@{", at, StringComparison.Ordinal);
        body.ShouldBeGreaterThanOrEqualTo(0, $"'{heading}' sends no -Body hashtable");

        JsonNode sent = JsonNode.Parse(new HashtableReader(document, body + "-Body (".Length).ReadValue())!;

        JsonNode.DeepEquals(sent, JsonNode.Parse(example)).ShouldBeTrue($"{heading}\nrun-locally.md: {sent.ToJsonString()}\nexample: {JsonNode.Parse(example)!.ToJsonString()}");
    }

    /// <summary>
    /// The Scenario's watches read the order back through the BFF, so that read is one run-locally.md makes by hand,
    /// at the gateway path the curated operation sends to, with the order id as the one path parameter.
    /// </summary>
    [Fact]
    public void The_order_read_the_scenario_watches_is_one_run_locally_makes()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        ApiOperation read = CuratedOperations.All(new AdminOptions()).Single(o => o.Id == "bff:GetOrder");

        document.ShouldContain($"Invoke-RestMethod \"{read.Url.Replace("{id}", "$orderId", StringComparison.Ordinal)}\" -Headers $auth");
    }

    [Fact]
    public void The_reader_turns_a_nested_hashtable_into_json()
    {
        string source = "@{ a = 'x'; n = 1.5\n  list = @(@{ id = $id; q = 1 })\n  g = [guid]::NewGuid() }";

        JsonNode read = JsonNode.Parse(new HashtableReader(source, 0).ReadValue())!;

        JsonNode.DeepEquals(read, JsonNode.Parse($$"""{"a":"x","n":1.5,"list":[{"id":"{{ZeroGuid}}","q":1}],"g":"{{ZeroGuid}}"}""")).ShouldBeTrue(read.ToJsonString());
    }

    /// <summary>
    /// Just enough PowerShell to read these literals: <c>@{ k = v }</c>, <c>@( v; v )</c>, single-quoted
    /// strings, numbers, <c>$name</c> and <c>[guid]::NewGuid()</c>, separated by <c>;</c> or a newline.
    /// Anything else throws, so a body the reader cannot follow fails rather than compares as empty.
    /// </summary>
    private sealed class HashtableReader(string source, int position)
    {
        private int at = position;

        public string ReadValue()
        {
            SkipSpace();

            if (Take("@{"))
            {
                return ReadMembers('}', member: true);
            }

            if (Take("@("))
            {
                return ReadMembers(')', member: false);
            }

            if (Take("[guid]::NewGuid()"))
            {
                return $"\"{ZeroGuid}\"";
            }

            char c = source[at];

            if (c == '\'')
            {
                int end = source.IndexOf('\'', at + 1);
                string text = source[(at + 1)..end];
                at = end + 1;
                return JsonValue.Create(text).ToJsonString();
            }

            if (c == '$')
            {
                at++;
                ReadWhile(char.IsAsciiLetterOrDigit);
                return $"\"{ZeroGuid}\"";
            }

            if (char.IsAsciiDigit(c))
            {
                string number = ReadWhile(ch => char.IsAsciiDigit(ch) || ch == '.');
                return decimal.Parse(number, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }

            throw new FormatException($"Unreadable PowerShell at {at}: '{source[at..Math.Min(at + 20, source.Length)]}'");
        }

        private string ReadMembers(char close, bool member)
        {
            StringBuilder json = new(member ? "{" : "[");
            bool first = true;

            while (true)
            {
                SkipSpace(alsoSeparators: true);

                if (source[at] == close)
                {
                    at++;
                    return json.Append(member ? '}' : ']').ToString();
                }

                if (!first)
                {
                    json.Append(',');
                }

                first = false;

                if (member)
                {
                    string key = ReadWhile(char.IsAsciiLetterOrDigit);
                    SkipSpace();

                    if (!Take("="))
                    {
                        throw new FormatException($"Expected '=' after '{key}' at {at}.");
                    }

                    json.Append(JsonValue.Create(key).ToJsonString()).Append(':');
                }

                json.Append(ReadValue());
            }
        }

        private bool Take(string token)
        {
            if (string.CompareOrdinal(source, at, token, 0, token.Length) != 0)
            {
                return false;
            }

            at += token.Length;
            return true;
        }

        private string ReadWhile(Func<char, bool> accept)
        {
            int start = at;

            while (at < source.Length && accept(source[at]))
            {
                at++;
            }

            return source[start..at];
        }

        private void SkipSpace(bool alsoSeparators = false) =>
            ReadWhile(c => c is ' ' or '\t' || (alsoSeparators && c is '\r' or '\n' or ';'));
    }
}
