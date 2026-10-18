using Admin.Host.Fakes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Admin.Host.Tests.Fakes;

public sealed class FakeStackTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_load_holds_the_strip_until_the_window_has_passed()
    {
        FakeStack stack = new(time);

        stack.Load();
        time.Advance(FakeStack.LoadWindow - TimeSpan.FromSeconds(1));
        stack.Loaded.ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(1));
        stack.Loaded.ShouldBeFalse();
    }

    // #261: a Playwright rerun on a reused host met the last run's load; the load-run test ends on a Down for that.
    [Fact]
    public void A_down_ends_the_load_inside_the_window()
    {
        FakeStack stack = new(time);

        stack.Load();
        stack.Unload();

        stack.Loaded.ShouldBeFalse();
    }
}
