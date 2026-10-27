using System.Diagnostics;
using System.IO;
using System.Windows;
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Steam;

namespace DeadlockScout.App;

/// <summary>Shows the same panels as the overlay without the game running, plus detection status and setup actions.</summary>
public partial class MainWindow : Window
{
    private readonly OverlayState _state;
    private readonly OverlayWindow _overlay;
    private readonly JsonSettingsMonitor _settings;
    private readonly GamePaths _paths;
    private readonly AssetCatalog _assets;
    private bool _syncingLiveSource;

    public MainWindow(OverlayState state, OverlayWindow overlay, JsonSettingsMonitor settings, GamePaths paths, AssetCatalog assets)
    {
        InitializeComponent();
        _state = state; _overlay = overlay; _settings = settings; _paths = paths; _assets = assets;
        DataContext = state;
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // the tray owns the lifetime
        _settings.OnChange((_, _) => Dispatcher.BeginInvoke(() => { SyncLiveSource(); RefreshDetection(); })); // settings.json edited by hand
        SyncLiveSource();
        RefreshDetection();
    }

    /// <summary>Shows the configured live source on the Direct/Hosted switch. SelfHosted is only set in settings.json and checks neither.</summary>
    private void SyncLiveSource()
    {
        var source = _settings.CurrentValue.Api.LiveSource;
        _syncingLiveSource = true;
        try
        {
            DirectSource.IsChecked = source == LiveSource.Direct;
            HostedSource.IsChecked = source == LiveSource.Hosted;
        }
        finally { _syncingLiveSource = false; }
        LiveSourceNote.Text = source == LiveSource.SelfHosted
            ? $"self-hosted at {_settings.CurrentValue.Api.LiveUrl} (from settings.json)"
            : "applies from the next match";
    }

