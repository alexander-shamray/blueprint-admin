using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FixtureScrubberTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJkZW1vIn0.c2lnbmF0dXJlLWJ5dGVz";

    [Fact]
    public void A_jwt_anywhere_in_the_text_is_scrubbed()
    {
        FixtureScrubber.Scrub($"token {Jwt} in a log line").ShouldBe("token <scrubbed> in a log line");
    }

    [Fact]
    public void A_bearer_credential_is_scrubbed_and_the_scheme_kept()
    {
        FixtureScrubber.Scrub("Authorization: bearer 0123456789abcdef0123").ShouldBe("Authorization: bearer <scrubbed>");
    }

    [Fact]
    public void Prose_naming_the_bearer_scheme_is_left_alone()
    {
        const string description = """{"description":"Bearer token from the realm","scheme":"bearer"}""";

        FixtureScrubber.Scrub(description).ShouldBe(description);
    }

    [Fact]
    public void An_authorization_header_is_scrubbed_whatever_its_scheme_and_in_wiremocks_array_form()
    {
        FixtureScrubber.Scrub("""{"Authorization":"Basic ZGVtbzpkZW1v"}""").ShouldBe("""{"Authorization":"<scrubbed>"}""");
        FixtureScrubber.Scrub("""{"headers":{"authorization": [ "Basic ZGVtbzpkZW1v" ]}}""")
            .ShouldBe("""{"headers":{"authorization": [ "<scrubbed>" ]}}""");
    }

    [Fact]
    public void A_token_responses_tokens_are_scrubbed_and_its_other_fields_kept()
    {
        string scrubbed = FixtureScrubber.Scrub("""{"access_token":"opaque","refresh_token":"r","id_token":"i","expires_in":300}""");

        scrubbed.ShouldBe("""{"access_token":"<scrubbed>","refresh_token":"<scrubbed>","id_token":"<scrubbed>","expires_in":300}""");
    }

    [Theory]
    [InlineData("""{"token":"opaque","n":1}""", """{"token":"<scrubbed>","n":1}""")]
    [InlineData("""{"api_key":"k","n":1}""", """{"api_key":"<scrubbed>","n":1}""")]
    [InlineData("""{"apiKey":"k","n":1}""", """{"apiKey":"<scrubbed>","n":1}""")]
    public void A_bare_token_or_api_key_in_json_is_scrubbed(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(text).ShouldNotBeEmpty();
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Fact]
    public void A_password_or_client_secret_is_scrubbed_in_json_and_in_a_form()
    {
        FixtureScrubber.Scrub("""{"Password":"demo","client_secret":"s3cret"}""").ShouldBe("""{"Password":"<scrubbed>","client_secret":"<scrubbed>"}""");
        FixtureScrubber.Scrub("grant_type=password&username=demo&password=demo&client_id=web-app")
            .ShouldBe("grant_type=password&username=demo&password=<scrubbed>&client_id=web-app");
    }

    [Fact]
    public void A_schema_that_declares_a_password_property_is_not_a_password()
    {
        const string schema = """{"properties":{"password":{"type":"string"}}}""";

        FixtureScrubber.Scrub(schema).ShouldBe(schema);
    }

    /// <summary>
    /// Every rule breaks the input before and none after: a rule whose skip of an already scrubbed value broke would
    /// keep finding its own replacement, and the gate would then fail every recording it wrote.
    /// </summary>
    [Fact]
    public void Every_rule_finds_its_input_and_nothing_in_what_it_wrote()
    {
        string raw = $$"""
            {"access_token":"{{Jwt}}","password":"demo","h":"Bearer 0123456789abcdef0123",
             "Authorization":["Basic ZGVtbzpkZW1v"],"body":"username=demo&password=demo",
             "broker":"amqp://bff-svc:local-dev-bff@127.0.0.1:5672"}
            """;

        FixtureScrubber.Findings(raw).Count.ShouldBe(7);

        string once = FixtureScrubber.Scrub(raw);

        FixtureScrubber.Findings(once).ShouldBeEmpty();
        FixtureScrubber.Scrub(once).ShouldBe(once);
    }

    [Fact]
    public void A_value_holding_an_escaped_quote_is_scrubbed_whole()
    {
        FixtureScrubber.Scrub("""{"password":"pa\"ss","next":1}""").ShouldBe("""{"password":"<scrubbed>","next":1}""");
    }

    [Fact]
    public void A_client_secret_is_scrubbed_in_either_spelling()
    {
        FixtureScrubber.Scrub("""{"clientSecret":"s"}""").ShouldBe("""{"clientSecret":"<scrubbed>"}""");
        FixtureScrubber.Scrub("clientSecret=s&x=1").ShouldBe("clientSecret=<scrubbed>&x=1");
    }

    [Fact]
    public void A_connection_strings_password_is_scrubbed_and_the_options_after_it_kept()
    {
        FixtureScrubber.Scrub("Server=s;User Id=sa;Password=pw;TrustServerCertificate=True")
            .ShouldBe("Server=s;User Id=sa;Password=<scrubbed>;TrustServerCertificate=True");
        FixtureScrubber.Scrub("Server=s;Password=pw").ShouldBe("Server=s;Password=<scrubbed>");
    }

    [Fact]
    public void A_quoted_connection_password_is_scrubbed_whole_even_when_it_holds_a_semicolon()
    {
        FixtureScrubber.Scrub("""Server=s;Password="a;b";TrustServerCertificate=True""")
            .ShouldBe("Server=s;Password=<scrubbed>;TrustServerCertificate=True");
        FixtureScrubber.Scrub("Server=s;Password='a;b';Encrypt=True").ShouldBe("Server=s;Password=<scrubbed>;Encrypt=True");
        FixtureScrubber.Scrub("""{"cs":"Server=s;Password=\"a;b\";Encrypt=True"}""")
            .ShouldBe("""{"cs":"Server=s;Password=<scrubbed>;Encrypt=True"}""");
    }

    [Theory]
    [InlineData("Server=s;Pwd=hunter2;Encrypt=True", "Server=s;Pwd=<scrubbed>;Encrypt=True")]
    [InlineData("https://h/cb?access_token=abc123&state=1", "https://h/cb?access_token=<scrubbed>&state=1")]
    [InlineData("https://h/x?api_key=k&y=2", "https://h/x?api_key=<scrubbed>&y=2")]
    [InlineData("Secret=s;Id=1", "Secret=<scrubbed>;Id=1")]
    public void A_differently_spelled_secret_key_is_scrubbed(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""Password="ab\"cd1234";X=1""", "Password=<scrubbed>;X=1")]
    [InlineData("""Password="a""b;c";Encrypt=True""", "Password=<scrubbed>;Encrypt=True")]
    [InlineData("pwd=it's-secret99;X=1", "pwd=<scrubbed>;X=1")]
    [InlineData("""password="abc""", "password=<scrubbed>")]
    [InlineData("""Password="<scrubbed>";X=1""", """Password="<scrubbed>";X=1""")]
    [InlineData("""Server=s;Password=",hunter2";X=1""", "Server=s;Password=<scrubbed>;X=1")]
    [InlineData("""pwd="}Zk9";X=1""", "pwd=<scrubbed>;X=1")]
    [InlineData("""Password=":abc";X=1""", "Password=<scrubbed>;X=1")]
    [InlineData("""{"b":"{\"c\":\"Password=\"s3cret;x\",\"d\":1}"}""", """{"b":"{\"c\":\"Password=<scrubbed>,\"d\":1}"}""")]
    [InlineData("Server=s;Password='a''b';X=1", "Server=s;Password=<scrubbed>;X=1")]
    [InlineData("Password='abc;X=1", "Password=<scrubbed>;X=1")]
    [InlineData("""{"cs":"{\"ConnectionString\":\"Server=s;Password=pw\"}"}""", """{"cs":"{\"ConnectionString\":\"Server=s;Password=<scrubbed>\"}"}""")]
    public void A_quoted_value_is_taken_whole_whatever_escape_it_uses_and_a_scrubbed_one_is_left(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("refresh_token=r&x=1", "refresh_token=<scrubbed>&x=1")]
    [InlineData("id_token=i;x=1", "id_token=<scrubbed>;x=1")]
    [InlineData("token=t&x=1", "token=<scrubbed>&x=1")]
    [InlineData("apikey=k&x=1", "apikey=<scrubbed>&x=1")]
    [InlineData("Server=s;ClientSecret=c;X=1", "Server=s;ClientSecret=<scrubbed>;X=1")]
    [InlineData("KEYCLOAK_ADMIN_PASSWORD=admin", "KEYCLOAK_ADMIN_PASSWORD=<scrubbed>")]
    [InlineData("POSTGRES_PASSWORD=s3cret", "POSTGRES_PASSWORD=<scrubbed>")]
    [InlineData("Keycloak__ClientSecret=abc", "Keycloak__ClientSecret=<scrubbed>")]
    [InlineData("Redis__Password=hunter2", "Redis__Password=<scrubbed>")]
    [InlineData("Keycloak__AdminPassword=hunter2", "Keycloak__AdminPassword=<scrubbed>")]
    [InlineData("NewPassword=abc&ConfirmPassword=abc", "NewPassword=<scrubbed>&ConfirmPassword=<scrubbed>")]
    [InlineData("GET /orders?page_token=abc123&limit=10", "GET /orders?page_token=<scrubbed>&limit=10")]
    public void Every_key_the_form_rule_names_is_scrubbed(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"body":"username=demo&password=","method":"POST"}""")]
    [InlineData("""{"a":["password="],"b":1}""")]
    [InlineData("""{"body":"password="}""")]
    [InlineData("{\"cmd\": \"x password=\"\n}")]
    [InlineData("password=\"")]
    [InlineData("""{"body":"{\"c\":\"Password=\",\"x\":\"y\"}"}""")]
    [InlineData("""{"body":"{\"a\":[\"password=\"],\"b\":1}"}""")]
    public void An_empty_value_before_a_json_strings_closing_quote_is_left_as_it_is(string json)
    {
        FixtureScrubber.Scrub(json).ShouldBe(json);
        FixtureScrubber.Findings(json).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"pwd":"x"}""", """{"pwd":"<scrubbed>"}""")]
    [InlineData("""{"AdminPassword":"hunter2","n":1}""", """{"AdminPassword":"<scrubbed>","n":1}""")]
    [InlineData("""{"secret":"x"}""", """{"secret":"<scrubbed>"}""")]
    [InlineData("""{"my_refresh_token":"r"}""", """{"my_refresh_token":"<scrubbed>"}""")]
    [InlineData("""{"body":"{\"password\":\"demo\",\"u\":1}"}""", """{"body":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    [InlineData("""{"body":"{\"access_token\":\"abc\"}"}""", """{"body":"{\"access_token\":\"<scrubbed>\"}"}""")]
    [InlineData("""{"b":"{\"Authorization\":\"Basic ZGVtbzpkZW1v\"}"}""", """{"b":"{\"Authorization\":\"<scrubbed>\"}"}""")]
    [InlineData("""{"body":"{\"password\":\"<scrubbed>\"}"}""", """{"body":"{\"password\":\"<scrubbed>\"}"}""")]
    public void A_json_key_is_scrubbed_whatever_prefixes_its_name(string json, string expected)
    {
        FixtureScrubber.Scrub(json).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"body":"password=ab\\","x":1}""", """{"body":"password=<scrubbed>","x":1}""")]
    [InlineData("""{"body":"password=ab\\\\","x":1}""", """{"body":"password=<scrubbed>","x":1}""")]
    [InlineData("""{"body":"password=a\b","x":1}""", """{"body":"password=<scrubbed>","x":1}""")]
    public void A_bare_value_ending_in_escaped_backslashes_leaves_the_json_string_closed(string json, string expected)
    {
        FixtureScrubber.Scrub(json).ShouldBe(expected);
    }

    [Theory]
    [InlineData("amqp://u:pa/ss@h:5672", "amqp://u:<scrubbed>@h:5672")]
    [InlineData("amqp://u:pa?ss@h:5672", "amqp://u:<scrubbed>@h:5672")]
    [InlineData("amqp://u:pa#ss@h:5672", "amqp://u:<scrubbed>@h:5672")]
    public void A_url_password_holding_a_raw_separator_is_scrubbed_whole(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
    }

    [Fact]
    public void A_url_password_holding_a_raw_at_sign_is_scrubbed_to_the_last_one()
    {
        FixtureScrubber.Scrub("amqp://guest:p@ssw0rd-long@rabbit:5672/").ShouldBe("amqp://guest:<scrubbed>@rabbit:5672/");
    }

    [Fact]
    public void An_ipv6_literal_is_not_userinfo()
    {
        const string url = "curl http://[::1]:5000/search?e=a@b";

        FixtureScrubber.Scrub(url).ShouldBe(url);
    }

    /// <summary>The accepted cost of the port guard: a password that is digits and then a separator reads as a port.</summary>
    [Fact]
    public void A_url_password_of_digits_then_a_separator_is_read_as_a_port_and_left_alone()
    {
        const string url = "amqp://u:12345#abc@h:5672";

        FixtureScrubber.Scrub(url).ShouldBe(url);
    }

    [Theory]
    [InlineData("http://localhost:5000/api?mail=a@b.com")]
    [InlineData("https://h:443/x#frag@y")]
    [InlineData("[http://localhost:5000](mailto:a@b.com)")]
    [InlineData("see (http://h:5000), mail x@y")]
    public void A_port_followed_by_a_path_is_not_a_url_password(string text)
    {
        FixtureScrubber.Scrub(text).ShouldBe(text);
    }

    [Fact]
    public void A_url_without_userinfo_is_left_alone_whatever_its_query_holds()
    {
        const string url = "https://h:443?e=a@b";

        FixtureScrubber.Scrub(url).ShouldBe(url);
    }

    [Fact]
    public void A_password_in_a_connection_url_is_scrubbed_and_its_user_and_host_kept()
    {
        FixtureScrubber.Scrub("ConnectionStrings__RabbitMq=amqp://bff-svc:local-dev-bff@127.0.0.1:5672")
            .ShouldBe("ConnectionStrings__RabbitMq=amqp://bff-svc:<scrubbed>@127.0.0.1:5672");
        FixtureScrubber.Scrub("amqp://bff-svc:<scrubbed>@127.0.0.1:5672").ShouldBe("amqp://bff-svc:<scrubbed>@127.0.0.1:5672");
    }

    [Theory]
    [InlineData("http://localhost:5000/api/v1/orders")]
    [InlineData("https://github.com/alexander-shamray/blueprint-backend/blob/main/docs/runbooks/error-queue.md")]
    [InlineData("see amqp://127.0.0.1:5672 and http://user@host/x")]
    public void A_url_with_no_password_in_it_is_left_alone(string text)
    {
        FixtureScrubber.Scrub(text).ShouldBe(text);
        FixtureScrubber.Findings(text).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"password":12345}""", """{"password":"<scrubbed>"}""")]
    [InlineData("""{"client_secret": -1.5e3,"n":1}""", """{"client_secret": "<scrubbed>","n":1}""")]
    [InlineData("""{"api_key":42,"n":1}""", """{"api_key":"<scrubbed>","n":1}""")]
    [InlineData("""{"access_token":9007199254740993}""", """{"access_token":"<scrubbed>"}""")]
    [InlineData("""{"body":"{\"password\":12345,\"u\":1}"}""", """{"body":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    public void A_secret_written_as_a_json_number_is_scrubbed_in_the_quotes_of_the_document_it_sits_in(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(text).ShouldNotBeEmpty();
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"expires_in":300,"max_tokens":100,"passwordMinLength":8}""")]
    [InlineData("""{"password":true,"token":null}""")]
    public void A_number_under_a_key_that_does_not_end_in_a_secret_word_is_left_alone(string text)
    {
        FixtureScrubber.Scrub(text).ShouldBe(text);
    }

    [Theory]
    [InlineData("""{"authorization": ["Basic aaa", "Basic bbb"]}""", """{"authorization": ["<scrubbed>", "<scrubbed>"]}""")]
    [InlineData("""{"Authorization":["Basic a","Bearer b","x"],"n":1}""", """{"Authorization":["<scrubbed>","<scrubbed>","<scrubbed>"],"n":1}""")]
    [InlineData("""{"authorization": ["<scrubbed>", "Basic bbb"]}""", """{"authorization": ["<scrubbed>", "<scrubbed>"]}""")]
    [InlineData("""{"b":"{\"authorization\":[\"Basic a\",\"Basic b\"]}"}""", """{"b":"{\"authorization\":[\"<scrubbed>\",\"<scrubbed>\"]}"}""")]
    public void Every_element_of_a_multi_value_authorization_array_is_scrubbed(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(text).ShouldNotBeEmpty();
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    /// <summary>A value that only begins like a scrubbed one is still a credential: the skip is the whole value, as the other rules' is.</summary>
    [Theory]
    [InlineData("""{"authorization": ["<scrubbed>hunter2", "<scrubbed>"]}""", """{"authorization": ["<scrubbed>", "<scrubbed>"]}""")]
    [InlineData("""{"authorization":"<scrubbed>hunter2"}""", """{"authorization":"<scrubbed>"}""")]
    [InlineData("""{"b":"{\"authorization\":[\"<scrubbed>hunter2\"]}"}""", """{"b":"{\"authorization\":[\"<scrubbed>\"]}"}""")]
    public void A_value_that_merely_begins_with_the_scrubbed_marker_is_scrubbed_too(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(text).ShouldNotBeEmpty();
    }

    /// <summary>A value holding an escaped quote is taken whole inside an escaped document too, at the depth the document sits at.</summary>
    [Theory]
    [InlineData("""{"b":"{\"password\":\"pa\\\"ss\",\"u\":1}"}""", """{"b":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    [InlineData("""{"b":"{\"authorization\":[\"Basic a\\\"b\",\"Basic c\"]}"}""", """{"b":"{\"authorization\":[\"<scrubbed>\",\"<scrubbed>\"]}"}""")]
    [InlineData(
        """{"a":"{\"b\":\"{\\\"password\\\":\\\"pa\\\\\\\"ss\\\",\\\"u\\\":1}\"}"}""",
        """{"a":"{\"b\":\"{\\\"password\\\":\\\"<scrubbed>\\\",\\\"u\\\":1}\"}"}""")]
    [InlineData("""{"b":"{\"password\":\"ab\\\\\",\"u\":1}"}""", """{"b":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    [InlineData("""{"b":"{\"password\":\"pa\\\"ss\\\\\",\"u\":1}"}""", """{"b":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    [InlineData("""{"b":"{\"authorization\":[\"Basic a\\\\\",\"Basic b\"]}"}""", """{"b":"{\"authorization\":[\"<scrubbed>\",\"<scrubbed>\"]}"}""")]
    [InlineData("""{"b":"{\"password\":\"a\\nb\",\"u\":1}"}""", """{"b":"{\"password\":\"<scrubbed>\",\"u\":1}"}""")]
    public void A_value_holding_an_escaped_quote_inside_an_escaped_document_is_scrubbed_whole(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Fact]
    public void A_single_authorization_value_does_not_take_the_next_pair_with_it()
    {
        FixtureScrubber.Scrub("""{"authorization":"Basic a","other":"b","list":["c"]}""")
            .ShouldBe("""{"authorization":"<scrubbed>","other":"b","list":["c"]}""");
    }

    [Theory]
    [InlineData(
        """{"a":"{\"b\":\"{\\\"password\\\":\\\"demo\\\",\\\"u\\\":1}\"}"}""",
        """{"a":"{\"b\":\"{\\\"password\\\":\\\"<scrubbed>\\\",\\\"u\\\":1}\"}"}""")]
    [InlineData(
        """{"a":"{\"b\":\"{\\\"password\\\":12345}\"}"}""",
        """{"a":"{\"b\":\"{\\\"password\\\":\\\"<scrubbed>\\\"}\"}"}""")]
    [InlineData(
        """{"a":"{\"b\":\"{\\\"authorization\\\":[\\\"Basic x\\\",\\\"Basic y\\\"]}\"}"}""",
        """{"a":"{\"b\":\"{\\\"authorization\\\":[\\\"<scrubbed>\\\",\\\"<scrubbed>\\\"]}\"}"}""")]
    public void A_document_escaped_inside_a_document_escaped_inside_a_document_is_read_too(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
        FixtureScrubber.Scrub(expected).ShouldBe(expected);
    }

    [Theory]
    [InlineData("password= abc", "password=<scrubbed>")]
    [InlineData("grant_type=password&password=\t s3&client_id=web-app", "grant_type=password&password=<scrubbed>&client_id=web-app")]
    [InlineData("Server=db;Pwd= hunter2;Encrypt=true", "Server=db;Pwd=<scrubbed>;Encrypt=true")]
    [InlineData("""{"body":"username=demo&password= abc","n":1}""", """{"body":"username=demo&password=<scrubbed>","n":1}""")]
    public void A_space_after_the_equals_sign_does_not_hide_the_value(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
        FixtureScrubber.Findings(expected).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("password= ")]
    [InlineData("password=  &x=1")]
    [InlineData("password= <scrubbed>")]
    [InlineData("""{"body":"password= "}""")]
    public void A_space_after_the_equals_sign_with_no_value_behind_it_is_left_as_it_is(string text)
    {
        FixtureScrubber.Scrub(text).ShouldBe(text);
        FixtureScrubber.Findings(text).ShouldBeEmpty();
    }

    /// <summary>
    /// The accepted cost of two guesses free text forces (#153): a bare value ends at a double quote, because in a JSON
    /// body that is where the string ends, and an unterminated quote takes the run only to <c>;</c>, <c>&amp;</c> or whitespace.
    /// </summary>
    [Theory]
    [InlineData("password=ab\"cd", "password=<scrubbed>\"cd")]
    [InlineData("password=\"abc def;x=1", "password=<scrubbed> def;x=1")]
    public void A_bare_value_holding_a_double_quote_and_an_unterminated_quote_keep_what_the_value_cannot_be_told_to_include(string text, string expected)
    {
        FixtureScrubber.Scrub(text).ShouldBe(expected);
    }

    [Fact]
    public void Findings_name_each_rule_the_text_still_breaks()
    {
        FixtureScrubber.Findings($$"""{"password":"demo","t":"{{Jwt}}"}""").ShouldBe(["a JWT", "a password or client secret"]);
    }
}
