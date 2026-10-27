namespace DeadlockScout.Core.Settings;

public enum SoulSourceMode { Auto, LiveFeed, Ocr }

public enum OcrEngineKind { WindowsMediaOcr, Tesseract }

public enum ScoutPanelMode { Off, On, Auto }

/// <summary>Bound to %LOCALAPPDATA%\DeadlockScout\settings.json; edits apply without restart through IOptionsMonitor.</summary>
public sealed record ScoutSettings
{
    public string? SteamPath { get; init; }
    public string? DeadlockPath { get; init; }
    public uint? AccountId { get; init; }
    public ApiSettings Api { get; init; } = new();
    public CacheSettings Cache { get; init; } = new();
    public MeterSettings Meter { get; init; } = new();
    public OcrSettings Ocr { get; init; } = new();
    public OverlaySettings Overlay { get; init; } = new();
    public FlagSettings Flags { get; init; } = new();
    public IngestSettings Ingest { get; init; } = new();
    public LaunchSettings Launch { get; init; } = new();
}

/// <summary>Where the live match feed comes from.</summary>
public enum LiveSource
{
    /// <summary>api.deadlock-api.com's live SQL query over the broadcast. Nothing to install.</summary>
    Hosted,
    /// <summary>The deadlock-api live-events container you run yourself, at <see cref="ApiSettings.LiveUrl"/>.</summary>
    SelfHosted,
    /// <summary>
    /// The app reads Valve's broadcast itself (DemoFile parser). Only the broadcast URL comes from the API. Falls back to
    /// Hosted for the match if the broadcast cannot be parsed, unless <see cref="ApiSettings.DirectFallbackToHosted"/> is off.
    /// </summary>
    Direct,
}

public sealed record ApiSettings
{
    public string BaseUrl { get; init; } = "https://api.deadlock-api.com";
    public string AssetsUrl { get; init; } = "https://assets.deadlock-api.com";
    /// <summary>Hosted (default, nothing to install) or SelfHosted (the live-events container at <see cref="LiveUrl"/>).</summary>
    public LiveSource LiveSource { get; init; } = LiveSource.Hosted;
    /// <summary>
    /// Only used when <see cref="LiveSource"/> is SelfHosted: the deadlock-api live-events service, normally the
    /// ghcr.io/deadlock-api/deadlock-live-events container on port 3000.
    /// </summary>
    public string LiveUrl { get; init; } = "http://localhost:3000";
    /// <summary>With LiveSource Direct: switch to the hosted feed for the rest of the match when the broadcast cannot be parsed.</summary>
    public bool DirectFallbackToHosted { get; init; } = true;
    /// <summary>Server ticks per second, for turning broadcast ticks into game time (64 on current builds).</summary>
    public int LiveTickRate { get; init; } = 64;
    public string? ApiKey { get; init; }
    public RateLimitSettings Limits { get; init; } = new();
}

/// <summary>Per-endpoint-family ceilings, kept below the documented per-IP caps.</summary>
public sealed record RateLimitSettings
{
    public int MatchHistoryPerMin { get; init; } = 2;
    public int HeroStatsPerMin { get; init; } = 30;
    public int MetadataPerMin { get; init; } = 10;
    public int AnalyticsPerMin { get; init; } = 60;
    /// <summary>The API allows 6 spectates per hour per IP; a match spectated in the last hour is served from its cache.</summary>
    public int LiveUrlPerHour { get; init; } = 4;
    /// <summary>The hosted live query allows 20 per minute per IP; a match needs two long-lived streams.</summary>
    public int LiveQueryPerMin { get; init; } = 10;
    public int DefaultPerMin { get; init; } = 30;
}

public sealed record CacheSettings
{
    public int PlayerStatsTtlHours { get; init; } = 12;
    public int SharedHistoryTtlHours { get; init; } = 24;
    public int BenchmarkTtlDays { get; init; } = 7;
    public int AssetsTtlHours { get; init; } = 24;
    /// <summary>"auto" = date bucket rotated every 7 days (or the client version when the assets API exposes one); anything else is used verbatim.</summary>
    public string PatchKey { get; init; } = "auto";
}

public sealed record MeterSettings
{
    public int WindowSeconds { get; init; } = 120;
    public SoulSourceMode SoulSource { get; init; } = SoulSourceMode.Auto;
    /// <summary>"auto" = your badge ± BracketWidth; or "min-max" badge numbers; or "all".</summary>
    public string BenchmarkBracket { get; init; } = "auto";
    public int BracketWidth { get; init; } = 10;
    /// <summary>Matches sampled by the metadata benchmark per hero and bracket.</summary>
    public int MetadataSampleSize { get; init; } = 40;
    public int PersonalSampleSize { get; init; } = 20;
}

public sealed record OcrSettings
{
    public bool Enabled { get; init; }
    public OcrEngineKind Engine { get; init; } = OcrEngineKind.WindowsMediaOcr;
    public HudRegions Regions { get; init; } = new();
    public double MinHealthy { get; init; } = 0.8;
}

/// <summary>Crop rectangles in 1080p-relative coordinates; null until the user calibrates them.</summary>
public sealed record HudRegions
{
    public HudRect? Souls { get; init; }
    public HudRect? Timer { get; init; }
}

public sealed record HudRect(double X, double Y, double Width, double Height);

public sealed record OverlaySettings
{
    public bool Enabled { get; init; } = true;
    public string HotkeyToggle { get; init; } = "Ctrl+Shift+O";
    public string HotkeyLayout { get; init; } = "Ctrl+Shift+L";
    public bool ExcludeFromCapture { get; init; } = true;
    public PanelSettings Panels { get; init; } = new();
    /// <summary>Panel positions keyed by "{panel}@{width}x{height}".</summary>
    public Dictionary<string, PanelPosition> Positions { get; init; } = new();
    /// <summary>Seconds the scout list stays up after the roster arrives when Panels.Scout is Auto.</summary>
    public int ScoutAutoSeconds { get; init; } = 90;
}

public sealed record PanelSettings
{
    public bool Pace { get; init; } = true;
    public bool Lane { get; init; } = true;
    public ScoutPanelMode Scout { get; init; } = ScoutPanelMode.Auto;
    public bool EnemyPace { get; init; } = true;
    public bool Build { get; init; } = true;
    public bool Session { get; init; } = true;
}

public sealed record PanelPosition(double X, double Y);

public sealed record FlagSettings
{
    public int NewOnHeroMaxGames { get; init; } = 5;
    public int SmurfMaxTotalGames { get; init; } = 50;
    public double SmurfMinWinRate { get; init; } = 0.7;
    public int SmurfMinGamesForWinRate { get; init; } = 10;
    public int UnknownBelowGames { get; init; } = 3;
}

public sealed record IngestSettings
{
    public bool AutoSubmitSalts { get; init; } = true;
}

public sealed record LaunchSettings
{
    public bool EnsureCondebug { get; init; } = true;
}
