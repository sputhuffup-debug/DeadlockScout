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

    [Theory]
    [InlineData(null, SetupChecks.ShellDisplay.Unknown)]
    [InlineData(3, SetupChecks.ShellDisplay.Exclusive)]     // QUNS_RUNNING_D3D_FULL_SCREEN
    [InlineData(2, SetupChecks.ShellDisplay.FullScreenApp)] // QUNS_BUSY
    [InlineData(7, SetupChecks.ShellDisplay.FullScreenApp)] // QUNS_APP
    [InlineData(5, SetupChecks.ShellDisplay.Desktop)]       // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(1, SetupChecks.ShellDisplay.Desktop)]       // QUNS_NOT_PRESENT
    public void Windows_notification_state_maps_to_what_the_display_check_needs(int? state, SetupChecks.ShellDisplay expected)
        => Assert.Equal(expected, SetupChecks.ShellDisplayFrom(state));

    [Fact]
    public void With_the_game_in_front_Windows_decides_over_a_stale_video_file()
    {
        // The player's case: video.txt months old and saying borderless.
        Assert.Equal(DisplayMode.Fullscreen,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.Exclusive, gameForeground: true, windowFillsScreen: true, DisplayMode.Borderless));
        // And the other way round: an old "fullscreen" file while Windows sees nothing exclusive and no full-screen program.
        Assert.Equal(DisplayMode.Borderless,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.Desktop, true, true, DisplayMode.Fullscreen));
        Assert.Equal(DisplayMode.Windowed,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.FullScreenApp, true, windowFillsScreen: false, DisplayMode.Fullscreen));
        // Windows only says a program fills the screen: a saved "fullscreen" keeps the warning, anything else is borderless.
        Assert.Equal(DisplayMode.Fullscreen,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.FullScreenApp, true, true, DisplayMode.Fullscreen));
        Assert.Equal(DisplayMode.Borderless,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.FullScreenApp, true, true, DisplayMode.Unknown));
        // The game behind another window, or no answer from Windows: the saved file is all there is.
        Assert.Equal(DisplayMode.Borderless,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.Exclusive, gameForeground: false, true, DisplayMode.Borderless));
        Assert.Equal(DisplayMode.Unknown,
            SetupChecks.LiveDisplayMode(SetupChecks.ShellDisplay.Unknown, true, true, DisplayMode.Unknown));
    }
}
