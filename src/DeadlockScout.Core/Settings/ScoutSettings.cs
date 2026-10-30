namespace DeadlockScout.Core.Settings;

public enum SoulSourceMode { Auto, LiveFeed, Ocr }

public enum OcrEngineKind { WindowsMediaOcr, Tesseract }

/// <summary>The lobby scout list on the overlay: never, always, for a while after the roster arrives, or only while the hotkey has it toggled on.</summary>
public enum ScoutPanelMode { Off, On, Auto, Hotkey }

/// <summary>Bound to %LOCALAPPDATA%\DeadlockScout\settings.json; edits apply without restart through IOptionsMonitor.</summary>
public sealed record ScoutSettings
{
    /// <summary>The settings shape this file was last written with; <see cref="Migrate"/> brings older files up to date.</summary>
    public const int CurrentVersion = 3;

    /// <summary>0 in files written before versioning (the property was added in version 2).</summary>
    public int Version { get; init; }

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
    public SessionSettings Session { get; init; } = new();
    public ObjectiveSettings Objectives { get; init; } = new();
    public GoalSettings Goal { get; init; } = new();
    public TimerSettings Timers { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();

    /// <summary>
    /// Brings settings written by an older version up to date. Every file gets the defaults of the build that wrote it,
    /// so an old default found in an old file is that default, not a choice.
    /// Version 2: the scout list's default changed from Auto (pops up for a while after the roster arrives) to Hotkey,
    /// because it takes a lot of the screen.
    /// Version 3: the first builds allowed one live/url lookup an hour, so a second match in the same hour (watching two
    /// games back to back) waited up to an hour for its broadcast. Raised to the current default.
    /// </summary>
    public static ScoutSettings Migrate(ScoutSettings s)
    {
        if (s.Version >= CurrentVersion) return s;
        if (s.Version < 2 && s.Overlay.Panels.Scout == ScoutPanelMode.Auto)
            s = s with { Overlay = s.Overlay with { Panels = s.Overlay.Panels with { Scout = ScoutPanelMode.Hotkey } } };
        if (s.Api.Limits.LiveUrlPerHour == FirstLiveUrlPerHour)
            s = s with { Api = s.Api with { Limits = s.Api.Limits with { LiveUrlPerHour = new RateLimitSettings().LiveUrlPerHour } } };
        return s with { Version = CurrentVersion };
    }

    /// <summary>The live/url limit the first builds wrote into settings.json.</summary>
    private const int FirstLiveUrlPerHour = 1;

    /// <summary>A copy with only <see cref="GoalSettings.IncludeLowRanks"/> changed; used by the match history's rank list.</summary>
    public ScoutSettings WithLowRanks(bool include)
        => Goal.IncludeLowRanks == include ? this : this with { Goal = Goal with { IncludeLowRanks = include } };

    /// <summary>A copy with only the audio cue switches changed; used by the main window's audio row.</summary>
    public ScoutSettings WithAudio(AudioSettings audio) => Audio == audio ? this : this with { Audio = audio };

    /// <summary>A copy with only the live feed source changed; used by the main window's Direct/Hosted switch.</summary>
    public ScoutSettings WithLiveSource(LiveSource source)
        => Api.LiveSource == source ? this : this with { Api = Api with { LiveSource = source } };
}

/// <summary>Where the live match feed comes from.</summary>
public enum LiveSource
{
    /// <summary>api.deadlock-api.com's live SQL query over the broadcast. Nothing to install. The fallback for Direct.</summary>
    Hosted,
    /// <summary>The deadlock-api live-events container you run yourself, at <see cref="ApiSettings.LiveUrl"/>.</summary>
    SelfHosted,
    /// <summary>
    /// The default. The app reads Valve's broadcast itself with the vendored DemoFile parser; only the broadcast URL comes
    /// from the API. Falls back to Hosted for the match if the broadcast cannot be parsed (a game patch the parser does not
    /// handle yet), unless <see cref="ApiSettings.DirectFallbackToHosted"/> is off.
    /// </summary>
    Direct,
}

public sealed record ApiSettings
{
    public string BaseUrl { get; init; } = "https://api.deadlock-api.com";
    public string AssetsUrl { get; init; } = "https://assets.deadlock-api.com";
    /// <summary>Direct (default: the app reads Valve's broadcast, falling back to Hosted), Hosted, or SelfHosted.</summary>
    public LiveSource LiveSource { get; init; } = LiveSource.Direct;
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
    /// <summary>
    /// Only games from the last this-many days feed the personal curve: older matches play a different economy, and the
    /// API fetches their metadata through a slow, rate-limited fallback.
    /// </summary>
    public int PersonalMaxAgeDays { get; init; } = 30;
    /// <summary>
    /// Between matches, warm the benchmark curves (instant, accurate and personal) for this many of your most played
    /// recent heroes, so the accurate curve is ready when the match starts. 0 turns prefetching off.
    /// </summary>
    public int PrefetchHeroes { get; init; } = 3;
    /// <summary>Minutes between prefetch passes; a pass of cached curves costs at most the match-history call.</summary>
    public int PrefetchIntervalMinutes { get; init; } = 30;
}

public sealed record OcrSettings
{
    public bool Enabled { get; init; }
    public OcrEngineKind Engine { get; init; } = OcrEngineKind.WindowsMediaOcr;
    public HudRegions Regions { get; init; } = new();
    public double MinHealthy { get; init; } = 0.8;
}

/// <summary>
/// Screen regions the OCR source reads, set with the app's calibration tool. Each is a rectangle in fractions (0..1) of the
/// game window's client area, so one calibration works at any resolution. Only these rectangles are ever captured.
/// Null until calibrated.
/// </summary>
public sealed record HudRegions
{
    /// <summary>Your soul count.</summary>
    public HudRect? Souls { get; init; }
    /// <summary>The match clock.</summary>
    public HudRect? Timer { get; init; }
    /// <summary>Enemy soul counts as the scoreboard lists them, one per enemy row from the top (up to 6; null = not set).</summary>
    public IReadOnlyList<HudRect?> EnemySouls { get; init; } = Array.Empty<HudRect?>();
}

/// <summary>A rectangle in fractions (0..1) of the game window's client area.</summary>
public sealed record HudRect(double X, double Y, double Width, double Height);

public sealed record OverlaySettings
{
    public bool Enabled { get; init; } = true;
    public string HotkeyToggle { get; init; } = "Ctrl+Shift+O";
    /// <summary>Shows or hides the lobby scout list (the name predates that being all it does).</summary>
    public string HotkeyLayout { get; init; } = "Ctrl+Shift+L";
    public bool ExcludeFromCapture { get; init; } = true;
    public PanelSettings Panels { get; init; } = new();
    /// <summary>Panel positions keyed by "{panel}@{width}x{height}".</summary>
    public Dictionary<string, PanelPosition> Positions { get; init; } = new();
    /// <summary>Seconds the scout list stays up after the roster arrives when Panels.Scout is Auto.</summary>
    public int ScoutAutoSeconds { get; init; } = 90;
    /// <summary>
    /// The panels' size on top of the HUD scaling (which already follows the game window's height, 1080p = 1.0):
    /// 0.6–1.6, 1 = as designed. Positions do not change with it.
    /// </summary>
    public double PanelScale { get; init; } = 1.0;
    /// <summary>Layouts saved by name: which panels show, where, and their size. See <see cref="OverlayPresets"/>.</summary>
    public Dictionary<string, LayoutPreset> Presets { get; init; } = new();
}

public sealed record PanelSettings
{
    public bool Pace { get; init; } = true;
    public bool Lane { get; init; } = true;
    public ScoutPanelMode Scout { get; init; } = ScoutPanelMode.Hotkey;
    public bool EnemyPace { get; init; } = true;
    /// <summary>A small line under the pace strip: your pace delta over the last ten minutes.</summary>
    public bool Sparkline { get; init; } = true;
    /// <summary>On the sparkline: your lane opponents' pace (else the richest enemies) beside yours, with their souls against yours.</summary>
    public bool SparklineEnemies { get; init; } = true;
    /// <summary>The lane card shows and hides with the scout list (Ctrl+Shift+L) instead of staying up all match.</summary>
    public bool LaneWithScout { get; init; } = true;
    /// <summary>A second pace line under the pace strip: you against your own recent games on the hero.</summary>
    public bool Personal { get; init; } = true;
    public bool Build { get; init; } = true;
    /// <summary>On the enemy pace board: flag an enemy's power-spike item (and their ultimate) when they get it.</summary>
    public bool Spikes { get; init; } = true;
    public bool Session { get; init; } = true;
    /// <summary>Mid boss, Rejuvenators and urn, from the Direct feed.</summary>
    public bool Objectives { get; init; } = true;
    /// <summary>The laning timers: the jungle's first spawns, with a "push now" warning before each.</summary>
    public bool Timers { get; init; } = true;
    /// <summary>On the build hint: your next usual big item and when the goal rank's players have it.</summary>
    public bool NextItem { get; init; } = true;
}

public sealed record PanelPosition(double X, double Y);

public sealed record FlagSettings
{
    public int NewOnHeroMaxGames { get; init; } = 5;
    public int SmurfMaxTotalGames { get; init; } = 50;
    public double SmurfMinWinRate { get; init; } = 0.7;
    public int SmurfMinGamesForWinRate { get; init; } = 10;
    public int UnknownBelowGames { get; init; } = 3;
    /// <summary>Tag likely parties on the scout list ("Team 1".."Team 6"): one cached mate-stats call per player.</summary>
    public bool PartyTags { get; init; } = true;
    /// <summary>Party matches together, in either player's public history, that link two teammates.</summary>
    public int PartyMinMatches { get; init; } = 1;
}

public sealed record IngestSettings
{
    public bool AutoSubmitSalts { get; init; } = true;
}

/// <summary>The session view: today's results, rank change, a loss-streak nudge and your hero pool against the meta.</summary>
public sealed record SessionSettings
{
    /// <summary>A day starts at this local hour, so a late-night session counts with the evening it began in.</summary>
    public int DayStartsAtHour { get; init; } = 5;
    /// <summary>Losses in a row that bring up the nudge; 0 turns it off.</summary>
    public int LossStreakNudge { get; init; } = 3;
    /// <summary>Heroes shown in the hero pool, most played first.</summary>
    public int HeroPoolSize { get; init; } = 5;
    /// <summary>Hours between refreshes while idle; the view also refreshes after each match.</summary>
    public int RefreshHours { get; init; } = 1;
}

public sealed record LaunchSettings
{
    public bool EnsureCondebug { get; init; } = true;
    /// <summary>The first-run wizard was finished or skipped; until then it opens at start-up. The tray can open it again.</summary>
    public bool SetupDone { get; init; }
}

/// <summary>The objectives panel. The broadcast has no respawn timers, only whether the mid boss and the urn are there.</summary>
public sealed record ObjectiveSettings
{
    /// <summary>
    /// The mid boss's respawn delay used until one has been observed in the match (then the observed one is used).
    /// UNVERIFIED default (see <c>ObjectiveTracker.DefaultMidBossRespawn</c>); the log records each delay the app sees.
    /// </summary>
    public int MidBossRespawnSeconds { get; init; } = 420;
    /// <summary>
    /// The urn's schedule by the game clock: first at this time, then every <see cref="UrnIntervalSeconds"/>. 10:00, then
    /// every 5:00, as a real match showed (verification note 23). The session logs each urn the broadcast shows, to catch
    /// a patch that moves it. 0 turns the urn countdown off.
    /// </summary>
    public int UrnFirstSpawnSeconds { get; init; } = 600;
    public int UrnIntervalSeconds { get; init; } = 300;
}

/// <summary>The rank gate: the average souls of a rank tier on your hero, drawn on the soul timeline and shown on the overlay.</summary>
public sealed record GoalSettings
{
    /// <summary>"next" (the default): the tier above your rank. Or a tier name such as "Oracle" to aim at it whatever your rank.</summary>
    public string Rank { get; init; } = "next";
    /// <summary>Initiate and Seeker join the rank lists and can be the goal. Off by default, so Alchemist is the lowest.</summary>
    public bool IncludeLowRanks { get; init; }
    /// <summary>The goal line on the overlay's pace panel.</summary>
    public bool Overlay { get; init; } = true;
    /// <summary>The live phase chips under the goal line: laning, mid and late game against the goal so far.</summary>
    public bool Phases { get; init; } = true;
}

/// <summary>A jungle spawn the laning timers count down to: its name, first spawn by the game clock, and how it comes back.</summary>
public sealed record SpawnRule(string Name, int FirstSpawnSeconds, string Note = "");

/// <summary>The laning timers panel: the jungle's first spawns, with a warning before each so you can push your wave.</summary>
public sealed record TimerSettings
{
    /// <summary>How long before a spawn the row turns to "push now".</summary>
    public int LeadSeconds { get; init; } = 30;
    /// <summary>How long a row stays, marked up, after its spawn.</summary>
    public int ShowUpSeconds { get; init; } = 20;
    public int MaxRows { get; init; } = 3;
    /// <summary>
    /// How far the broadcast runs behind the game, used only when neither OCR of the HUD clock nor the console log's
    /// game-start line gives the live clock. UNVERIFIED: the docs say 60–90 s; the app logs the delay it measures.
    /// </summary>
    public int BroadcastDelaySeconds { get; init; } = 75;
    /// <summary>
    /// UNVERIFIED defaults from community timer pages (deadlock.wiki, senseos.gg, deadlockhandbook.com, unDeadlock's
    /// Sep 30 2026 patch notes), not from a replay; change them here after a patch.
    /// </summary>
    public List<SpawnRule> Spawns { get; init; } = new()
    {
        new("Small camps", 120, "back 1:25 after a clear"),
        new("Crates", 180, "back 3:00 after broken"),
        new("Medium camps", 300, "back 4:50 after a clear"),
        new("Midtown crates", 300, "back 5:00 after broken"),
        new("Large camps", 480, "back 5:35 after a clear"),
        new("Sinner's Sacrifice", 480, "back 5:00 after a clear"),
    };
}

/// <summary>
/// Short sounds the app plays itself (never the game's): opt-in, off until switched on, each cue on its own switch, from
/// the main window's audio row or here.
/// </summary>
public sealed record AudioSettings
{
    /// <summary>The master switch; off by default.</summary>
    public bool Enabled { get; init; }
    /// <summary>0–1.</summary>
    public double Volume { get; init; } = 0.6;
    /// <summary>A laning timer reached "push now".</summary>
    public bool PushNow { get; init; } = true;
    /// <summary>The mid boss's return or the next urn is inside the lead.</summary>
    public bool Objectives { get; init; } = true;
    /// <summary>Your pace has been more than <see cref="PaceBehindPercent"/> under the rank gate for <see cref="PaceBehindSeconds"/>.</summary>
    public bool PaceBehind { get; init; } = true;
    public int PaceBehindPercent { get; init; } = 5;
    public int PaceBehindSeconds { get; init; } = 60;
    /// <summary>The least game time between two pace cues.</summary>
    public int PaceCooldownSeconds { get; init; } = 180;
}
