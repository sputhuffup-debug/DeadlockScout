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
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Detection;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Sources;
using DeadlockScout.Ocr;
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

        using var mutex = new Mutex(true, "DeadlockScout.SingleInstance", out bool first);
        if (!first) return 0;

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Information).AddDebug())
            .ConfigureServices(services => ConfigureServices(services))
            .Build();

        var app = new App();
        app.InitializeComponent();
        var state = host.Services.GetRequiredService<OverlayState>();
        var overlay = host.Services.GetRequiredService<OverlayWindow>();
        var main = host.Services.GetRequiredService<MainWindow>();
        using var tray = new TrayIcon(() => { main.Show(); main.Activate(); main.RefreshDetection(); }, overlay.ToggleVisible, app.Shutdown);

        app.Startup += async (_, _) =>
        {
            await host.StartAsync();
            overlay.Show(); // hidden again by its own timer until the game is in front
            main.Show();
            try { await host.Services.GetRequiredService<AssetCatalog>().LoadAsync(CancellationToken.None); } catch { }
            host.Services.GetRequiredService<MatchSession>(); // subscribes to the detector
            await host.Services.GetRequiredService<IMatchDetector>().StartAsync(CancellationToken.None);
            main.RefreshDetection();
            if (!host.Services.GetRequiredService<GamePaths>().HasCondebug)
                state.Status.Warning = "Match detection needs the -condebug launch option. Use 'Add -condebug' (with Steam closed) or add it in Steam → Deadlock → Properties.";
        };
        app.Exit += async (_, _) =>
        {
            try { await host.Services.GetRequiredService<IMatchDetector>().StopAsync(); } catch { }
            await host.StopAsync(TimeSpan.FromSeconds(3));
            host.Dispose();
        };
        return app.Run();
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

                var main = host.Services.GetRequiredService<MainWindow>();
                main.LoadDemo();
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Left = 0; main.Top = 0;
                main.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var content = (FrameworkElement)main.Content;
                ScreenshotRenderer.RenderStacked(new[] { main.Header, main.Body }, main.Background, Path.Combine(folder, "main-window.png"), content.Margin);

                var backdrop = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x33, 0x3D, 0x47), System.Windows.Media.Color.FromRgb(0x0C, 0x0F, 0x12), 90);
                host.Services.GetRequiredService<OverlayWindow>().RenderPreview(Path.Combine(folder, "overlay.png"), backdrop);
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
        services.AddTransient<ApiKeyHandler>();
        services.AddTransient<RetryHandler>();
        services.AddSingleton<RateLimiterRegistry>();
        services.AddTransient(sp => new RateLimitedHandler(sp.GetRequiredService<RateLimiterRegistry>(), sp.GetRequiredService<MatchActivityGate>().WaitAsync));
        services.AddHttpClient<IDeadlockApi, DeadlockApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RetryHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>();
        // Live feed: hosted live query on api.deadlock-api.com (default) or the self-hosted live-events container.
        // Both streams stay open for the whole match, so no client timeout; the hosted one goes through the API key and rate limiter.
        services.AddHttpClient<HostedLiveQueryStream>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<ApiKeyHandler>()
            .AddHttpMessageHandler<RateLimitedHandler>();
        services.AddHttpClient<SseLiveEventStream>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<BroadcastUrlResolver>();
        services.AddSingleton<DirectBroadcastStream>();
        services.AddSingleton<ILiveEventStream>(sp => new LiveEventStreamSelector(
            sp.GetRequiredService<HostedLiveQueryStream>(), sp.GetRequiredService<SseLiveEventStream>(), sp.GetRequiredService<DirectBroadcastStream>(),
            sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>(), sp.GetService<ILogger<LiveEventStreamSelector>>()));
        services.AddSingleton<ILiveMatchFeed, LiveMatchFeed>();
        services.AddSingleton<IScoutingService, ScoutingService>();
        services.AddSingleton<IPatchKeyProvider>(sp => new ClientVersionPatchKeyProvider(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            new DatePatchKeyProvider(sp.GetRequiredService<IClock>(), sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.PatchKey)));
        services.AddSingleton(sp => new AssetCatalog(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(),
            TimeSpan.FromHours(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.AssetsTtlHours)));

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
        // Lane matchups: one hero-counter-stats table per bracket and patch.
        services.AddSingleton(sp => new MatchupService(sp.GetRequiredService<IDeadlockApi>(), sp.GetRequiredService<ICache>(), sp.GetRequiredService<IPatchKeyProvider>(),
            TimeSpan.FromDays(sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Cache.BenchmarkTtlDays)));

        // Soul samples: live feed now, OCR seam ready
        services.AddSingleton<LiveFeedSoulSource>();
        services.AddSingleton<ISoulSampleSource>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>();
            var ocr = new OcrSoulSource(new NullCaptureSource(), new SettingsHudRegionMap(() => monitor.CurrentValue.Ocr.Regions), new NullOcrEngine(),
                sp.GetRequiredService<IClock>(), monitor.CurrentValue.Ocr.MinHealthy);
            return new CompositeSoulSource(sp.GetRequiredService<LiveFeedSoulSource>(), ocr, () => monitor.CurrentValue.Meter.SoulSource, () => monitor.CurrentValue.Ocr.Enabled);
        });

        // UI
        services.AddSingleton(sp => new OverlayState { WindowSeconds = sp.GetRequiredService<IOptionsMonitor<ScoutSettings>>().CurrentValue.Meter.WindowSeconds });
        services.AddSingleton<MatchSession>();
        services.AddSingleton<OverlayWindow>();
        services.AddSingleton<MainWindow>();
    }
}
