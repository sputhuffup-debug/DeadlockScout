using DeadlockScout.Core.Setup;
using DeadlockScout.Steam.Vdf;

namespace DeadlockScout.Steam;

/// <summary>
/// Reads Deadlock's display mode from the video config it writes on purpose, <c>game\citadel\cfg\video.txt</c> (a KeyValues
/// file). <c>setting.fullscreen</c> "1" is exclusive fullscreen (confirmed by players' forum posts on editing the file).
/// With it "0", <c>setting.nowindowborder</c> "1" is fullscreen windowed (borderless) and "0" a framed window:
/// UNVERIFIED for Deadlock, taken from other Source 2 games' video configs; replace from a real file.
/// </summary>
public static class VideoConfig
{
    public static string PathFor(string deadlockPath) => Path.Combine(deadlockPath, "game", "citadel", "cfg", "video.txt");

    public static DisplayMode Read(VdfNode root)
    {
        // The settings sit in the file's one top-level object ("video.cfg" or similar); tolerate them at the top too.
        var settings = root.Children.FirstOrDefault(c => c.Value.IsObject).Value ?? root;
        string? Get(string key) => settings[key]?.Value ?? root[key]?.Value;
        var fullscreen = Get("setting.fullscreen");
        if (fullscreen is null) return DisplayMode.Unknown;
        if (fullscreen.Trim() == "1") return DisplayMode.Fullscreen;
        return Get("setting.nowindowborder")?.Trim() == "1" ? DisplayMode.Borderless : DisplayMode.Windowed;
    }

    /// <summary>The display mode from the file in <paramref name="deadlockPath"/>; Unknown when it is missing or unreadable.</summary>
    public static DisplayMode ReadFile(string? deadlockPath)
    {
        if (deadlockPath is null) return DisplayMode.Unknown;
        var path = PathFor(deadlockPath);
        try { return File.Exists(path) ? Read(VdfParser.ParseFile(path)) : DisplayMode.Unknown; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return DisplayMode.Unknown; }
    }
}
