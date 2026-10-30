using System.Diagnostics;
using System.IO;
using System.Windows;
using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.App.History;
using DeadlockScout.Core.History;
using DeadlockScout.App.Calibration;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.App.Diagnostics;
using DeadlockScout.App.Settings;
using DeadlockScout.App.Setup;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Cues;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Objectives;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using DeadlockScout.Steam;

namespace DeadlockScout.App;

/// <summary>Shows the same panels as the overlay without the game running, plus setup actions; detection details are on the diagnostics page.</summary>
public partial class MainWindow : Window
{
    private readonly OverlayState _state;
    private readonly OverlayWindow _overlay;
    private readonly JsonSettingsMonitor _settings;
    private readonly GamePaths _paths;
    private readonly AssetCatalog _assets;
    private readonly IOcrEngine _ocr;
    private readonly MatchSession _session;
    private readonly CompositeBenchmark _benchmark;
    private readonly BuildHintService _builds;
    private readonly AudioCuePlayer _audio;
    private SettingsWindow? _settingsWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private readonly DiagnosticsLog _diagnostics;
    private readonly OcrSoulSource _ocrSource;
    private readonly OcrJumpGuard _guard;
    private readonly CounterItemService _counters;
    private SetupWindow? _setupWindow;
    private SettingsViewModel? _settingsVm;
    private bool _previewDemo; // the preview loaded the demo lobby, so stopping the preview takes it down again
    private readonly GameWindowTracker _gameWindow = new();
    private CalibrationWindow? _calibration;
    private MatchHistoryWindow? _history;
    private readonly MatchHistoryService _historyService;
    private readonly ItemIcons _icons;
    private bool _exiting, _hiddenOnce;

    /// <summary>The Exit button; the composition root owns shutdown.</summary>
    public event Action? ExitRequested;
    /// <summary>The first time closing the window hides it to the tray instead of quitting.</summary>
    public event Action? FirstHiddenToTray;

