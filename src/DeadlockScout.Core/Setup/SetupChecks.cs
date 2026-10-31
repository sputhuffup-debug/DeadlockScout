namespace DeadlockScout.Core.Setup;

/// <summary>How Deadlock is set to show itself, from its video config.</summary>
public enum DisplayMode
{
    /// <summary>No video config found or read: the game has not been run yet, or the file moved.</summary>
    Unknown,
    /// <summary>Fullscreen windowed (no border): the overlay works.</summary>
    Borderless,
    /// <summary>A window with a frame: the overlay works and follows it.</summary>
    Windowed,
    /// <summary>Exclusive fullscreen: the game hides every other window, the overlay included.</summary>
    Fullscreen,
}

public enum CheckState { Ok, Warning, Missing }

/// <summary>One first-run check: what it found, and what to do about it when it is not ok.</summary>
public sealed record SetupCheck(string Title, CheckState State, string Detail);

/// <summary>
/// The first-run wizard's checks, as plain decisions over what the app found on disk. The window shows them, offers the
/// fix it can make itself (adding -condebug, typing a path), and says what only the player can do (the game's display
/// mode, which the app never changes).
/// </summary>
public static class SetupChecks
{
    public static SetupCheck Steam(string? steamPath, string? deadlockPath, uint accountId) =>
        steamPath is null
            ? new("Steam", CheckState.Missing, "Steam was not found. Type its folder below (where steam.exe is).")
        : deadlockPath is null
            ? new("Steam", CheckState.Missing, $"Steam is at {steamPath}, but Deadlock is not in its libraries. Type the Deadlock folder below.")
        : accountId == 0
            ? new("Steam", CheckState.Warning, "Steam and Deadlock were found, but not your account: sign in to Steam once, or type your account id below.")
            : new("Steam", CheckState.Ok, $"Deadlock at {deadlockPath}, account {accountId}.");

    public static SetupCheck Condebug(bool hasCondebug, bool canCheck) =>
        hasCondebug
            ? new("Match detection", CheckState.Ok, "-condebug is set: the app sees each match start from the game's console log.")
        : canCheck
            ? new("Match detection", CheckState.Missing, "Add -condebug to Deadlock's launch options. The button below does it while Steam is closed; or Steam → Deadlock → Properties → Launch options.")
            : new("Match detection", CheckState.Warning, "Find Steam and your account first, then add -condebug.");

    /// <summary>
    /// What Windows says about the foreground program (<c>SHQueryUserNotificationState</c>, the state Windows itself uses
    /// to hold back notifications), reduced to what the display check needs. Asks Windows, never the game.
    /// </summary>
    public enum ShellDisplay
    {
        /// <summary>The call failed or was not made.</summary>
        Unknown,
        /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN: an exclusive-mode Direct3D program owns the screen.</summary>
        Exclusive,
        /// <summary>QUNS_BUSY or QUNS_APP: a program fills the screen, borderless or exclusive (Windows does not say which).</summary>
        FullScreenApp,
        /// <summary>Anything else: nothing fills the screen.</summary>
        Desktop,
    }

    /// <summary>The <c>QUERY_USER_NOTIFICATION_STATE</c> value as a <see cref="ShellDisplay"/>.</summary>
    public static ShellDisplay ShellDisplayFrom(int? state) => state switch
    {
        null => ShellDisplay.Unknown,
        3 => ShellDisplay.Exclusive,       // QUNS_RUNNING_D3D_FULL_SCREEN
        2 or 7 => ShellDisplay.FullScreenApp, // QUNS_BUSY, QUNS_APP
        _ => ShellDisplay.Desktop,
    };

    /// <summary>
    /// Deadlock's display mode as it is now. Deadlock no longer keeps <c>video.txt</c> up to date (a player's file was
    /// months old while the game ran borderless, Oct 31, 2026, and players report the same on the forums), so while the
    /// game is in front Windows decides: an exclusive-mode program means exclusive fullscreen; otherwise a window that
    /// fills the screen is borderless and a smaller one windowed. When Windows says only that a program fills the screen,
    /// a saved "Fullscreen" still counts, so the warning errs towards showing. UNVERIFIED: whether Deadlock's Vulkan
    /// renderer in exclusive fullscreen reports as Direct3D; if not, it reads as a full-screen program. With the game in
    /// the background, or no answer from Windows, the saved file is all there is.
    /// </summary>
    public static DisplayMode LiveDisplayMode(ShellDisplay shell, bool gameForeground, bool windowFillsScreen, DisplayMode saved)
    {
        if (!gameForeground || shell == ShellDisplay.Unknown) return saved;
        if (shell == ShellDisplay.Exclusive) return DisplayMode.Fullscreen;
        if (!windowFillsScreen) return DisplayMode.Windowed;
        return shell == ShellDisplay.FullScreenApp && saved == DisplayMode.Fullscreen ? DisplayMode.Fullscreen : DisplayMode.Borderless;
    }

    /// <summary>
    /// Whether the overlay should warn about exclusive fullscreen when the game window fills the screen. A borderless
    /// window fills the screen too, so the size alone cannot tell them apart: <see cref="LiveDisplayMode"/> decides, and
    /// only an unknown mode leaves the hint on.
    /// </summary>
    public static bool WarnFullscreen(DisplayMode mode, bool windowFillsScreen)
        => windowFillsScreen && mode is DisplayMode.Fullscreen or DisplayMode.Unknown;

    public static SetupCheck Display(DisplayMode mode) => mode switch
    {
        DisplayMode.Borderless => new("Display mode", CheckState.Ok, "Deadlock's saved settings say borderless: the overlay can draw over it. (Deadlock does not always keep that file current; while you play, the overlay asks Windows.)"),
        DisplayMode.Windowed => new("Display mode", CheckState.Ok, "Deadlock's saved settings say windowed: the overlay follows the window. Borderless (fullscreen windowed) fills the screen and works too."),
        DisplayMode.Fullscreen => new("Display mode", CheckState.Missing, "Deadlock's saved settings say exclusive fullscreen, which hides every other window. In the game: Settings → Video → Display mode → Fullscreen windowed (borderless)."),
        _ => new("Display mode", CheckState.Warning, "Deadlock's video settings were not found (run the game once). Set Display mode to Fullscreen windowed (borderless) in its video settings."),
    };

    public static SetupCheck Ocr(bool enabled, bool calibrated) =>
        !enabled
            ? new("OCR (optional)", CheckState.Ok, "Off: souls come from the broadcast, 30–60 s late. Turn it on for real-time souls and the exact clock.")
        : calibrated
            ? new("OCR (optional)", CheckState.Ok, "On, and your soul count and clock boxes are set.")
            : new("OCR (optional)", CheckState.Warning, "On, but the boxes are not drawn yet: calibrate with the game running.");
}
