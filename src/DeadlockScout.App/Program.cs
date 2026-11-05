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
    /// <summary>
    /// Set by a second start of the app: the running one opens its main window, so starting it again (from the Start
    /// menu, say) never looks like nothing happened. Session-local, like the single-instance mutex.
    /// </summary>
    private const string ShowMainEvent = "DeadlockScout.ShowMain";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--screenshots") return RenderScreenshots(args[1]);

        int code;
        using (var mutex = new Mutex(true, "DeadlockScout.SingleInstance", out bool first))
        {
            if (!first)
            {
                // Already running: ask it to show itself, then leave. If it has not set the event up yet (it is still
                // starting), there is nothing to ask, and it opens its window when it has started anyway.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowMainEvent, out var show))
                        using (show) show.Set();
                }
                catch (Exception) { }
                return 0;
            }
            code = RunApp(args);
        }
        // Everything is stopped and disposed by now; make sure no stray foreground thread keeps the process (and the
        // terminal that started it) alive.
        Environment.Exit(code);
        return code;
    }

    /// <summary>Where the log files go, next to settings.json and the cache.</summary>
    public static string LogFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "logs");

    private static int RunApp(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Information).AddDebug()
                // The log a player can open or send: %LOCALAPPDATA%\DeadlockScout\logs, one file a day, a week kept.
                .AddProvider(new DeadlockScout.Steam.Logging.FileLoggerProvider(LogFolder)))
            .ConfigureServices(services => ConfigureServices(services))
            .Build();
        // Before anything else loads: the app's own process, locked down (DLL search, image loads, access list).
        ProcessHardening.Apply(host.Services.GetRequiredService<JsonSettingsMonitor>().CurrentValue.Security.HardenProcess,
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DeadlockScout.Hardening"));

        var app = new App();
        app.InitializeComponent();
        var state = host.Services.GetRequiredService<OverlayState>();
        var overlay = host.Services.GetRequiredService<OverlayWindow>();
        var shell = host.Services.GetRequiredService<AppShell>();
        // A second start sets this event (Main above); the main window opens on the UI thread.
        using var showMain = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainEvent);
        var showMainWait = ThreadPool.RegisterWaitForSingleObject(showMain,
            (_, _) => app.Dispatcher.BeginInvoke(shell.ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);
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
            shell.CloseAll();
            tray?.Dispose();
            StopServices();
            host.Services.GetRequiredService<GlyphStore>().Flush(); // what a match taught the glyph reader in its last seconds
            app.Shutdown();
        }
        var updates = host.Services.GetRequiredService<UpdateService>();
        async void CheckUpdatesNow()
        {
            try
            {
                var (title, text) = await updates.CheckNowAsync(CancellationToken.None);
                await app.Dispatcher.BeginInvoke(() => tray?.Notify(title, text));
            }
            catch (Exception) { app.Dispatcher.BeginInvoke(() => tray?.Notify("Couldn't check for updates", "Try again later.")); }
        }
        var overlaySettings = host.Services.GetRequiredService<JsonSettingsMonitor>().CurrentValue.Overlay;
        tray = new TrayIcon(shell.ShowMain, overlay.ToggleVisible, overlay.ToggleScout, shell.TogglePreview, shell.OpenMatchHistory, shell.OpenCalibration, Exit,
            overlaySettings.HotkeyLayout, shell.OpenSettingsWindow, shell.OpenSetupWizard, shell.OpenDiagnosticsWindow,
            CheckUpdatesNow, overlaySettings.HotkeyMain);
        overlay.MainHotkey += shell.ShowMain;
        overlay.PreviewHotkey += shell.TogglePreview;
        shell.ExitRequested += Exit;
        shell.FirstHiddenToTray += () => tray.Notify("Still running", $"DeadlockScout is in the tray: open it again from there or with {overlaySettings.HotkeyMain}. Use Exit in the window or the tray menu to quit.");
        // Ctrl+C in the terminal stops the host; take the whole app down with it.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => app.Dispatcher.BeginInvoke(Exit));
        updates.OfferFound += offer => app.Dispatcher.BeginInvoke(() => tray.ShowUpdate(offer.Manifest!.Version, async () =>
        {
            if (state.InMatch) { tray.Notify("Update", "After the match: updates never install during one."); return; }
            if (await updates.InstallAsync(CancellationToken.None)) Exit();
            else tray.Notify("Update", "The update could not be downloaded or did not match its signature; nothing was installed.");
        }));
        // A box that stops reading mid-match is said once, in the tray; a click opens the calibration tool to redraw it.
        host.Services.GetRequiredService<SessionRefreshService>().BreakNudge += nudge =>
            app.Dispatcher.BeginInvoke(() => tray.Notify("Time for a break?", nudge));
        host.Services.GetRequiredService<SettingsHudRegionMap>().Carried += (w, h) =>
            app.Dispatcher.BeginInvoke(() => tray.Notify("OCR boxes moved", $"The game window is now {w}×{h}, another shape than the boxes were drawn on, so they were moved to match: check them in the calibration tool.", shell.OpenCalibration));
        host.Services.GetRequiredService<OcrSoulSource>().ReadsFailing += warning =>
            app.Dispatcher.BeginInvoke(() => tray.Notify("OCR cannot read a box", warning, shell.OpenCalibration));
        host.Services.GetRequiredService<PostMatchCoordinator>().ReportReady += _ =>
            app.Dispatcher.BeginInvoke(() => tray.Notify("Match report", state.LastMatch.Title));

        app.Startup += async (_, _) =>
        {
            await host.StartAsync();
            ShowWhatsNew(host.Services.GetRequiredService<JsonSettingsMonitor>(), tray);
            overlay.Show(); // hidden again by its own timer until the game is in front
            shell.ShowMain();
            try { await host.Services.GetRequiredService<AssetCatalog>().LoadAsync(CancellationToken.None); } catch { }
            host.Services.GetRequiredService<MatchSession>(); // subscribes to the detector
            host.Services.GetRequiredService<AudioCuePlayer>(); // subscribes to the overlay's cues
            await host.Services.GetRequiredService<IMatchDetector>().StartAsync(CancellationToken.None);
            shell.RefreshDetection();
            if (!host.Services.GetRequiredService<JsonSettingsMonitor>().CurrentValue.Launch.SetupDone)
                shell.OpenSetupWizard(); // first run: walk through Steam, -condebug, display mode and OCR
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
        showMainWait.Unregister(null);
        try { host.Dispose(); } catch { } // releases the console lifetime, which otherwise holds up process exit
        return code;
    }

    /// <summary>
    /// The first start of an installed copy after an update says so once, linking that release's notes; then the running
    /// version is kept as the last one run. Build-folder runs keep nothing, so they never hide a notice.
    /// </summary>
    private static void ShowWhatsNew(JsonSettingsMonitor settings, TrayIcon? tray)
    {
        if (!UpdateService.IsInstalled) return;
        var s = settings.CurrentValue;
        var running = UpdateService.Running;
        if (DeadlockScout.Core.Updates.WhatsNew.NotesFor(s.Updates.LastRunVersion, running) is { } notes)
            tray?.Notify($"Updated to {running.ToString(3)}", "Click to see what's new in this version.",
                () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(notes) { UseShellExecute = true }); } catch { } });
        var keep = DeadlockScout.Core.Updates.WhatsNew.Keep(running);
        if (s.Updates.LastRunVersion != keep)
            try { settings.Save(s with { Updates = s.Updates with { LastRunVersion = keep } }); } catch (System.IO.IOException) { }
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
        // The same lock-down as a real start, so the Windows runner shows it holds (Diagnostics' "Process hardening" line)
        // and that the windows still render under it.
        ProcessHardening.Apply(true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
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

                var shell = host.Services.GetRequiredService<AppShell>();
                shell.LoadDemo();
                var main = new MainWindow(shell, host.Services.GetRequiredService<OverlayState>());
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
                    counterItems: (mine, enemy, _) => Task.FromResult(MatchHistoryWindow.DemoCounters(mine, enemy)),
                    sourceRates: (hero, tier, _) => Task.FromResult<SourceRates?>(MatchHistoryWindow.DemoSourceRates(hero, tier)),
                    targets: new TargetSettings { LaningSpm = 420, MaxLaningDeaths = 1, FixPhase = GamePhase.Mid },
                    sendMatches: _ => Task.FromResult("demo"))   // shows the Send button; never pressed in the shot
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

                // The OCR region tool with the souls box from the 1080p preset and the clock not set yet, so the preset
                // button shows (the real boxes are drawn over the game).
                new CalibrationWindow(host.Services.GetRequiredService<JsonSettingsMonitor>(), () => null, new NullOcrEngine(), new GlyphBook()).RenderPreview(Path.Combine(folder, "ocr-calibration.png"), backdrop, new[]
                {
                    (RegionCalibration.SoulsKey, HudPresets.For(1920, 1080)!.Souls),
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
        services.AddTransient<RedirectHandler>();
        services.AddTransient<RetryHandler>();
        services.AddSingleton<RateLimiterRegistry>();
        services.AddTransient(sp => new RateLimitedHandler(sp.GetRequiredService<RateLimiterRegistry>(), sp.GetRequiredService<MatchActivityGate>().WaitAsync,
            sp.GetService<ILogger<RateLimitedHandler>>(), sp.GetRequiredService<DiagnosticsLog>()));
        // Diagnostics innermost: it sees every attempt on the wire, retries included. Redirects outermost, with the
        // socket handler's own redirects off: each hop goes through the API key check again, so the key never follows
        // a redirect to another host.
        services.AddHttpClient<IDeadlockApi, DeadlockApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<RedirectHandler>()
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RetryHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>()
            .AddHttpMessageHandler<DiagnosticsHandler>();
        // Live feed: hosted live query on api.deadlock-api.com (default) or the self-hosted live-events container.
        // Both streams stay open for the whole match, so no client timeout; the hosted one goes through the API key and rate limiter.
        services.AddHttpClient<HostedLiveQueryStream>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<RedirectHandler>()
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>()
            .AddHttpMessageHandler<DiagnosticsHandler>();
        services.AddHttpClient<SseLiveEventStream>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        // GitHub's releases for the updater: its own client (GitHub wants a User-Agent), never the deadlock-api pipeline.
        services.AddHttpClient("updates", c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadlockScout/{UpdateService.Running}");
        });
        services.AddSingleton(sp => new DeadlockScout.Api.Updates.UpdateClient(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("updates"),
            log: sp.GetService<ILogger<DeadlockScout.Api.Updates.UpdateClient>>()));
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
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
        services.AddSingleton(sp => new PlayedMatches(sp.GetRequiredService<ICache>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new MatchHistoryService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            sp.GetRequiredService<IClock>(), sp.GetService<ILogger<MatchHistoryService>>(), sp.GetRequiredService<PlayedMatches>()));

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
        services.AddHostedService<PerformanceSampler>();
        // Build hint: item purchases by net worth and ability orders, per hero, bracket and patch.
        services.AddSingleton(sp => new BuildHintService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays), sp.GetService<ILogger<BuildHintService>>()));
        // After each match: replay salts to deadlock-api, then the post-match report once the API has the match.
        services.AddSingleton(sp => new PostMatchService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<CompositeBenchmark>(),
            sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<PostMatchService>>()));
        services.AddSingleton(sp => new SaltIngest(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            since => sp.GetRequiredService<GamePaths>().SteamPath is { } steam ? new SteamHttpCache(steam).ScanSalts(since) : Array.Empty<MatchSalts>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<SaltIngest>>(),
            () => sp.GetRequiredService<GamePaths>().SteamPath is { } steam && new SteamHttpCache(steam).Exists));
        // Session view: today's results, rank change, loss-streak nudge, hero pool against the meta.
        services.AddSingleton(sp => new SessionService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), log: sp.GetService<ILogger<SessionService>>(),
            gate: sp.GetServices<IPaceBenchmark>().FirstOrDefault(p => p.Name == AnalyticsBucketBenchmark.ProviderName)));
        services.AddSingleton<SessionRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionRefreshService>());
        services.AddSingleton<PostMatchCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<PostMatchCoordinator>());
        // Lane matchups: one hero-counter-stats table per bracket and patch.
        services.AddSingleton<RecentFormService>();
        services.AddSingleton<TeamFormService>();
        services.AddSingleton<EnemyTendencyService>();
        // Counter items: one item-stats call for your hero and one per enemy hero, all ranks, cached for the benchmark TTL.
        services.AddSingleton(sp => new CounterItemService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays)));
        // Soul sources: one rank sample per hero and tier (the metadata benchmark's call), reduced and cached for the benchmark TTL.
        services.AddSingleton(sp => new SoulSourceService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays),
            sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Meter.MetadataSampleSize));
        services.AddSingleton(sp => new MatchupService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays)));

        // Soul samples: live feed now, OCR seam ready
        services.AddSingleton(sp =>
        {
            var catalog = sp.GetRequiredService<AssetCatalog>();
            return new LiveFeedSoulSource(sp.GetRequiredService<ILiveMatchFeed>(), catalog.HeldCost);
        });
        services.AddSingleton(sp => new GlyphStore(log: sp.GetService<ILogger<GlyphStore>>()));
        services.AddSingleton<IOcrEngine>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            IOcrEngine windows = monitor.CurrentValue.Ocr.Engine == OcrEngineKind.WindowsMediaOcr
                ? new WindowsMediaOcrEngine(sp.GetService<ILogger<WindowsMediaOcrEngine>>())
                : new NullOcrEngine(); // Tesseract is not bundled
            // The learned glyphs first; Windows OCR (loaded on its first read) only for what they cannot read.
            var store = sp.GetRequiredService<GlyphStore>();
            var engine = new GlyphOcrEngine(store.Glyphs, windows, () => monitor.CurrentValue.Ocr.GlyphReader);
            engine.Learned += _ => store.SaveSoon(); // learning during a match: saved at most every 30 s
            return engine;
        });
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            // The boxes follow the game window's shape; its client size is read from the same tracker as the capture.
            var window = new GameWindowTracker();
            return new SettingsHudRegionMap(() => monitor.CurrentValue.Ocr.Regions,
                () => window.ClientBounds() is { Width: > 0, Height: > 0 } b ? (b.Width, b.Height) : null);
        });
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            // Captures only the calibrated regions of the game window, and only while OCR is switched on.
            var gameWindow = new GameWindowTracker();
            // Only while the game is in front: alt-tabbed, the boxes would capture whatever covers it.
            var regions = sp.GetRequiredService<SettingsHudRegionMap>();
            return new OcrSoulSource(new ScreenRegionCapture(() => gameWindow.Poll().Foreground ? gameWindow.ClientBounds() : null), regions,
                sp.GetRequiredService<IOcrEngine>(), sp.GetRequiredService<IClock>(), monitor.CurrentValue.Ocr.MinHealthy,
                log: sp.GetService<ILogger<OcrSoulSource>>(), enabled: () => monitor.CurrentValue.Ocr.Enabled);
        });
        services.AddSingleton(sp =>
        {
            var catalog = sp.GetRequiredService<AssetCatalog>();
            return new OcrJumpGuard(log: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OcrJumpGuard>(), prices: catalog.ShopPrices);
        });
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
        services.AddSingleton<AppShell>(); // builds the main window on demand
    }
}
