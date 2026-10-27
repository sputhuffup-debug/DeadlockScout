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

public sealed record ApiSettings
{
    public string BaseUrl { get; init; } = "https://api.deadlock-api.com";
    public string AssetsUrl { get; init; } = "https://assets.deadlock-api.com";
    /// <summary>Live match events service (WebSocket). Separate host from the REST API.</summary>
    public string LiveUrl { get; init; } = "https://live.deadlock-api.com";
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
    public int LiveUrlPerHour { get; init; } = 1;
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
