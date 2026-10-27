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
        services.AddHttpClient<ILiveEventStream, SseLiveEventStream>(c => c.Timeout = Timeout.InfiniteTimeSpan);
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
