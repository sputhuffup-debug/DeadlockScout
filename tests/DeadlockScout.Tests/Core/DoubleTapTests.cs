using DeadlockScout.Core.Input;

namespace DeadlockScout.Tests.Core;

public class DoubleTapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 19, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Ms(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Two_quick_taps_toggle()
    {
        var tap = new DoubleTap(TimeSpan.FromMilliseconds(350));
        Assert.False(tap.Press(Ms(0)));
        Assert.True(tap.Press(Ms(200)));
    }

    [Fact]
    public void A_slow_second_tap_starts_a_new_pair()
    {
        var tap = new DoubleTap(TimeSpan.FromMilliseconds(350));
        Assert.False(tap.Press(Ms(0)));
        Assert.False(tap.Press(Ms(500)));   // too slow: this is a new first tap
        Assert.True(tap.Press(Ms(700)));    // and this its second
    }

    [Fact]
    public void Three_quick_taps_toggle_once_and_four_twice()
    {
        var tap = new DoubleTap(TimeSpan.FromMilliseconds(350));
        var results = new[] { 0, 150, 300, 450 }.Select(ms => tap.Press(Ms(ms))).ToArray();
        Assert.Equal(new[] { false, true, false, true }, results);
    }

    [Fact]
    public void Reset_forgets_a_half_made_pair()
    {
        var tap = new DoubleTap(TimeSpan.FromMilliseconds(350));
        tap.Press(Ms(0));
        tap.Reset();                         // the game lost focus between the taps
        Assert.False(tap.Press(Ms(100)));
    }

    [Fact]
    public void A_clock_going_backwards_is_not_a_double_tap()
    {
        var tap = new DoubleTap(TimeSpan.FromMilliseconds(350));
        tap.Press(Ms(500));
        Assert.False(tap.Press(Ms(400)));
    }
}
