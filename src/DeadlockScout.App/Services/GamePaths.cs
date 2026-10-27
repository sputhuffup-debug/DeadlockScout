using System.IO;
using DeadlockScout.Core.Settings;
using DeadlockScout.Steam;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>Resolves Steam, Deadlock and account from settings or discovery, re-checking on each call so a late install is found.</summary>
public sealed class GamePaths
{
    private readonly SteamLocator _locator;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public GamePaths(SteamLocator locator, IOptionsMonitor<ScoutSettings> settings)
    {
        _locator = locator;
        _settings = settings;
    }

    public string? SteamPath => _locator.FindSteamPath(_settings.CurrentValue.SteamPath);
    public string? DeadlockPath => SteamPath is { } s ? _locator.FindDeadlockPath(s, _settings.CurrentValue.DeadlockPath) : null;
    public string? ConsoleLogPath => DeadlockPath is { } d ? SteamLocator.ConsoleLogPath(d) : null;
    public string? ContentLogPath => SteamPath is { } s ? SteamLocator.ContentLogPath(s) : null;
    public uint AccountId => SteamPath is { } s ? _locator.FindAccountId(s, _settings.CurrentValue.AccountId) ?? 0 : _settings.CurrentValue.AccountId ?? 0;

    public bool HasCondebug
    {
        get
        {
            var steam = SteamPath; var acc = AccountId;
            if (steam is null || acc == 0) return false;
            var path = LaunchOptions.LocalConfigPath(steam, acc);
            if (!File.Exists(path)) return false;
            try { return LaunchOptions.HasCondebug(LaunchOptions.ReadLaunchOptions(Steam.Vdf.VdfParser.ParseFile(path))); }
            catch { return false; }
        }
    }
}