    private void LiveSourceChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingLiveSource) return;
        var chosen = ReferenceEquals(sender, DirectSource) ? LiveSource.Direct : LiveSource.Hosted;
        var current = _settings.CurrentValue;
        if (current.Api.LiveSource == chosen) return;
        try { _settings.Save(current.WithLiveSource(chosen)); }
        catch (Exception ex) { _state.Status.Warning = $"Could not save settings.json: {ex.Message}"; }
        SyncLiveSource();
        RefreshDetection();
    }

    public void RefreshDetection()
    {
        var steam = _paths.SteamPath ?? "not found";
        var game = _paths.DeadlockPath ?? "not found";
        var log = _paths.ConsoleLogPath;
        string condebug = _paths.HasCondebug ? "yes" : "NO (match detection needs the -condebug launch option)";
        _state.Status.Detection = $"Steam: {steam}\nDeadlock: {game}\nconsole.log: {(log is not null && File.Exists(log) ? "present" : "missing")}\n-condebug: {condebug}\nAccount: {_paths.AccountId}\nLive feed: {_settings.CurrentValue.Api.LiveSource switch { DeadlockScout.Core.Settings.LiveSource.SelfHosted => "self-hosted at " + _settings.CurrentValue.Api.LiveUrl, DeadlockScout.Core.Settings.LiveSource.Direct => "direct from Valve's broadcast", _ => "hosted (api.deadlock-api.com)" }}\nSettings: {_settings.Path}";
        if (_settings.LastError is { } err) _state.Status.Warning = err;
    }

    private void ToggleOverlay(object sender, RoutedEventArgs e) => _overlay.ToggleVisible();

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_settings.Path) { UseShellExecute = true }); } catch { }
    }

    private void AddCondebug(object sender, RoutedEventArgs e)
    {
        var steam = _paths.SteamPath; var acc = _paths.AccountId;
        if (steam is null || acc == 0) { _state.Status.Warning = "Steam or account not found; set steamPath/accountId in settings.json."; return; }
        var result = LaunchOptions.EnsureCondebug(steam, acc);
        _state.Status.Warning = result switch
        {
            LaunchOptions.Result.AlreadySet => "-condebug is already set.",
            LaunchOptions.Result.Written => "-condebug added. Start Steam and Deadlock again.",
            LaunchOptions.Result.SteamRunning => "Close Steam first, or add -condebug yourself: Steam → Deadlock → Properties → Launch options.",
            _ => "localconfig.vdf not found; add -condebug in Steam → Deadlock → Properties → Launch options.",
        };
        RefreshDetection();
    }

    private void ShowDemo(object sender, RoutedEventArgs e) => LoadDemo();

    /// <summary>
    /// Fills the panels with original demo content so the layout can be checked without a match. Player names are made
    /// up; heroes are real ids so names resolve from the assets endpoint ("You" plays Haze).
    /// </summary>
    public void LoadDemo()
    {
        _state.Reset();
        _state.Status.HeroName = "Haze";
        var players = new List<RosterPlayer>();
        string[] names = { "You", "Vex", "Moth", "Quill", "Rook", "Sable", "Ash", "Brim", "Cinder", "Drift", "Ember", "Flint" };
        int[] heroes = { 13, 1, 6, 7, 11, 19, 2, 3, 4, 10, 12, 14 };
        for (int i = 0; i < 12; i++)
            players.Add(new RosterPlayer((uint)(1000 + i), names[i], i < 6 ? Teams.Amber : Teams.Sapphire, heroes[i], 60 + (i * 3) % 20, i % 4 + 1));
        var roster = new Roster(1, players, TimeSpan.FromSeconds(90));
        var rows = players.Select((p, i) => new ScoutRow(p, 5 + i * 7, 0.45 + i * 0.02, 120 + i * 30, p.Rank, i == 7 ? 2 : null, i == 9 ? "Smurf?" : i == 2 ? "new on hero" : null)
        {
            Flags = i == 9 ? ScoutFlags.SuspectedSmurf : i == 2 ? ScoutFlags.NewOnHero : ScoutFlags.None,
            MetRecord = i == 7 ? (1, 1) : null,
        }).ToList();
        _state.SetRoster(roster, rows, 1000, _assets);
        _state.ScoutVisibleUntil = DateTimeOffset.UtcNow.AddSeconds(90);
        var curve = new PaceCurve(1, RankBracket.All, "demo",
            Enumerable.Range(0, 9).Select(m => new PacePoint(m * 5, m * 5 * 540, m * 5 * 580, m * 5 * 620, m * 5 * 580)).ToList(), 40, "demo");
        _state.Curve = curve;
        // Your own last 12 games on the hero, a little behind the population at this rank.
        _state.PersonalCurve = curve with
        {
            Provider = "personal", SampleSize = 12,
            Points = curve.Points.Select(p => p with { Mean = (int)(p.Mean * 0.97) }).ToList(),
        };
        // Haze's build tables: real item and ability ids so names resolve from the assets; the counts are made up.
        const long fixation = 1080948381, sleepDagger = 2948410412, smokeBomb = 2414191464, bulletDance = 731943444;
        long[] order = { fixation, fixation, sleepDagger, smokeBomb, fixation, sleepDagger, bulletDance, sleepDagger, sleepDagger, fixation, bulletDance };
        double[] times = { 13, 60, 105, 199, 254, 315, 365, 478, 790, 905, 1130 };
        _state.SetBuild(new BuildTables(
            new[]
            {
                new ItemBucketStat(2739107182, 8000, 10843, 5800, 1054), new ItemBucketStat(3585132399, 8000, 7605, 3950, 1003),
                new ItemBucketStat(1292979587, 8000, 7446, 3700, 1006), new ItemBucketStat(2480592370, 8000, 5293, 2600, 1037),
            },
            new[] { new AbilityOrderStat(order, times, 5501, 3110) }));
        var me = players[0];
        foreach (var opponent in players.Where(p => p.Team != me.Team && p.Lane == me.Lane))
            _state.SetMatchup(opponent.AccountId, new HeroMatchup(me.HeroId, opponent.HeroId, 2572, 1368));
        _state.Pace.Provider = "demo"; _state.Pace.SampleSize = 40; _state.Pace.Bracket = "all";
        for (int s = 0; s <= 860; s += 60) _state.Meter.Add(new SoulSample(TimeSpan.FromSeconds(s), (int)(s * 10.2), SampleSource.LiveFeed));
        foreach (var p in players.Where(p => p.Team == Teams.Sapphire))
        {
            _state.EnemyCurves[p.AccountId] = curve;
            _state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(860), p.AccountId, 8000 + (int)p.AccountId % 7 * 300, 2, 1, 3), p, _assets);
        }
        _state.Refresh();
    }
}
