using System.Runtime.InteropServices;
using DeadlockScout.Steam.Vdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Steam;

/// <summary>Where Steam and Deadlock live and who is logged in. Everything here is a plain file or registry read.</summary>
public sealed class SteamLocator
{
    /// <summary>Deadlock's Steam app id.</summary>
    public const int DeadlockAppId = 1422450;

    private readonly ILogger _log;

    public SteamLocator(ILogger<SteamLocator>? log = null) => _log = log ?? NullLogger<SteamLocator>.Instance;

    /// <summary>Steam install folder from HKCU\Software\Valve\Steam\SteamPath, else the usual default; null when not found.</summary>
    public string? FindSteamPath(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var fromRegistry = ReadRegistrySteamPath();
                if (fromRegistry is not null && Directory.Exists(fromRegistry)) return Path.GetFullPath(fromRegistry);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Registry read failed"); }

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            })
                if (Directory.Exists(candidate)) return candidate;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var candidate in new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") })
                if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistrySteamPath()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }

    /// <summary>Finds the Deadlock install folder via libraryfolders.vdf; null when the app is not installed.</summary>
    public string? FindDeadlockPath(string steamPath, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        foreach (var library in EnumerateLibraryFolders(steamPath))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{DeadlockAppId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var acf = VdfParser.ParseFile(manifest);
                var installDir = acf.GetString("AppState", "installdir") ?? "Deadlock";
                var path = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(path)) return path;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Bad manifest {Manifest}", manifest); }
        }
        var fallback = Path.Combine(steamPath, "steamapps", "common", "Deadlock");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public IEnumerable<string> EnumerateLibraryFolders(string steamPath)
    {
        yield return steamPath;
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        VdfNode root;
        try { root = VdfParser.ParseFile(vdf); }
        catch (Exception ex) { _log.LogDebug(ex, "Bad libraryfolders.vdf"); yield break; }
        var folders = root["libraryfolders"] ?? root["LibraryFolders"];
        if (folders is null) yield break;
        foreach (var kv in folders.Children)
        {
            var path = kv.Value.IsObject ? kv.Value.GetString("path") : kv.Value.Value;
            if (!string.IsNullOrWhiteSpace(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(steamPath), StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    /// <summary>Path of the game's console log; exists only with the -condebug launch option.</summary>
    public static string ConsoleLogPath(string deadlockPath) => Path.Combine(deadlockPath, "game", "citadel", "console.log");

    public static string ContentLogPath(string steamPath) => Path.Combine(steamPath, "logs", "content_log.txt");

    public static string LoginUsersPath(string steamPath) => Path.Combine(steamPath, "config", "loginusers.vdf");

    public static string HttpCachePath(string steamPath) => Path.Combine(steamPath, "appcache", "httpcache");

    /// <summary>The most recently used account from loginusers.vdf, else the newest userdata folder; null when unknown.</summary>
    public uint? FindAccountId(string steamPath, uint? configured = null)
    {
        if (configured is > 0) return configured;
        var loginUsers = LoginUsersPath(steamPath);
        if (File.Exists(loginUsers))
        {
            try
            {
                var id = AccountFromLoginUsers(VdfParser.ParseFile(loginUsers));
                if (id is not null) return id;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Bad loginusers.vdf"); }
        }
        var userdata = Path.Combine(steamPath, "userdata");
        if (Directory.Exists(userdata))
        {
            var newest = new DirectoryInfo(userdata).GetDirectories()
                .Where(d => uint.TryParse(d.Name, out var n) && n > 0)
                .OrderByDescending(d => File.Exists(Path.Combine(d.FullName, "config", "localconfig.vdf"))
                    ? File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "config", "localconfig.vdf"))
                    : d.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null && uint.TryParse(newest.Name, out var acc)) return acc;
        }
        return null;
    }

    /// <summary>Picks MostRecent=1, else the latest Timestamp. Pure, for tests.</summary>
    public static uint? AccountFromLoginUsers(VdfNode root)
    {
        var users = root["users"];
        if (users is null) return null;
        (uint id, long ts, bool recent)? best = null;
        foreach (var kv in users.Children)
        {
            if (!ulong.TryParse(kv.Key, out var steam64)) continue;
            bool recent = kv.Value.GetString("MostRecent") == "1";
            long.TryParse(kv.Value.GetString("Timestamp"), out var ts);
            var acc = SteamIds.ToAccountId(steam64);
            if (best is null || (recent && !best.Value.recent) || (recent == best.Value.recent && ts > best.Value.ts))
                best = (acc, ts, recent);
        }
        return best?.id;
    }
}
