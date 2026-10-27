using System.Diagnostics;
using DeadlockScout.Steam.Vdf;

namespace DeadlockScout.Steam;

/// <summary>Reads and (only while Steam is closed) edits Deadlock's launch options so -condebug is present.</summary>
public static class LaunchOptions
{
    public const string Condebug = "-condebug";

    public static string LocalConfigPath(string steamPath, uint accountId)
        => Path.Combine(steamPath, "userdata", accountId.ToString(), "config", "localconfig.vdf");

    private static readonly string[] AppPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", SteamLocator.DeadlockAppId.ToString() };

    public static string? ReadLaunchOptions(VdfNode localConfig)
        => localConfig.Find(AppPath)?.GetString("LaunchOptions");

    public static bool HasCondebug(string? launchOptions)
        => launchOptions is not null && launchOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t, Condebug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Appends -condebug, preserving the rest, and returns the new value. Pure; nothing is written.</summary>
    public static string WithCondebug(string? launchOptions)
        => HasCondebug(launchOptions) ? launchOptions! : string.IsNullOrWhiteSpace(launchOptions) ? Condebug : $"{launchOptions.Trim()} {Condebug}";

    public static bool IsSteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return true; } // when in doubt, do not touch the file
    }

    public enum Result { AlreadySet, Written, SteamRunning, FileMissing }

    /// <summary>
    /// Adds -condebug to localconfig.vdf. Refuses while Steam runs (Steam would overwrite it and may corrupt state).
    /// Writes a .bak copy first.
    /// </summary>
    public static Result EnsureCondebug(string steamPath, uint accountId)
    {
        var path = LocalConfigPath(steamPath, accountId);
        if (!File.Exists(path)) return Result.FileMissing;
        var root = VdfParser.ParseFile(path);
        var current = ReadLaunchOptions(root);
        if (HasCondebug(current)) return Result.AlreadySet;
        if (IsSteamRunning()) return Result.SteamRunning;

        var node = root.GetOrAddObject(AppPath[0]);
        for (int i = 1; i < AppPath.Length; i++) node = node.GetOrAddObject(AppPath[i]);
        node.Set("LaunchOptions", WithCondebug(current));

        File.Copy(path, path + ".deadlockscout.bak", overwrite: true);
        File.WriteAllText(path, root.Serialize());
        return Result.Written;
    }
}
