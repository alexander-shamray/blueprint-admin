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

    [Fact]
    public void Scrubbing_twice_changes_nothing_and_what_was_scrubbed_has_no_findings()
    {
        string once = FixtureScrubber.Scrub($$"""{"access_token":"{{Jwt}}","password":"demo","h":"Bearer 0123456789abcdef0123"}""");

        FixtureScrubber.Scrub(once).ShouldBe(once);
        FixtureScrubber.Findings(once).ShouldBeEmpty();
    }

    [Fact]
    public void Findings_name_each_rule_the_text_still_breaks()
    {
        FixtureScrubber.Findings($$"""{"password":"demo","t":"{{Jwt}}"}""").ShouldBe(["a JWT", "a password or client secret"]);
    }
}
