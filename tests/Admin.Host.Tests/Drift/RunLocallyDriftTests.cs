using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Admin.Host.Api;
using Admin.Host.Config;
using Admin.Host.Scenario;
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
        { "Stock for a product", RunLocallyExamples.For("SetOnHand")! },
        { "Cancel (", RunLocallyExamples.For("CancelOrder")! },
        { "The log keeps every request", RunLocallyExamples.CarrierBookings },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void The_example_body_is_the_one_run_locally_sends(string heading, string example)
    {
        JsonNode sent = Sent(File.ReadAllText(Backend.RunLocally), heading);

        JsonNode.DeepEquals(sent, JsonNode.Parse(example)).ShouldBeTrue($"{heading}\nrun-locally.md: {sent.ToJsonString()}\nexample: {JsonNode.Parse(example)!.ToJsonString()}");
    }

    /// <summary>
    /// The feed read is the one body whose pattern the document builds in a variable first
    /// (<c>$feed = "/v1/shipments/crr_SIM-STRANGE-$orderId/events"</c>), which the reader can only call a variable. So the
    /// body is held with the pattern taken from the example, and the pattern is held as that line: the example's
    /// <c>{orderId}</c> is the document's <c>$orderId</c>.
    /// </summary>
    [Fact]
    public void The_feed_read_is_the_one_run_locally_sends_for_the_order_it_placed()
    {
        const string heading = "And the polls of one script's parcel feed";
        string document = File.ReadAllText(Backend.RunLocally);
        JsonNode sent = Sent(document, heading);
        JsonNode example = JsonNode.Parse(RunLocallyExamples.CarrierFeedPolls)!;
        string pattern = example["Path"]!["Matchers"]![0]!["Pattern"]!.GetValue<string>();

        sent["Path"]!["Matchers"]![0]!["Pattern"] = pattern;

        JsonNode.DeepEquals(sent, example).ShouldBeTrue($"{heading}\nrun-locally.md: {sent.ToJsonString()}\nexample: {example.ToJsonString()}");
        document.ShouldContain($"$feed = \"{pattern.Replace("{orderId}", "$orderId", StringComparison.Ordinal)}\"");
    }

    /// <summary>The <c>-Body (@{ … })</c> hashtable run-locally.md sends under <paramref name="heading"/>, as JSON.</summary>
    private static JsonNode Sent(string document, string heading)
    {
        int at = document.IndexOf("\n" + heading, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"run-locally.md has no paragraph starting '{heading}'");

        int body = document.IndexOf("-Body (@{", at, StringComparison.Ordinal);
        body.ShouldBeGreaterThanOrEqualTo(0, $"'{heading}' sends no -Body hashtable");

        return JsonNode.Parse(new HashtableReader(document, body + "-Body (".Length).ReadValue())!;
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

    /// <summary>
    /// The strip's second column is Ordering's saga read, at the gateway path the fake document gives it, so
    /// that read is one run-locally.md makes by hand beside the BFF's.
    /// </summary>
    [Fact]
    public void The_saga_read_the_strip_sets_beside_the_order_read_is_one_run_locally_makes()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        string gateway = new AdminOptions().GatewayUrl.TrimEnd('/');

        document.ShouldContain($"Invoke-RestMethod \"{gateway}/api/v1/orders/$orderId/saga\" -Headers $auth");
    }

    /// <summary>
    /// The Scenario's review step reads Loki with the query run-locally.md gives for "flagged for review", so the document
    /// holds <see cref="ReviewFlagService.LogQl"/> verbatim, with its own placeholder for the order id.
    /// </summary>
    [Fact]
    public void The_review_flag_query_is_the_one_run_locally_gives_for_the_log_line()
    {
        string document = File.ReadAllText(Backend.RunLocally);

        document.ShouldContain(ReviewFlagService.LogQl("<order id>"));
    }

    /// <summary>
    /// The Mail screen's three reads are the ones run-locally.md makes of the sink by hand, on the configured origin.
    /// </summary>
    [Fact]
    public void The_mail_sink_reads_are_the_ones_run_locally_makes()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        string sink = new AdminOptions().MailSinkUrl;

        document.ShouldContain($"Invoke-RestMethod {sink}/api/v1/messages");
        document.ShouldContain($"Invoke-RestMethod \"{sink}/api/v1/message/$messageId/headers\"");
        document.ShouldContain($"Invoke-RestMethod \"{sink}/api/v1/message/$messageId\"");
    }

    /// <summary>
    /// "Attach a product image by hand": the upload call names the image's type and its own length, and the length is the
    /// image the console sends (<see cref="SampleImage.SizeBytes"/>). <c>$size</c> reads as the zero Guid, so the type is
    /// compared by the document's literal and the size by the line that computes it.
    /// </summary>
    [Fact]
    public void The_upload_example_names_the_type_run_locally_sends_and_the_length_of_the_image_the_console_uploads()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        int at = document.IndexOf("\nAttach a product image by hand", StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0);

        int body = document.IndexOf("-Body (@{", at, StringComparison.Ordinal);
        JsonNode sent = JsonNode.Parse(new HashtableReader(document, body + "-Body (".Length).ReadValue())!;
        JsonNode example = JsonNode.Parse(RunLocallyExamples.ProductImageUpload)!;

        sent["contentType"]!.GetValue<string>().ShouldBe(example["contentType"]!.GetValue<string>());
        document.ShouldContain("$size = (Get-Item .\\desk.png).Length");
        document.ShouldContain("sizeBytes = $size");
    }

    [Fact]
    public void The_saved_address_example_is_the_one_run_locally_puts()
    {
        string document = File.ReadAllText(Backend.RunLocally);
        int at = document.IndexOf("$addr = @{", StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, "run-locally.md no longer builds $addr");

        JsonNode sent = JsonNode.Parse(new HashtableReader(document, at + "$addr = ".Length).ReadValue())!;

        JsonNode.DeepEquals(sent, JsonNode.Parse(RunLocallyExamples.SavedAddress)).ShouldBeTrue($"run-locally.md: {sent.ToJsonString()}");
        document.ShouldContain("/bff/v1/saved-address -Method Put");
    }

    [Fact]
    public void The_publish_with_an_image_sends_the_key_the_upload_returned()
    {
        string document = File.ReadAllText(Backend.RunLocally);

        document.ShouldContain("imageKey = $up.key");
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

            if (Take("$null"))
            {
                return "null";
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
