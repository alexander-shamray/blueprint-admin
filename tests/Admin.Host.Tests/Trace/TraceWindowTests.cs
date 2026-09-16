using Admin.Host.Trace;
using Shouldly;

namespace Admin.Host.Tests.Trace;

public sealed class TraceWindowTests
{
    [Theory]
    [InlineData("15m", 900)]
    [InlineData("2h", 7200)]
    [InlineData("90s", 90)]
    [InlineData("24h", 86400)]
    [InlineData("1m", 60)]
    public void A_window_is_a_whole_number_and_a_unit(string text, int seconds)
    {
        TraceWindow.TryParse(text, out TimeSpan window, out string? error).ShouldBeTrue();

        window.ShouldBe(TimeSpan.FromSeconds(seconds));
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_window_at_all_is_the_fifteen_minute_default(string? text)
    {
        TraceWindow.TryParse(text, out TimeSpan window, out string? error).ShouldBeTrue();

        window.ShouldBe(TimeSpan.FromMinutes(15));
        TraceWindow.Format(window).ShouldBe(TraceWindow.Default);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("30s")]
    [InlineData("48h")]
    [InlineData("banana")]
    [InlineData("-5m")]
    [InlineData("15")]
    [InlineData("1.5h")]
    [InlineData("m")]
    [InlineData("15 m")]
    public void Anything_else_is_refused_with_an_error_naming_the_accepted_form(string text)
    {
        TraceWindow.TryParse(text, out TimeSpan window, out string? error).ShouldBeFalse();

        window.ShouldBe(TimeSpan.Zero);
        error.ShouldNotBeNull();
        error.ShouldContain("15m");
        error.ShouldContain("24h");
    }

    [Theory]
    [InlineData(900, "15m")]
    [InlineData(7200, "2h")]
    [InlineData(90, "90s")]
    [InlineData(86400, "24h")]
    public void A_parsed_window_formats_back_to_the_text_it_came_from(int seconds, string expected) =>
        TraceWindow.Format(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Fact]
    public void The_bounds_are_one_minute_to_one_day()
    {
        TraceWindow.Min.ShouldBe(TimeSpan.FromMinutes(1));
        TraceWindow.Max.ShouldBe(TimeSpan.FromHours(24));
    }
}
