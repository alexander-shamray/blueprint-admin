using Admin.Host.Fakes;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

/// <summary>Which screen FakePlatform's assistant reads a message as naming.</summary>
public sealed class FakeAssistantScriptsTests
{
    [Theory]
    [InlineData("открой журнал команд", "transcript")]
    [InlineData("открой журнал", "logs")]
    [InlineData("show the transcript", "transcript")]
    [InlineData("open the logs", "logs")]
    [InlineData("what is the weather", null)]
    public void A_screen_whose_words_hold_another_screens_word_is_still_named_by_its_own(string text, string? screen) =>
        FakeAssistantScripts.ScreenNamedIn(text).ShouldBe(screen);
}