    public MainWindow(OverlayState state, OverlayWindow overlay, JsonSettingsMonitor settings, GamePaths paths, AssetCatalog assets, IOcrEngine ocr,
                      MatchHistoryService historyService, ItemIcons icons, MatchSession session, CompositeBenchmark benchmark, BuildHintService builds,
                      AudioCuePlayer audio, DiagnosticsLog diagnostics, OcrSoulSource ocrSource, OcrJumpGuard guard, CounterItemService counters)
    {
        InitializeComponent();
        _counters = counters;
        _diagnostics = diagnostics; _ocrSource = ocrSource; _guard = guard;
        _state = state; _overlay = overlay; _settings = settings; _paths = paths; _assets = assets; _ocr = ocr;
        _historyService = historyService; _icons = icons; _session = session; _benchmark = benchmark; _builds = builds; _audio = audio;
        DataContext = state;
        overlay.PreviewChanged += on => Dispatcher.BeginInvoke(() =>
        {
            PreviewButton.Content = on ? "Stop preview" : "Preview overlay";
            // A demo the preview brought up goes with it, so its stats never linger; a match start has already ended it.
            if (!on && _previewDemo) { _previewDemo = false; EndDemo(); }
        });
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; Hide(); // the tray owns the lifetime
            if (!_hiddenOnce) { _hiddenOnce = true; FirstHiddenToTray?.Invoke(); }
        };
        _settings.OnChange((_, _) => Dispatcher.BeginInvoke(RefreshDetection)); // settings.json edited by hand or from the settings window
        RefreshDetection();
    }

    /// <summary>Lets the window close for real during shutdown.</summary>
    public void AllowClose() => _exiting = true;

    private void ExitApp(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private string GameWindowStatus()
    {
        var game = _gameWindow.Poll();
        return game.Found ? $"found ({_gameWindow.FoundIn}){(game.Foreground ? ", in front" : "")}" : "not running (the overlay shows only over the game)";
    }

    public void RefreshDetection()
    {
        var steam = _paths.SteamPath ?? "not found";
        var game = _paths.DeadlockPath ?? "not found";
        var log = _paths.ConsoleLogPath;
        string condebug = _paths.HasCondebug ? "yes" : "NO (match detection needs the -condebug launch option)";
        _state.Status.Detection = $"Steam: {steam}\nDeadlock: {game}\nconsole.log: {(log is not null && File.Exists(log) ? "present" : "missing")}\nGame window: {GameWindowStatus()}\n-condebug: {condebug}\nAccount: {_paths.AccountId}\nLive feed: {_settings.CurrentValue.Api.LiveSource switch { DeadlockScout.Core.Settings.LiveSource.SelfHosted => "self-hosted at " + _settings.CurrentValue.Api.LiveUrl, DeadlockScout.Core.Settings.LiveSource.Direct => "direct from Valve's broadcast", _ => "hosted (api.deadlock-api.com)" }}\nOCR regions: {OcrRegionSummary()}\nSettings: {_settings.Path}";
        if (_settings.LastError is { } err) _state.Status.Warning = err;
    }

    private string OcrRegionSummary()
    {
        int set = new RegionCalibration(_settings.CurrentValue.Ocr.Regions).Count;
        var regions = set == 0 ? "not calibrated" : $"{set} of {RegionCalibration.Slots.Count} set";
        var state = !_ocr.IsAvailable ? _ocr.Name : _settings.CurrentValue.Ocr.Enabled ? $"on, {_ocr.Name}" : "off";
        return $"{regions} · OCR {state}";
    }

    private void ToggleOverlay(object sender, RoutedEventArgs e) => _overlay.ToggleVisible();


    private void PreviewOverlay(object sender, RoutedEventArgs e) => TogglePreview();

    /// <summary>Turns the overlay preview on or off; public so the tray menu can too. Fills in the demo lobby when no match is live.</summary>
    public void TogglePreview()
    {
        bool on = !_overlay.Preview;
        if (on && !_state.InMatch && !_state.Pace.HasData) { LoadDemo(); _previewDemo = true; }
        _overlay.SetPreview(on);
    }

    /// <summary>Opens the match history (one window at a time); public so the tray menu can open it.</summary>
    public void OpenMatchHistory()
    {
        if (_history is not null) { _history.Activate(); return; }
        _history = new MatchHistoryWindow(_historyService, _assets, _icons, _paths.AccountId,
            (hero, tier, ct) => RankCurves.GetAsync(hero, RankGoal.Bracket(tier), ct), _settings.CurrentValue.Goal,
            include => _settings.Save(_settings.CurrentValue.WithLowRanks(include)),
            (hero, tier, ct) => _builds.GetItemTimingsAsync(hero, RankGoal.Bracket(tier), ct),
            (mine, enemy, ct) => _counters.GetAsync(mine, enemy, ct));
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }

    private void MatchHistory(object sender, RoutedEventArgs e) => OpenMatchHistory();

    /// <summary>Opens the OCR region tool over the game window (one at a time); public so the tray menu can open it.</summary>
    public void OpenCalibration()
    {
        if (_calibration is not null) { _calibration.Activate(); return; }
        _calibration = new CalibrationWindow(_settings, _gameWindow.ClientBounds, _ocr);
        _calibration.Closed += (_, _) => { _calibration = null; RefreshDetection(); };
        _calibration.Show();
    }

    private void OpenDiagnostics(object sender, RoutedEventArgs e) => OpenDiagnosticsWindow();

    /// <summary>The diagnostics page: setup, the last match, API calls; one window at a time.</summary>
    public void OpenDiagnosticsWindow()
    {
        if (_diagnosticsWindow is not null) { _diagnosticsWindow.Activate(); return; }
        _diagnosticsWindow = new DiagnosticsWindow(new DiagnosticsViewModel(_diagnostics, _state, _settings, () => _paths.AccountId, _ocrSource, _guard), RefreshDetection);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void OpenSettings(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    /// <summary>The settings window, one at a time; also from the tray.</summary>
    public void OpenSettingsWindow()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsVm ??= new SettingsViewModel(_settings);
        _settingsWindow = new SettingsWindow(_settingsVm, _audio, OpenCalibration, OpenSettingsFile);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>
    /// The provider for rank gate curves: the analytics buckets, which answer every hero of a rank from one cached set of
    /// foreground calls. Not the composite: its fallback samples 40 matches per hero as background work, which waits while
    /// a match is live and is far too slow for a drop-down.
    /// </summary>
    private IPaceBenchmark RankCurves => _benchmark.Providers.FirstOrDefault(p => p.Name == AnalyticsBucketBenchmark.ProviderName) ?? _benchmark;

    /// <summary>The first-run wizard, one at a time: at start-up until it is finished or skipped, and from the tray.</summary>
    public void OpenSetupWizard()
    {
        if (_setupWindow is not null) { _setupWindow.Activate(); return; }
        _setupWindow = new SetupWindow(new SetupViewModel(_paths, _settings), OpenCalibration, TogglePreview);
        _setupWindow.Closed += (_, _) => { _setupWindow = null; RefreshDetection(); };
        _setupWindow.Show();
    }

    /// <summary>settings.json in the default editor, for anything the settings window does not show.</summary>
    private void OpenSettingsFile()
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

    private void ShowDemo(object sender, RoutedEventArgs e)
    {
        if (_state.IsDemo) { _previewDemo = false; EndDemo(); }
        else LoadDemo();
    }

    /// <summary>Takes the demo lobby down; during a match the session then fills the panels again from the live feed.</summary>
    private void EndDemo()
    {
        bool wasDemo = _state.IsDemo;
        _state.EndDemo();
        if (wasDemo && _state.InMatch) _session.Resync();
        _state.Refresh();
    }

    /// <summary>
    /// Fills the panels with original demo content so the layout can be checked without a match. Player names are made
    /// up; heroes are real ids so names resolve from the assets endpoint ("You" plays Haze). During a match the session
    /// pauses so live data never mixes in, and hiding the demo resyncs it.
    /// </summary>
    public void LoadDemo()
    {
        if (_state.InMatch) _session.Pause();
        _state.BeginDemo();
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
        // Likely parties from made-up party histories: you and Quill; Ash, Brim and Cinder; Ember and Flint.
        SharedHistory Mate(uint id, int games) => new(id, games, games / 2, games - games / 2, false);
        _state.SetParties(PartyGroups.Detect(roster, new Dictionary<uint, IReadOnlyList<SharedHistory>>
        {
            [1000] = new[] { Mate(1003, 14) },
            [1006] = new[] { Mate(1007, 9), Mate(1008, 3) },
            [1010] = new[] { Mate(1011, 22) },
            [1004] = new[] { Mate(1009, 5) }, // partied before, but on the other team now: no tag
        }));
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
        // The timers at a laning-phase moment: medium camps and Midtown crates 25 s away, so their row asks you to push.
        _state.DemoClock = new TimeSpan(0, 4, 35);
        // The next-item nudge: Headhunter already bought, Fortitude next, which Oracle players have 24 s from now.
        const long hh = 4053935515, fort = 3585132399, lch = 865846625; // Headhunter, Fortitude, Leech
        _state.UsualBuild = new[]
        {
            new UsualItem(hh, 9, new TimeSpan(0, 13, 10)), new UsualItem(fort, 7, new TimeSpan(0, 16, 30)),
            new UsualItem(lch, 6, new TimeSpan(0, 23, 0)),
        };
        _state.RankItemTimings = new Dictionary<long, ItemTiming>
        {
            [hh] = new(hh, 900, 727), [fort] = new(fort, 700, 864), [lch] = new(lch, 500, 1274),
        };
        _state.NextItemRank = "Oracle";
        _state.MyItems.Observe(new[] { hh }, new TimeSpan(0, 12, 50));
        // ...and the objectives just before the demo mid boss's estimated return (18:20), so its countdown is gold.
        _state.DemoObjectiveClock = new TimeSpan(0, 17, 55);
        // The rank above the demo player's: a slower lane than the demo player's, a faster mid game, so the phase chips show
        // a good lane, a mid game behind and the catch-up hint.
        _state.GoalRank = "Oracle";
        _state.GoalCurve = curve with
        {
            Provider = "analytics", SampleSize = 900,
            Points = curve.Points.Select(p => new PacePoint(p.Minute, null, null, null,
                (int)(p.Minute <= 10 ? 568 * p.Minute : 5680 + 690 * (p.Minute - 10)))).ToList(),
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
        // Today so far, after a couple of losses: the nudge shows from three in a row by default.
        _state.SetSession(new SessionView(new SessionSummary(4, 3, -3, 73, 72, DateTimeOffset.Now.Date), SessionMath.Nudge(new SessionSummary(4, 3, -3, 73, 72, DateTimeOffset.Now.Date), 3),
            new[] { new HeroPoolRow(13, 214, 118, 0.512), new HeroPoolRow(1, 96, 45, 0.497), new HeroPoolRow(7, 51, 27, 0.505) }, new RankBracket(63, 83)), _assets, demo: true);
        // The previous match's report, as it appears once the API has processed a match.
        _state.SetLastMatch(new PostMatchReport(1, 13, true, DateTimeOffset.Now.AddMinutes(-52), new TimeSpan(0, 34, 12), 8, 3, 11, 31240, 2, 6,
            new[]
            {
                new PaceCheckpoint(10, 6150, 5800, 0.06, 64), new PaceCheckpoint(20, 12210, 11600, 0.053, 61),
                new PaceCheckpoint(30, 17050, 17400, -0.02, 47),
            },
            new LaneResult(10, 6150, 4910, new[] { 4 }), new RankBracket(65, 85), "metadata", 40,
            new[]
            {
                new PhaseResult(GamePhase.Laning, TimeSpan.Zero, TimeSpan.FromMinutes(10), 6150, 5800),
                new PhaseResult(GamePhase.Mid, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), 6060, 6650),
                new PhaseResult(GamePhase.Late, TimeSpan.FromMinutes(20), new TimeSpan(0, 34, 12), 19030, 17900),
            }, "Oracle"), _assets, demo: true);
        var me = players[0];
        foreach (var opponent in players.Where(p => p.Team != me.Team && p.Lane == me.Lane))
            _state.SetMatchup(opponent.AccountId, new HeroMatchup(me.HeroId, opponent.HeroId, 2572, 1368));
        // Recent form for the lane opponent (Cinder on Lady Geist): made-up games, a hot streak above their usual pace.
        if (players.FirstOrDefault(p => p.Name == "Cinder") is { } laneOpponent)
            _state.SetForm(laneOpponent.AccountId, new RecentForm(10, 7, 3, 846, 776));
        // Counter items on three enemies: real item ids so names resolve; the lifts and game counts are made up. The rest of
        // the enemy team has no item clear enough, as most matchups do.
        const long inhibitor = 2037039379, frenzy = 339443430, healbane = 2603935618;
        var counterItems = new[] { (Name: "Brim", Item: inhibitor, Lift: 0.013, Games: 44180), (Name: "Ember", Item: frenzy, Lift: 0.033, Games: 5737),
                                   (Name: "Cinder", Item: healbane, Lift: 0.021, Games: 9120) };
        foreach (var (name, item, lift, games) in counterItems)
            if (players.FirstOrDefault(p => p.Name == name) is { } enemy)
                _state.SetCounter(enemy.AccountId, new CounterItem(item, enemy.HeroId, lift, 4.0, games));
        _state.Pace.Provider = "demo"; _state.Pace.SampleSize = 40; _state.Pace.Bracket = "all";
        // About 6% ahead of pace, swinging ±12% with fights and farm, so the sparkline has a shape.
        for (int s = 0; s <= 860; s += 30) _state.Meter.Add(new SoulSample(TimeSpan.FromSeconds(s), (int)(s * 10.2 + 70 * Math.Sin(s / 360.0 * 2 * Math.PI)), SampleSource.LiveFeed));
        // Objectives: the urn appeared at 10:00 and is still out; the enemy took the mid boss at 11:20 and its Rejuvenator.
        foreach (var o in new[]
                 {
                     new ObjectiveSample(TimeSpan.FromMinutes(0), true, 0, 0, false), new ObjectiveSample(TimeSpan.FromMinutes(10), true, 0, 0, true),
                     new ObjectiveSample(new TimeSpan(0, 11, 20), false, 0, 0, true), new ObjectiveSample(new TimeSpan(0, 11, 24), false, 0, 1, true),
                 })
            _state.ObjectiveTracker.Observe(o);
        // Power spikes: real item ids so names resolve; who holds what is made up. The first enemy's Leech showed up ten
        // seconds ago, so their row flashes; the others carry a quiet chip for an earlier spike, one of each colour
        // (weapon, ultimate, spirit).
        const long leech = 865846625, headhunter = 4053935515, mysticReverb = 3577481646;
        var spikes = new HashSet<long> { leech, headhunter, mysticReverb, ItemSpikes.UltimateId };
        var held = new[] { Array.Empty<long>(), new long[] { headhunter }, Array.Empty<long>(), new long[] { mysticReverb } };
        int n = 0;
        foreach (var p in players.Where(p => p.Team == Teams.Sapphire))
        {
            // Their souls over the match from the broadcast, for the sparkline's enemy lines: a steady rate that lands on
            // the net worth below at 14:00, wobbling with fights; your lane opponent Cinder starts ahead and falls back.
            int final = 8000 + (int)p.AccountId % 7 * 300;
            for (int s = 0; s <= 810; s += 30)
            {
                double lead = p.Name == "Cinder" ? 450 * Math.Sin(Math.Min(s, 600) / 600.0 * Math.PI) : 0;
                int souls = (int)(final * s / 840.0 + lead + 60 * Math.Sin((s + p.AccountId * 50) / 240.0 * 2 * Math.PI));
                _state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(s), p.AccountId, Math.Max(0, souls), 0, 0, 0), p, _assets);
            }
            _state.EnemyCurves[p.AccountId] = curve;
            _state.EnemySpikes[p.AccountId] = spikes;
            int nw = 8000 + (int)p.AccountId % 7 * 300;
            var items = n < held.Length ? held[n] : Array.Empty<long>();
            _state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(840), p.AccountId, nw, 2, 1, 3, items, n == 2), p, _assets);
            if (n == 0) items = items.Append(leech).ToArray();
            _state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(850), p.AccountId, nw, 2, 1, 3, items, n == 2), p, _assets);
            n++;
        }
        _state.Refresh();
    }
}
