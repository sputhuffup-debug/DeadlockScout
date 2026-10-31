using DeadlockScout.Core.Setup;

namespace DeadlockScout.Tests.Core;

public class SetupChecksTests
{
    [Fact]
    public void Steam_check_names_the_first_thing_missing()
    {
        Assert.Equal(CheckState.Missing, SetupChecks.Steam(null, null, 0).State);
        Assert.Contains("Deadlock is not in its libraries", SetupChecks.Steam("C:/Steam", null, 0).Detail);
        Assert.Equal(CheckState.Warning, SetupChecks.Steam("C:/Steam", "D:/Deadlock", 0).State);
        Assert.Equal(CheckState.Ok, SetupChecks.Steam("C:/Steam", "D:/Deadlock", 1000).State);
    }

    [Fact]
    public void Display_mode_is_only_a_problem_in_exclusive_fullscreen()
    {
        Assert.Equal(CheckState.Ok, SetupChecks.Display(DisplayMode.Borderless).State);
        Assert.Equal(CheckState.Ok, SetupChecks.Display(DisplayMode.Windowed).State);
        Assert.Equal(CheckState.Missing, SetupChecks.Display(DisplayMode.Fullscreen).State);
        Assert.Equal(CheckState.Warning, SetupChecks.Display(DisplayMode.Unknown).State);
    }

    [Fact]
    public void Condebug_and_ocr_checks()
    {
        Assert.Equal(CheckState.Ok, SetupChecks.Condebug(true, true).State);
        Assert.Equal(CheckState.Missing, SetupChecks.Condebug(false, true).State);
        Assert.Equal(CheckState.Warning, SetupChecks.Condebug(false, false).State);
        Assert.Equal(CheckState.Ok, SetupChecks.Ocr(false, false).State);       // optional: off is fine
        Assert.Equal(CheckState.Warning, SetupChecks.Ocr(true, false).State);
    }

    [Fact]
    public void The_fullscreen_warning_shows_only_when_video_txt_does_not_say_borderless_or_windowed()
    {
        // A borderless window fills the screen exactly like exclusive fullscreen, so the size alone is not enough.
        Assert.False(SetupChecks.WarnFullscreen(DisplayMode.Borderless, windowFillsScreen: true));
        Assert.False(SetupChecks.WarnFullscreen(DisplayMode.Windowed, windowFillsScreen: true));
        Assert.True(SetupChecks.WarnFullscreen(DisplayMode.Fullscreen, windowFillsScreen: true));
        Assert.True(SetupChecks.WarnFullscreen(DisplayMode.Unknown, windowFillsScreen: true)); // no file: keep the hint
        Assert.False(SetupChecks.WarnFullscreen(DisplayMode.Fullscreen, windowFillsScreen: false));
    }
}
