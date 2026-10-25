using Shouldly;

namespace Admin.Mcp.Tests;

/// <summary>Where the MCP process reaches the console: given, never guessed, and loopback or nothing.</summary>
public sealed class ConsoleUrlTests
{
    [Theory]
    [InlineData("http://localhost:5100", "http://localhost:5100/")]
    [InlineData("http://127.0.0.1:5100/", "http://127.0.0.1:5100/")]
    [InlineData("http://[::1]:5100", "http://[::1]:5100/")]
    public void A_loopback_address_is_taken_with_a_trailing_slash(string given, string expected)
    {
        ConsoleUrl.From(["--url", given], null).AbsoluteUri.ShouldBe(expected);
    }

    [Fact]
    public void The_argument_wins_over_the_environment_variable_and_the_variable_is_the_fallback()
    {
        ConsoleUrl.From(["--url", "http://localhost:1"], "http://localhost:2").Port.ShouldBe(1);
        ConsoleUrl.From([], "http://localhost:2").Port.ShouldBe(2);
    }

    [Theory]
    [InlineData("http://example.com:5100")]
    [InlineData("http://10.0.0.5:5100")]
    [InlineData("https://localhost:5100")]
    [InlineData("localhost:5100")]
    [InlineData("not a url")]
    public void An_address_that_is_not_http_loopback_is_refused(string given)
    {
        Should.Throw<ArgumentException>(() => ConsoleUrl.From(["--url", given], null));
    }

    [Fact]
    public void No_address_is_refused_with_the_way_to_give_one()
    {
        ArgumentException refused = Should.Throw<ArgumentException>(() => ConsoleUrl.From([], null));

        refused.Message.ShouldContain("--url");
        refused.Message.ShouldContain(ConsoleUrl.Variable);
        Should.Throw<ArgumentException>(() => ConsoleUrl.From(["--url"], null));
    }
}
