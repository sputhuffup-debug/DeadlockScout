using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Windows.Threading;
using DeadlockScout.Api.Benchmarks;
using DeadlockScout.App.Calibration;
using DeadlockScout.App.Diagnostics;
using DeadlockScout.App.History;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Settings;
using DeadlockScout.App.Setup;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.History;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using DeadlockScout.Steam;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// What the tray, the hotkeys and the main window's buttons do, kept out of the main window so that window can be
/// released when it is closed to the tray and built again when it is opened: a hidden window kept its whole visual
/// tree (the lobby table, the cards, the sparklines) alive and laid out through every match, which is most of what put
/// the match of Oct 31 over the memory budget. Owns the other windows (one of each at a time), the demo lobby, and the
/// detection lines Diagnostics shows. Lives on the UI thread.
/// </summary>
public sealed class AppShell
{
    private readonly OverlayState _state;
    private readonly OverlayWindow _overlay;
    private readonly JsonSettingsMonitor _settings;
    private readonly GamePaths _paths;
    private readonly AssetCatalog _assets;
    private readonly IOcrEngine _ocr;
    private readonly GlyphStore _glyphStore;
    private readonly MatchSession _session;
    private readonly CompositeBenchmark _benchmark;
    private readonly BuildHintService _builds;
    private readonly AudioCuePlayer _audio;
    private readonly DiagnosticsLog _diagnostics;
    private readonly OcrSoulSource _ocrSource;
    private readonly OcrJumpGuard _guard;
    private readonly CounterItemService _counters;
    private readonly SoulSourceService _sources;
    private readonly MatchHistoryService _historyService;
    private readonly SaltIngest _ingest;
    private readonly UpdateService _updates;
    private readonly ItemIcons _icons;
    private readonly ILogger<AppShell> _log;
    private readonly Dispatcher _dispatcher;
    private readonly GameWindowTracker _gameWindow = new();
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private MainWindow? _main;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsVm;
    private DiagnosticsWindow? _diagnosticsWindow;
    private SetupWindow? _setupWindow;
    private CalibrationWindow? _calibration;
    private MatchHistoryWindow? _history;
    private bool _previewDemo; // the preview loaded the demo lobby, so stopping the preview takes it down again
    private bool _exiting, _hiddenOnce;
    private long _releasedFrom;

    /// <summary>The Exit button; the composition root owns shutdown.</summary>
    public event Action? ExitRequested;
    /// <summary>The first time closing the main window sends it to the tray instead of quitting.</summary>
    public event Action? FirstHiddenToTray;
    /// <summary>The overlay preview went on (true) or off, on the UI thread.</summary>
    public event Action<bool>? PreviewChanged;

