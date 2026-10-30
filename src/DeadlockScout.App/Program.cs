using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeadlockScout.Api;
using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Api.Caching;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Live;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.History;
using DeadlockScout.App.History;
using DeadlockScout.App.Calibration;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Detection;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Sources;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using DeadlockScout.Steam;
using DeadlockScout.Steam.Logs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App;

/// <summary>Composition root: wires the adapters to Core's seams and starts the WPF application on the host.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--screenshots") return RenderScreenshots(args[1]);

        int code;
        using (var mutex = new Mutex(true, "DeadlockScout.SingleInstance", out bool first))
        {
            if (!first) return 0;
            code = RunApp(args);
        }
        // Everything is stopped and disposed by now; make sure no stray foreground thread keeps the process (and the
        // terminal that started it) alive.
        Environment.Exit(code);
        return code;
    }

    private static int RunApp(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Information).AddDebug())
            .ConfigureServices(services => ConfigureServices(services))
            .Build();

        var app = new App();
        app.InitializeComponent();
        var state = host.Services.GetRequiredService<OverlayState>();
        var overlay = host.Services.GetRequiredService<OverlayWindow>();
        var main = host.Services.GetRequiredService<MainWindow>();
        TrayIcon? tray = null;
        bool exiting = false;
        void StopServices()
        {
            // On the thread pool with a deadline: the UI thread waits here, so nothing may need it to finish.
            try
            {
                Task.Run(async () =>
                {
                    try { await host.Services.GetRequiredService<IMatchDetector>().StopAsync(); } catch { }
                    try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
        void Exit()
        {
            if (exiting) return;
            exiting = true;
            main.AllowClose();
            tray?.Dispose();
            StopServices();
            app.Shutdown();
        }
        tray = new TrayIcon(() => { main.Show(); main.Activate(); main.RefreshDetection(); }, overlay.ToggleVisible, overlay.ToggleScout, main.TogglePreview, main.OpenMatchHistory, main.OpenCalibration, Exit,
            host.Services.GetRequiredService<JsonSettingsMonitor>().CurrentValue.Overlay.HotkeyLayout, main.OpenSettingsWindow, main.OpenSetupWizard, main.OpenDiagnosticsWindow);
        main.ExitRequested += Exit;
        main.FirstHiddenToTray += () => tray.Notify("Still running", "DeadlockScout is in the tray. Use Exit in the window or the tray menu to quit.");
        // Ctrl+C in the terminal stops the host; take the whole app down with it.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => app.Dispatcher.BeginInvoke(Exit));
        host.Services.GetRequiredService<PostMatchCoordinator>().ReportReady += _ =>
            app.Dispatcher.BeginInvoke(() => tray.Notify("Match report", state.LastMatch.Title));

        app.Startup += async (_, _) =>
        {
            await host.StartAsync();
            overlay.Show(); // hidden again by its own timer until the game is in front
            main.Show();
            try { await host.Services.GetRequiredService<AssetCatalog>().LoadAsync(CancellationToken.None); } catch { }
            host.Services.GetRequiredService<MatchSession>(); // subscribes to the detector
            host.Services.GetRequiredService<AudioCuePlayer>(); // subscribes to the overlay's cues
            await host.Services.GetRequiredService<IMatchDetector>().StartAsync(CancellationToken.None);
            main.RefreshDetection();
            if (!host.Services.GetRequiredService<JsonSettingsMonitor>().CurrentValue.Launch.SetupDone)
                main.OpenSetupWizard(); // first run: walk through Steam, -condebug, display mode and OCR
            else if (!host.Services.GetRequiredService<GamePaths>().HasCondebug)
                state.Status.Warning = "Match detection needs the -condebug launch option. Use 'Add -condebug' (with Steam closed) or add it in Steam → Deadlock → Properties.";
        };
        var code = app.Run();
        if (!exiting) // Windows logoff ends the dispatcher without going through Exit
        {
            exiting = true;
            tray.Dispose();
            StopServices();
        }
        try { host.Dispose(); } catch { } // releases the console lifetime, which otherwise holds up process exit
        return code;
    }

    /// <summary>
    /// Renders the README screenshots from the demo lobby into <paramref name="folder"/> and exits: the main window and
    /// the overlay over a plain backdrop. Starts no detector and no live feed; the only network call is the hero names.
    /// Run by .github/workflows/screenshots.yml on a Windows runner.
    /// </summary>
    private static int RenderScreenshots(string folder)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; // CI runners have no GPU
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services => ConfigureServices(services))
            .Build();
        int exit = 1;
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(2), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        var app = new App();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    try { await host.Services.GetRequiredService<AssetCatalog>().LoadAsync(cts.Token); } catch { }
                host.Services.GetRequiredService<ItemIcons>().Enabled = false; // shots never show game art: chips keep their placeholder

                var main = host.Services.GetRequiredService<MainWindow>();
                main.LoadDemo();
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Left = 0; main.Top = 0;
                main.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // The whole window as it shows on screen: the title bar and the scroll bar. Before the stacked shot, which lays
                // the body out at its full height.
                ScreenshotRenderer.Render((FrameworkElement)VisualTreeHelper.GetChild(main, 0), main.Background, Path.Combine(folder, "window-frame.png"));
                var content = (FrameworkElement)main.Content;
                ScreenshotRenderer.RenderStacked(new[] { main.Header, main.Body }, main.Background, Path.Combine(folder, "main-window.png"), content.Margin);

                var backdrop = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x33, 0x3D, 0x47), System.Windows.Media.Color.FromRgb(0x0C, 0x0F, 0x12), 90);
                // The match history with demo games, the first opened on its timelines.
                var history = new MatchHistoryWindow(null, host.Services.GetRequiredService<AssetCatalog>(), host.Services.GetRequiredService<ItemIcons>(), 0,
                    (hero, tier, _) => Task.FromResult<PaceCurve?>(MatchHistoryWindow.DemoGoalCurve(hero, tier)),
                    itemTimings: (hero, tier, _) => Task.FromResult(MatchHistoryWindow.DemoItemTimings(hero, tier)),
                    counterItems: (mine, enemy, _) => Task.FromResult(MatchHistoryWindow.DemoCounters(mine, enemy)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
                };
                history.Show();
                history.ShowPage(MatchHistoryWindow.DemoPage(DateTimeOffset.Now));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ScreenshotRenderer.RenderStacked(new[] { history.HeaderPart, history.BodyPart }, history.Background, Path.Combine(folder, "match-history.png"),
                    ((FrameworkElement)history.Content).Margin);
                history.Close();

                // The setup wizard on its Steam step: a fresh runner has no Steam, so its checks show what a new player sees.
                var setup = new DeadlockScout.App.Setup.SetupWindow(
                    new DeadlockScout.App.Setup.SetupViewModel(host.Services.GetRequiredService<GamePaths>(), host.Services.GetRequiredService<JsonSettingsMonitor>()),
                    () => { }, () => { })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
                };
                setup.Show();
                setup.ShowStep(1);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ScreenshotRenderer.Render((FrameworkElement)VisualTreeHelper.GetChild(setup, 0), setup.Background, Path.Combine(folder, "setup.png"));
                setup.Close();

                // The settings window with the defaults, every section in one tall image.
                var settingsWindow = new DeadlockScout.App.Settings.SettingsWindow(
                    new DeadlockScout.App.Settings.SettingsViewModel(host.Services.GetRequiredService<JsonSettingsMonitor>()), null, () => { }, () => { })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
                };
                settingsWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ScreenshotRenderer.RenderStacked(new[] { settingsWindow.BodyPart }, settingsWindow.Background, Path.Combine(folder, "settings.png"), new Thickness(14));
                settingsWindow.Close();

                // The diagnostics page with a made-up last match and API log; the setup lines are the runner's own.
                var diagnosticsWindow = new DeadlockScout.App.Diagnostics.DiagnosticsWindow(new DeadlockScout.App.Diagnostics.DiagnosticsViewModel(
                    DeadlockScout.App.Diagnostics.DiagnosticsWindow.DemoLog(DateTimeOffset.UtcNow), host.Services.GetRequiredService<OverlayState>(),
                    host.Services.GetRequiredService<JsonSettingsMonitor>(), () => 0, liveOverride: () => DeadlockScout.App.Diagnostics.DiagnosticsWindow.DemoLive))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0,
                };
                diagnosticsWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ScreenshotRenderer.RenderStacked(new[] { diagnosticsWindow.BodyPart }, diagnosticsWindow.Background, Path.Combine(folder, "diagnostics.png"), new Thickness(16));
                diagnosticsWindow.Close();

                host.Services.GetRequiredService<OverlayWindow>().RenderPreview(Path.Combine(folder, "overlay.png"), backdrop);
                host.Services.GetRequiredService<OverlayWindow>().RenderSpikeCloseUp(Path.Combine(folder, "enemy-spikes.png"), backdrop);
                host.Services.GetRequiredService<OverlayWindow>().RenderEditorPreview(Path.Combine(folder, "overlay-editor.png"), backdrop);
                host.Services.GetRequiredService<OverlayWindow>().RenderCompactPace(Path.Combine(folder, "pace-compact.png"), backdrop);

                // The OCR region tool with example boxes (the real ones are drawn over the game).
                new CalibrationWindow(host.Services.GetRequiredService<JsonSettingsMonitor>(), () => null, new NullOcrEngine()).RenderPreview(Path.Combine(folder, "ocr-calibration.png"), backdrop, new[]
                {
                    (RegionCalibration.TimerKey, new HudRegion(0.482, 0.012, 0.036, 0.03)),
                    (RegionCalibration.EnemyKey(1), new HudRegion(0.62, 0.30, 0.05, 0.028)),
                    (RegionCalibration.EnemyKey(2), new HudRegion(0.62, 0.345, 0.05, 0.028)),
                    (RegionCalibration.SoulsKey, new HudRegion(0.47, 0.925, 0.06, 0.035)),
                });
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(exit); }
        };
        app.Run();
        return exit;
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        var settings = new JsonSettingsMonitor(JsonSettingsMonitor.DefaultPath());
        services.AddSingleton(settings);
        services.AddSingleton<IOptionsMonitor<ScoutSettings>>(settings);
        services.AddSingleton<IClock>(SystemClock.Instance);

        // Steam / local
        services.AddSingleton<SteamLocator>();
        services.AddSingleton<GamePaths>();
        services.AddSingleton<IMatchSignalSource>(sp => new ConsoleLogTailer(() => sp.GetRequiredService<GamePaths>().ConsoleLogPath, sp.GetRequiredService<IClock>(), sp.GetService<ILogger<ConsoleLogTailer>>()));
        services.AddSingleton<IMatchSignalSource>(sp => new SteamContentLogWatcher(() => sp.GetRequiredService<GamePaths>().ContentLogPath, sp.GetRequiredService<IClock>()));
        services.AddSingleton<IMatchDetector>(sp => new CompositeMatchDetector(sp.GetServices<IMatchSignalSource>(), sp.GetRequiredService<IClock>(),
            () => sp.GetRequiredService<GamePaths>().AccountId, log: sp.GetService<ILogger<CompositeMatchDetector>>()));

        // Cache + API
        services.AddSingleton(_ => new SqliteCacheStore(SqliteCacheStore.DefaultPath()));
        services.AddSingleton<LayeredCache>();
        services.AddSingleton<ICache>(sp => sp.GetRequiredService<LayeredCache>());
        services.AddSingleton<MatchActivityGate>();
        services.AddSingleton<DiagnosticsLog>();
        services.AddTransient<DiagnosticsHandler>();
        services.AddTransient<ApiKeyHandler>();
        services.AddTransient<RetryHandler>();
        services.AddSingleton<RateLimiterRegistry>();
        services.AddTransient(sp => new RateLimitedHandler(sp.GetRequiredService<RateLimiterRegistry>(), sp.GetRequiredService<MatchActivityGate>().WaitAsync,
            sp.GetService<ILogger<RateLimitedHandler>>(), sp.GetRequiredService<DiagnosticsLog>()));
        // Diagnostics innermost: it sees every attempt on the wire, retries included.
        services.AddHttpClient<IDeadlockApi, DeadlockApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RetryHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>()
            .AddHttpMessageHandler<DiagnosticsHandler>();
        // Live feed: hosted live query on api.deadlock-api.com (default) or the self-hosted live-events container.
        // Both streams stay open for the whole match, so no client timeout; the hosted one goes through the API key and rate limiter.
        services.AddHttpClient<HostedLiveQueryStream>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>()
            .AddHttpMessageHandler<DiagnosticsHandler>();
        services.AddHttpClient<SseLiveEventStream>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<BroadcastUrlResolver>();
        services.AddSingleton<DirectBroadcastStream>();
        services.AddSingleton<ILiveEventStream>(sp => new LiveEventStreamSelector(
            sp.GetRequiredService<HostedLiveQueryStream>(), sp.GetRequiredService<SseLiveEventStream>(), sp.GetRequiredService<DirectBroadcastStream>(),
            sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<LiveEventStreamSelector>>(), sp.GetRequiredService<DiagnosticsLog>()));
        services.AddSingleton<ILiveMatchFeed, LiveMatchFeed>();
        services.AddSingleton<IScoutingService, ScoutingService>();
        services.AddSingleton<IPatchKeyProvider>(sp => new ClientVersionPatchKeyProvider(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            new DatePatchKeyProvider(sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.PatchKey)));
        services.AddSingleton(sp => new AssetCatalog(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            TimeSpan.FromHours(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.AssetsTtlHours)));
        services.AddSingleton<ItemIcons>();
        services.AddSingleton(sp => new MatchHistoryService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            sp.GetRequiredService<IClock>(), sp.GetService<ILogger<MatchHistoryService>>()));

        // Benchmarks: population providers in the composite; the personal one is a separate view (M4)
        services.AddSingleton<IPaceBenchmark, AnalyticsBucketBenchmark>();
        services.AddSingleton<IPaceBenchmark, MetadataSampleBenchmark>();
        services.AddSingleton(sp => new CompositeBenchmark(sp.GetServices<IPaceBenchmark>(), sp.GetService<ILogger<CompositeBenchmark>>()));
        services.AddSingleton(sp => new PersonalBenchmark(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), () => sp.GetRequiredService<GamePaths>().AccountId));
        // Between matches: warm every provider's curves (population and personal) for your recent heroes.
        services.AddSingleton(sp => new BenchmarkWarmer(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            sp.GetServices<IPaceBenchmark>().Append(sp.GetRequiredService<PersonalBenchmark>()), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(),
            sp.GetRequiredService<MatchupService>(), sp.GetRequiredService<BuildHintService>(), sp.GetService<ILogger<BenchmarkWarmer>>()));
        services.AddHostedService<BenchmarkPrefetchService>();
        // Build hint: item purchases by net worth and ability orders, per hero, bracket and patch.
        services.AddSingleton(sp => new BuildHintService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays), sp.GetService<ILogger<BuildHintService>>()));
        // After each match: replay salts to deadlock-api, then the post-match report once the API has the match.
        services.AddSingleton(sp => new PostMatchService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<CompositeBenchmark>(),
            sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<PostMatchService>>()));
        services.AddSingleton(sp => new SaltIngest(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            since => sp.GetRequiredService<GamePaths>().SteamPath is { } steam ? new SteamHttpCache(steam).ScanSalts(since) : Array.Empty<MatchSalts>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<SaltIngest>>()));
        // Session view: today's results, rank change, loss-streak nudge, hero pool against the meta.
        services.AddSingleton(sp => new SessionService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), log: sp.GetService<ILogger<SessionService>>()));
        services.AddHostedService<SessionRefreshService>();
        services.AddSingleton<PostMatchCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<PostMatchCoordinator>());
        // Lane matchups: one hero-counter-stats table per bracket and patch.
        services.AddSingleton<RecentFormService>();
        services.AddSingleton<EnemyTendencyService>();
        // Counter items: one item-stats call for your hero and one per enemy hero, all ranks, cached for the benchmark TTL.
        services.AddSingleton(sp => new CounterItemService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays)));
        services.AddSingleton(sp => new MatchupService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays)));

        // Soul samples: live feed now, OCR seam ready
        services.AddSingleton<LiveFeedSoulSource>();
        services.AddSingleton<IOcrEngine>(sp => sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Ocr.Engine == OcrEngineKind.WindowsMediaOcr
            ? new WindowsMediaOcrEngine(sp.GetService<ILogger<WindowsMediaOcrEngine>>())
            : new NullOcrEngine()); // Tesseract is not bundled
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            // Captures only the calibrated regions of the game window, and only while OCR is switched on.
            var gameWindow = new GameWindowTracker();
            // Only while the game is in front: alt-tabbed, the boxes would capture whatever covers it.
            return new OcrSoulSource(new ScreenRegionCapture(() => gameWindow.Poll().Foreground ? gameWindow.ClientBounds() : null), new SettingsHudRegionMap(() => monitor.CurrentValue.Ocr.Regions),
                sp.GetRequiredService<IOcrEngine>(), sp.GetRequiredService<IClock>(), monitor.CurrentValue.Ocr.MinHealthy,
                log: sp.GetService<ILogger<OcrSoulSource>>(), enabled: () => monitor.CurrentValue.Ocr.Enabled);
        });
        services.AddSingleton(sp => new OcrJumpGuard(log: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OcrJumpGuard>()));
        services.AddSingleton<ISoulSampleSource>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            return new CompositeSoulSource(sp.GetRequiredService<LiveFeedSoulSource>(), sp.GetRequiredService<OcrSoulSource>(),
                () => monitor.CurrentValue.Meter.SoulSource, () => monitor.CurrentValue.Ocr.Enabled, sp.GetRequiredService<OcrJumpGuard>());
        });

        // UI
        services.AddSingleton(sp => new OverlayState
        {
            WindowSeconds = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Meter.WindowSeconds,
            Icons = sp.GetRequiredService<ItemIcons>(),
        });
        services.AddSingleton<MatchSession>();
        services.AddSingleton<AudioCuePlayer>();
        services.AddSingleton<OverlayWindow>();
        services.AddSingleton<MainWindow>();
    }
}
