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
    /// Whether the overlay should warn about exclusive fullscreen when the game window fills the screen. A borderless
    /// window fills the screen too, so the size alone cannot tell them apart: <c>video.txt</c> decides, and only an
    /// unreadable file leaves the hint on.
    /// </summary>
    public static bool WarnFullscreen(DisplayMode mode, bool windowFillsScreen)
        => windowFillsScreen && mode is DisplayMode.Fullscreen or DisplayMode.Unknown;

    public static SetupCheck Display(DisplayMode mode) => mode switch
    {
        DisplayMode.Borderless => new("Display mode", CheckState.Ok, "Deadlock runs borderless: the overlay can draw over it."),
        DisplayMode.Windowed => new("Display mode", CheckState.Ok, "Deadlock runs in a window: the overlay follows it. Borderless (fullscreen windowed) fills the screen and works too."),
        DisplayMode.Fullscreen => new("Display mode", CheckState.Missing, "Deadlock is in exclusive fullscreen, which hides every other window. In the game: Settings → Video → Display mode → Fullscreen windowed (borderless)."),
        _ => new("Display mode", CheckState.Warning, "Deadlock's video settings were not found (run the game once). Set Display mode to Fullscreen windowed (borderless) in its video settings."),
    };

    public static SetupCheck Ocr(bool enabled, bool calibrated) =>
        !enabled
            ? new("OCR (optional)", CheckState.Ok, "Off: souls come from the broadcast, 30–60 s late. Turn it on for real-time souls and the exact clock.")
        : calibrated
            ? new("OCR (optional)", CheckState.Ok, "On, and your soul count and clock boxes are set.")
            : new("OCR (optional)", CheckState.Warning, "On, but the boxes are not drawn yet: calibrate with the game running.");
}