    public AppShell(OverlayState state, OverlayWindow overlay, JsonSettingsMonitor settings, GamePaths paths, AssetCatalog assets, IOcrEngine ocr,
                    MatchHistoryService historyService, ItemIcons icons, MatchSession session, CompositeBenchmark benchmark, BuildHintService builds,
                    AudioCuePlayer audio, DiagnosticsLog diagnostics, OcrSoulSource ocrSource, OcrJumpGuard guard, CounterItemService counters,
                    SoulSourceService sources, SaltIngest ingest, UpdateService updates, GlyphStore glyphStore, ILogger<AppShell> log, MarkStore marks)
    {
        _marks = marks;
        _state = state; _overlay = overlay; _settings = settings; _paths = paths; _assets = assets; _ocr = ocr;
        _historyService = historyService; _icons = icons; _session = session; _benchmark = benchmark; _builds = builds; _audio = audio;
        _diagnostics = diagnostics; _ocrSource = ocrSource; _guard = guard; _counters = counters; _sources = sources;
        _ingest = ingest; _updates = updates; _log = log; _glyphStore = glyphStore;
        _dispatcher = Dispatcher.CurrentDispatcher;
        overlay.PreviewChanged += on => _dispatcher.BeginInvoke(() =>
        {
            // A demo the preview brought up goes with it, so its stats never linger; a match start has already ended it.
            if (!on && _previewDemo) { _previewDemo = false; EndDemo(); }
            PreviewChanged?.Invoke(on);
        });
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); Trim(); };
        _settings.OnChange((_, _) => _dispatcher.BeginInvoke(() => { RefreshDetection(); _state.RefreshNotes(); })); // settings.json edited by hand or from the settings window
        RefreshDetection();
    }

    /// <summary>Whether the overlay preview is on, for the main window's button.</summary>
    public bool PreviewOn => _overlay.Preview;

    // ---- the main window ----

    /// <summary>Opens the main window, building it again if it was released to the tray.</summary>
    public void ShowMain()
    {
        if (_exiting) return;
        _releaseTimer.Stop();
        if (_main is null)
        {
            _main = new MainWindow(this, _state);
            _main.Closed += (_, _) => MainClosed();
        }
        if (_main.WindowState == System.Windows.WindowState.Minimized) _main.WindowState = System.Windows.WindowState.Normal;
        _main.Show();
        _main.Activate();
        RefreshDetection();
    }

    private void MainClosed()
    {
        _main = null;
        if (_exiting) return;
        if (!_hiddenOnce) { _hiddenOnce = true; FirstHiddenToTray?.Invoke(); }
        using (var self = Process.GetCurrentProcess()) _releasedFrom = self.WorkingSet64;
        _releaseTimer.Start(); // once the window's last frame and its HWND are gone
    }

    /// <summary>
    /// A closed window's visuals are garbage only until the next collection, which an idle tray app may not run for a
    /// long while: collect once (compacting, the large object heap too) and log what the release gave back.
    /// </summary>
    private void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        _log.LogInformation("Main window closed to the tray: memory {Before} MB -> {After} MB (.NET heap {Heap} MB)",
            _releasedFrom / (1024 * 1024), self.WorkingSet64 / (1024 * 1024), GC.GetTotalMemory(false) / (1024 * 1024));
    }

    /// <summary>Shutdown: the windows close for real and nothing is rebuilt.</summary>
    public void CloseAll()
    {
        _exiting = true;
        _releaseTimer.Stop();
        foreach (var w in new System.Windows.Window?[] { _main, _settingsWindow, _diagnosticsWindow, _setupWindow, _calibration, _history })
            try { w?.Close(); } catch { }
    }

    /// <summary>The Exit button.</summary>
    public void Exit() => ExitRequested?.Invoke();

    // ---- detection lines (Diagnostics) ----

    private string GameWindowStatus()
    {
        var game = _gameWindow.Poll();
        return game.Found ? $"found ({_gameWindow.FoundIn}){(game.Foreground ? ", in front" : "")}" : "not running (the overlay shows only over the game)";
    }

    /// <summary>
    /// "248 MB (private 210 MB, .NET heap 41 MB) · main window open · OCR engine not loaded": this process right now, so
    /// a player can see what a window, OCR or a match costs by switching it on and off with Diagnostics open (the 1.0
    /// budget is 400 MB).
    /// </summary>
    private string MemoryNow()
    {
        using var self = Process.GetCurrentProcess();
        static string Mb(long bytes) => $"{bytes / (1024 * 1024)} MB";
        string main = _main is null ? " · main window released" : " · main window open";
        string ocr = WindowsOcr is WindowsMediaOcrEngine w ? (w.Loaded ? " · OCR engine loaded" : " · OCR engine not loaded") : "";
        return $"{Mb(self.WorkingSet64)} (private {Mb(self.PrivateMemorySize64)}, .NET heap {Mb(GC.GetTotalMemory(false))}){main}{ocr}";
    }

    /// <summary>"0.1.4 (installed) · update check 14:02: up to date": what runs, and what the updater last found.</summary>
    private string VersionLine()
    {
        string where = UpdateService.IsInstalled ? "installed" : "run from a build folder, so no updates";
        string check = !UpdateService.IsInstalled ? ""
            : _updates.LastCheck is { } c ? $" · update check {c.At.ToLocalTime():HH:mm}: {c.Result}" : " · no update check yet";
        return $"{UpdateService.Running.ToString(3)} ({where}){check}";
    }

    public void RefreshDetection()
    {
        var steam = _paths.SteamPath ?? "not found";
        var game = _paths.DeadlockPath ?? "not found";
        var log = _paths.ConsoleLogPath;
        string condebug = _paths.HasCondebug ? "yes" : "NO (match detection needs the -condebug launch option)";
        _state.Status.Detection = $"Version: {VersionLine()}\nSteam: {steam}\nDeadlock: {game}\nconsole.log: {(log is not null && File.Exists(log) ? "present" : "missing")}\nGame window: {GameWindowStatus()}\nDisplay mode: {(_state.DisplaySeen is { Length: > 0 } seen ? seen : "not seen yet (Windows tells once the game is in front)")}\n-condebug: {condebug}\nAccount: {_paths.AccountId}\nLive feed: {_settings.CurrentValue.Api.LiveSource switch { LiveSource.SelfHosted => "self-hosted at " + _settings.CurrentValue.Api.LiveUrl, LiveSource.Direct => "direct from Valve's broadcast", _ => "hosted (api.deadlock-api.com)" }}\nOCR regions: {OcrRegionSummary()}\nMemory now: {MemoryNow()}\nProcess hardening: {Interop.ProcessHardening.Status}\nMatch ingest: {SaltIngest.Summary(_ingest.LastRun)}\nSettings: {_settings.Path}";
        if (_settings.LastError is { } err) _state.Status.Warning = err;
    }

    /// <summary>The Windows OCR engine, behind the glyph reader when it is in front.</summary>
    private IOcrEngine WindowsOcr => _ocr is GlyphOcrEngine g ? g.Fallback : _ocr;

    private string OcrRegionSummary()
    {
        int set = new RegionCalibration(_settings.CurrentValue.Ocr.Regions).Count;
        var regions = set == 0 ? "not calibrated" : $"{set} of {RegionCalibration.Slots.Count} set";
        var state = !_ocr.IsAvailable ? _ocr.Name : _settings.CurrentValue.Ocr.Enabled ? $"on, {_ocr.Name}" : "off";
        string glyphs = _ocr is GlyphOcrEngine g ? $"\nGlyph reader: {g.Status}" : "";
        return $"{regions} · OCR {state}{glyphs}";
    }

    // ---- the tray's and the main window's actions ----

    public void ToggleOverlay() => _overlay.ToggleVisible();

    /// <summary>Turns the overlay preview on or off. Fills in the demo lobby when no match is live.</summary>
    private readonly MarkStore _marks;

    /// <summary>
    /// The mark hotkey: in a match you play (not spectate) with a known match id and a game clock, saves the game time
    /// and says "Marked 14:20" in the pace strip; anything else does nothing.
    /// </summary>
    public void MarkMoment()
    {
        if (!_state.InMatch || _state.CurrentMatchId <= 0) return;
        if (_state.Clock.Read(DateTimeOffset.UtcNow, _state.AssumedDelay)?.GameTime is not { } at) return;
        if (_marks.Add(_state.CurrentMatchId, at)) _state.ShowMark(at);
    }

    public void TogglePreview()
    {
        bool on = !_overlay.Preview;
        if (on && !_state.InMatch && !_state.Pace.HasData) { LoadDemo(); _previewDemo = true; }
        _overlay.SetPreview(on);
    }

    /// <summary>Opens the match history (one window at a time).</summary>
    public void OpenMatchHistory()
    {
        if (_history is not null) { _history.Activate(); return; }
        _history = new MatchHistoryWindow(_historyService, _assets, _icons, _paths.AccountId,
            (hero, tier, ct) => RankCurves.GetAsync(hero, RankGoal.Bracket(tier), ct), _settings.CurrentValue.Goal,
            include => _settings.Save(_settings.CurrentValue.WithLowRanks(include)),
            (hero, tier, ct) => _builds.GetItemTimingsAsync(hero, RankGoal.Bracket(tier), ct),
            (mine, enemy, ct) => _counters.GetAsync(mine, enemy, ct),
            (hero, tier, ct) => _sources.GetAsync(hero, RankGoal.Bracket(tier), ct),
            _settings.CurrentValue.Targets, targets => _settings.Save(_settings.CurrentValue.WithTargets(targets)),
            async ct => SaltIngest.Describe(await _ingest.RunNowAsync(ct)),
            () => _state.Friends,
            _marks.For,
            _settings.CurrentValue.MatchupNotes, (hero, text) => _settings.Save(_settings.CurrentValue.WithMatchupNote(hero, text)));
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }

    /// <summary>Opens the OCR region tool over the game window (one at a time).</summary>
    public void OpenCalibration()
    {
        if (_calibration is not null) { _calibration.Activate(); return; }
        // The tool reads with Windows OCR itself, so the glyphs it learns are labelled by Windows OCR, never by themselves.
        var glyphs = _ocr as GlyphOcrEngine;
        _calibration = new CalibrationWindow(_settings, _gameWindow.ClientBounds, WindowsOcr, glyphs?.Glyphs, _glyphStore.Save);
        _calibration.Closed += (_, _) => { _calibration = null; RefreshDetection(); };
        _calibration.Show();
    }

    /// <summary>The diagnostics page: setup, the last match, API calls; one window at a time.</summary>
    public void OpenDiagnosticsWindow()
    {
        if (_diagnosticsWindow is not null) { _diagnosticsWindow.Activate(); return; }
        _diagnosticsWindow = new DiagnosticsWindow(new DiagnosticsViewModel(_diagnostics, _state, _settings, () => _paths.AccountId, _ocrSource, _guard), RefreshDetection);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    /// <summary>The settings window, one at a time.</summary>
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

    public void AddCondebug()
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

    // ---- the demo lobby ----

    /// <summary>The main window's demo button: shows the demo lobby, or takes it down.</summary>
    public void ToggleDemo()
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
    /// Fills the panels with the demo lobby (<see cref="DemoLobby"/>). During a match the session pauses so live data
    /// never mixes in, and hiding the demo resyncs it.
    /// </summary>
    public void LoadDemo()
    {
        if (_state.InMatch) _session.Pause();
        _state.BeginDemo();
        DemoLobby.Fill(_state, _assets);
    }
}
