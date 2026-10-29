# DeadlockScout — Seed Design Doc

Oct 26, 2026 · @M

## Purpose & scope

DeadlockScout is a local Windows app (C#, .NET 8) that detects your Deadlock match the moment it starts, scouts the lobby, and shows a click-through overlay with your souls-per-minute against the pace of the hero you are playing. It runs from the tray, never touches the game process, and caches everything so a lobby costs at most a handful of API calls.

In scope:

- Match detection from the game's own console log and Steam's logs
- Lobby roster, enemy and teammate scouting (games on hero, winrate, rank, recurring opponents, smurf flag)
- Soul-rate meter: 2-minute rolling average vs a per-hero, per-rank pace benchmark
- Enemy pace board, lane matchup card, matchup winrate, item and ability-order hints
- Post-match report built from local Steam cache data; auto-ingest of match salts
- Session layer: MMR history, daily W/L, hero pool vs meta
- Overlay as an external transparent window; OCR capture seam for a real-time source later

Out of scope, deliberately:

- Memory reading, DLL injection, DirectX hooks or any interaction with the game process (VAC risk)
- Any action in-game (no input automation)
- Hosting a backend; the app is single-user and local

## Data sources

Valve publishes no official Deadlock API; the app combines files the game writes on purpose with the community [deadlock-api.com](https://deadlock-api.com) service. Limits below are from the API's source as documented on [DeepWiki](https://deepwiki.com/deadlock-api/deadlock-api-rust) and the [deadlock-mod-manager issue #580](https://github.com/deadlock-mod-manager/deadlock-mod-manager/issues/580); verify against the live OpenAPI at `https://api.deadlock-api.com/docs` before coding (see Open questions).

### Local (zero API calls)

| Source | Path | What it gives | Needs |
| --- | --- | --- | --- |
| Console log | `<Deadlock>\game\citadel\console.log` | `Lobby <id> for Match <id> created`, matchmaking state, your hero, deaths, killcam hero, ping, end screen | `-condebug` launch option |
| Steam content log | `<Steam>\logs\content_log.txt` | Match start/end ("updates disabled/enabled"), no match id | Nothing |
| Steam login config | `<Steam>\config\loginusers.vdf` + newest `userdata\<id>\config\localconfig.vdf` | Your account\_id (SteamID3) | Nothing |
| Steam HTTP cache | `<Steam>\appcache\httpcache` | Full match metadata (net-worth series, items, deaths) after the client loads a match; replay URLs with salts | Open the match in-game once |
| Windows registry | `HKCU\Software\Valve\Steam\SteamPath` | Steam install path | Nothing |

### deadlock-api.com

| Endpoint | Used for | Limit / cost |
| --- | --- | --- |
| `GET /v1/matches/{id}/live/url` | Broadcast URL for the current match | 2 req/h per IP; cache per match |
| `GET /v1/matches/demo/live/query` (SSE) | Live roster: steam id, team, hero, net worth, KDA, rank per player | Stream; \~60–90 s behind real time |
| `GET /v1/players/hero-stats?account_ids=` | Games/winrate/KDA per hero for up to 1000 accounts in one call | One call per lobby after cache |
| `GET /v1/players/{id}/match-history` | Fallback match detection, personal benchmark input | Tight (order of 3 req/min per IP) |
| `GET /v1/players/{id}/enemy-stats`, `mate-stats`, `party-stats` | Recurring opponents/teammates, party sizes | One call per new player, cached |
| `GET /v1/players/mmr` | Rank/MMR history | Session layer only |
| `GET /v1/matches/{id}/metadata` | Full match data for benchmarks and reports | Expensive; background, low priority |
| `GET /v1/analytics/hero-stats` | Pace benchmark buckets (hero, duration, badge filters) | 100 req/s per IP; cached 1 h server-side |
| `GET /v1/analytics/item-stats`, ability-order, `hero-comb-stats` | Item timing, ability order, matchup winrate | Cached per hero/patch |
| `POST /v1/matches/salts` | Auto-ingest your matches | No auth required |
| `assets.deadlock-api.com/v2/heroes`, `/v2/items` | Hero and item names, icons | 24 h cache |

API key is optional and raises per-key quotas; it is accepted as `X-API-KEY`, `?api_key=` or `Authorization: Bearer`.

## Solution layout

&#91;embedded content: solution layout · 5 projects and what they touch\]

Arrows point at what each project depends on: App uses Core, the adapters implement Core's interfaces, and only the adapters touch files, the API or the database. Ocr is the dashed box: its interfaces exist from day one, its code arrives in M6.

## Runtime data flow

&#91;embedded content: runtime data flow · one match from lobby line to overlay\]

The console log fires once at lobby creation; the live feed then supplies both the roster (scouted once, through the cache) and the net-worth ticks (metered continuously against the benchmark curve). When the match ends, `PostMatchCoordinator` sends any new replay salts from the Steam cache to the ingest endpoint so the match is on record, then asks for the match's metadata on a backing-off schedule until the API has processed it, and shows the post-match report (result, K/D/A, souls and SPM, team net-worth rank, pace against the curve at 10/20/30 min with percentile, and lane souls against your lane opponents at 10 min) in the main window and as a tray notice. The Steam cache's own metadata format is still not captured, so the report reads the API. A null "not processed yet" answer is never cached.

## Core interfaces

Everything that can later be swapped (sample source, benchmark, cache, API) sits behind a small interface in `DeadlockScout.Core`; concrete types live in the other projects and are wired by DI.

```csharp
// Match detection
public record MatchContext(long MatchId, long LobbyId, uint MyAccountId, DateTimeOffset DetectedAt);

public interface IMatchDetector
{
    event Action<MatchContext> MatchStarted;
    event Action<long> MatchEnded;
    event Action<string> HeroLocked;      // hero internal name from console.log
}

// Live roster + samples
public record RosterPlayer(uint AccountId, string Name, int Team, int HeroId, int? Rank, int? Lane);
public record Roster(long MatchId, IReadOnlyList<RosterPlayer> Players, TimeSpan AsOfGameTime);

public interface ILiveMatchFeed
{
    Task<Roster> WaitForRosterAsync(MatchContext ctx, CancellationToken ct);
    IAsyncEnumerable<NetWorthTick> TicksAsync(MatchContext ctx, CancellationToken ct);
}
public record NetWorthTick(TimeSpan GameTime, uint AccountId, int NetWorth, int Kills, int Deaths, int Assists);

// Soul samples (OCR seam)
public enum SampleSource { LiveFeed, Ocr }
public record SoulSample(TimeSpan GameTime, int Souls, SampleSource Source);

public interface ISoulSampleSource
{
    SampleSource Kind { get; }
    TimeSpan ExpectedLatency { get; }
    bool IsHealthy { get; }
    IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, CancellationToken ct);
}

// Pace benchmark
public record RankBracket(int MinBadge, int MaxBadge);
public record PaceCurve(int HeroId, RankBracket Bracket, string PatchKey,
                        IReadOnlyList<PacePoint> Points, int SampleSize, string Provider);
public record PacePoint(int Minute, int P25, int P50, int P75, int Mean);

public interface IPaceBenchmark
{
    int Priority { get; }                                  // lower runs first
    Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct);
}

// Scouting
public record ScoutRow(RosterPlayer Player, int GamesOnHero, double WinRateOnHero,
                       int TotalGames, int? Rank, int? MetBefore, string? Flag);
public interface IScoutingService
{
    Task<IReadOnlyList<ScoutRow>> ScoutAsync(Roster roster, uint myAccountId, CancellationToken ct);
}

// Cache + API
public interface ICache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct);
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory,
                                bool staleWhileRevalidate, CancellationToken ct);
}

public interface IDeadlockApi
{
    Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct);
    Task<string> LiveUrlAsync(long matchId, CancellationToken ct);
    Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct);
    Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct);
    // enemy-stats, mate-stats, mmr, item-stats, ability-order, hero-comb-stats, salts, assets ...
}
```

Rules that keep the seams clean:

- `ISoulSampleSource` implementations never know about the overlay or the benchmark; they only emit samples with game time.
- `IPaceBenchmark` implementations are stateless over `ICache`; a `CompositeBenchmark` tries them by `Priority` and returns the first non-null curve, upgrading in the background.
- `IDeadlockApi` is the only type that holds an `HttpClient`; every method goes through the rate limiter and the cache.
- `IMatchDetector` combines `ConsoleLogTailer` (primary) and `SteamContentLogWatcher` (fallback, no match id) and dedupes.

## Soul rate meter & pace benchmark

The meter answers one question: at this game minute, on this hero, at my rank, am I above or below pace? It compares a 2-minute rolling rate against the same 2-minute rate derived from a per-hero net-worth curve.

### Samples

- `LiveFeedSoulSource` maps your `NetWorthTick`s from the SSE feed to `SoulSample`s, using the feed's own game clock. Latency \~75 s; the overlay labels the sample time ("as of 12:40").
- `OcrSoulSource`: `IHudRegionMap` (boxes for your soul counter, the match clock and each enemy scoreboard row, stored in settings as fractions of the game's client area) → `IRegionCapture` (a GDI BitBlt of **just that box** off the screen; never the whole screen or window, and nothing from the game process) → `IOcrEngine` (Windows.Media.Ocr first, Tesseract optional) → parse. `IsHealthy` is true when parse success over the last 30 s exceeds 80%.
- The boxes are set with the calibration tool (main window or tray: "Calibrate OCR regions"): a dimmed layer over the game where you pick a region and drag a box around it, with a live zoomed preview of exactly the pixels that box captures and what OCR reads from them, and the switch that turns OCR on (`ocr.enabled`). The tool and the overlay set `WDA_EXCLUDEFROMCAPTURE`, so they never appear in a capture. Enemy rows are read while the scoreboard is open.
- `OcrPreprocess` prepares each crop for the engine: grayscale, contrast stretch, dark text on white (the HUD draws light on dark), enlarged to at least 64 px tall, padded. `WindowsMediaOcrEngine` reads it with Windows.Media.Ocr in the first OCR-capable language of the Windows profile. Nothing is captured while OCR is off.
- Enemy rows are read in the calibration tool's preview; feeding them into the enemy pace board waits for the scoreboard row order to be checked on a real match.
- `CompositeSoulSource` honours `SoulSource = Auto | LiveFeed | Ocr`: in `Auto` it uses OCR when enabled and healthy, otherwise the feed, and switches without resetting the meter.

### Rate math

`SoulRateMeter` keeps a ring buffer of samples keyed by game time.

```latex
\mathrm{SPM}_{2}(t) = \frac{S(t) - S(t - 120\,\mathrm{s})}{2}
```

`S(t − 120 s)` is linearly interpolated between the two nearest samples; before 120 s of data exist, the window is from match start. The meter also exposes overall SPM (`S(t) / minutes`) and current net worth. Samples older than 10 minutes are dropped.

### Benchmark providers

| Provider | Priority | Source | Calls | Freshness |
| --- | --- | --- | --- | --- |
| `AnalyticsBucketBenchmark` | 1 (instant) | `analytics/hero-stats` filtered by hero, your badge range, and duration buckets 10–15, 15–20 … 45–50 min; mean final net worth per bucket → monotone curve | \~8 per hero/bracket | 7 days or patch |
| `MetadataSampleBenchmark` | 2 (accurate) | \~40 matches of the hero in your bracket; per-minute net-worth series from match metadata → P25/P50/P75/mean | \~40 per hero/bracket, background only | 7 days or patch |
| `PersonalBenchmark` | 3 (optional view) | Your last 20 games on the hero from the Steam cache or metadata | 0–20 | Invalidate on a new match |

Expected 2-minute rate at minute t comes from the same curve, so the comparison is like for like:

```latex
\mathrm{SPM}^{exp}_{2}(t) = \frac{NW(t) - NW(t-2)}{2}
```

`CompositeBenchmark` returns the best curve available now and queues the next provider; the overlay shows the provider name in a tooltip so you know how good the number is. Percentile is shown when the curve has P25/P75 (provider 2), otherwise only the delta vs mean.

The accurate and personal providers make background requests, which wait while a match is live. So between matches `BenchmarkWarmer` (run by `BenchmarkPrefetchService` every `prefetchIntervalMinutes`, only when no match is live) asks every provider, personal included, for your `prefetchHeroes` most played recent heroes in your bracket, and warms the bracket's lane matchup table. `BracketPolicy` is the one rule both the warmer and the match session use, so they hit the same cache keys. The personal curve is shown as a second pace line, "vs your last 12 on Haze: +4%".

### Overlay line

`Haze · 14:20 · 612 spm (2m) · pace 580 · +5% · 58th pct`

## Caching & rate limiting

Target: a lobby of 11 strangers costs 1 hero-stats call plus 1 live-URL call; a lobby of players you have met in the last 12 hours costs only the live-URL call. Benchmarks and assets are paid once per hero per patch.

Two layers: `IMemoryCache` (process lifetime) in front of SQLite at `%LOCALAPPDATA%\DeadlockScout\cache.db` (`Microsoft.Data.Sqlite`, WAL mode). Every entry stores `key`, `json`, `fetched_at`, `expires_at`, `etag` (unused until the API confirms ETag support).

| Data | Key | TTL | Policy |
| --- | --- | --- | --- |
| Hero / item assets | `assets:v2:heroes`, `assets:v2:items` | 24 h | Refresh on unknown hero id |
| Match history detail | `md:{me}:{match_id}` | never expires | A processed match never changes. Only games without a stored detail are asked for, all in one bulk `matches/metadata` call (`include_player_info`, `include_player_items`, the net worth series columns); one the API has not processed yet is asked for again next time |
| Item and hero icons (spike chips, match history) | `img:{url}` | 30 days | Fetched once per item on first use, only from `assets-bucket.deadlock-api.com` over HTTPS, at most 256 KB, through the Assets rate limit and without the API key; the URL changes when the art does |
| Player hero-stats (whole response) | `phs:{account_id}` | 12 h | Stale-while-revalidate |
| Enemy / mate stats | `enemy:{me}:{them}`, `mate:{me}:{them}` | 24 h | Stale-while-revalidate |
| Live URL | `live:{match_id}` | Forever | Never refetch (2 req/h) |
| Match metadata | `meta:{match_id}` | Forever | Immutable |
| Analytics bucket curve | `pace:a:{hero}:{bracket}:{patch}` | 7 days | Patch key bump invalidates |
| Metadata sample curve | `pace:m:{hero}:{bracket}:{patch}` | 7 days | Built in background between matches |
| Personal curve | `pace:p:{hero}:{me}` | Until new match on hero | Rebuilt from local cache; warmed between matches |
| Item timing / ability order | `items:{hero}:{bracket}:{patch}`, `abil:…` | 7 days | Lazy on first use |
| Lane matchup table (every hero pair, `analytics/hero-counter-stats`) | `counters:{bracket}:{patch}` | 7 days | Warmed between matches; read on first lane card |
| Player ranks | `ranks:{account_ids}` | 12 h | Stale-while-revalidate; shared by the lobby and the prefetch |
| Hero meta for the hero pool | `meta:heroes:{bracket}:{patch}` | 1 day | Session view only |
| Own match history | `mh:{me}` | 10 min | Personal benchmark, prefetch, latest match at startup |
| Salts already sent; last Steam cache scan | `ingest:submitted`, `ingest:last-scan` | Forever | Each match is sent once (last 500 remembered); scans start 10 min before the last one |

### Request policy

- One `TokenBucketRateLimiter` per endpoint family from `System.Threading.RateLimiting`, configured below the documented IP limits: match-history 2/min, live-url 1/h plus cached, metadata 10/min at low priority, hero-stats 30/min, analytics 60/min.
- `429` → honour `Retry-After`, exponential backoff capped at 5 min, no retry storms; `5xx` → 3 retries with jitter.
- Coalescing: `ScoutingService` collects cache misses for the whole roster and issues one bulk `hero-stats?account_ids=` call; concurrent requests for the same key share one in-flight task (`GetOrCreateAsync` holds a per-key semaphore).
- Priority queue: foreground (live URL, hero-stats for the current lobby) always runs before background (benchmark sampling, auto-ingest, session refresh); background work pauses while a match is live.
- Patch key: default is a date-based key rotated every 7 days; if the assets API exposes a client version, use it instead so caches invalidate on patch day.

## Overlay

The overlay is an ordinary top-level WPF window drawn over the game, never inside it. This keeps it VAC-safe and means it only works when Deadlock runs in **borderless windowed** mode; exclusive fullscreen hides every topmost window, and the app tells you so on first launch.

### Window technique

- `OverlayWindow`: `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`, `ShowInTaskbar=False`, `ShowActivated=False`.
- After `SourceInitialized`, set `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` via `SetWindowLong` so clicks pass through and it never steals focus.
- `GameWindowTracker` finds the Deadlock window by process (`deadlock.exe`, formerly `project8.exe`; window title "Deadlock"), polls `GetWindowRect` and `GetForegroundWindow` every 500 ms, and hides the overlay when the game is not foreground.
- Optional: `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` so the overlay stays out of your own clips and streams.
- `RegisterHotKey` toggles the overlay (default `Ctrl+Shift+O`) and shows or hides the lobby scout list (`Ctrl+Shift+L`; the list is hidden until then by default, since it covers a lot of the screen).

### Layout

| Panel | Shows | Default position |
| --- | --- | --- |
| Pace strip | Hero · game time · SPM₂ · expected · delta · percentile · provider dot; a sparkline of the delta over the last 10 minutes (±25% band); the personal line | Top centre, under the timer |
| Lane card | Lane opponent(s): games on hero, WR, rank, met-before, matchup WR | Top left |
| Scout list | All 11 players, two columns by team; flags for smurf, new-on-hero, stack | Toggle; shown automatically for the first 90 s after the roster arrives |
| Enemy pace board | Each enemy's net worth vs their hero's curve, sorted by lead; their latest power-spike item under the row, flashing for 30 s after the purchase | Right edge, compact |
| Build hint | The items most bought at your current net worth with the buyers' win rate; your next usual ability point and the ability usually maxed first | Bottom right, small |
| Session | Today's W/L (a day starts at 05:00 local), streak, rank change, loss-streak nudge, hero pool against the meta | Bottom left, only outside a match (Hideout and menus); also in the main window |

- Rendering: one `DispatcherTimer` at 1 Hz updates bound view models; a foreground-change event (`SetWinEventHook`) shows and hides the overlay at once on alt-tab. No per-frame work.
- Polling budget: nothing in the app runs faster than once a second. Log tailers 1 s, OCR 1 s (and only while OCR is on and the game is in front), overlay 1 s, calibration preview 1 s, broadcast fragments at the relay's 3 s publish cadence (1 s while catching up), hosted live query 10/min.
- Scale follows the game window height (1080p = 1.0) so panels land in the same HUD-relative spot at any resolution; positions are saved per resolution.
- Everything is a `UserControl` bound to a view model so the same panels can be shown in the main window for testing without the game running.

## Feature backlog

All agreed features, with the data each one needs and what it costs after caching. Milestone refers to the Build order below.

| Feature | Data source | Extra API calls | Milestone |
| --- | --- | --- | --- |
| Match detection (console.log + Steam content log) | Local | 0 | M1 |
| Account + hero detection | Local (`loginusers.vdf`, console.log) | 0 | M1 |
| Live roster via broadcast | live-url + SSE | 1 per match | M2 |
| Enemy scouting: games on hero, WR, total games, rank | hero-stats (bulk), feed rank | ≤1 per lobby | M2 |
| Teammate scouting | Same | 0 (same call) | M2 |
| Lane matchup card | Feed lane/team, hero-stats | 0 | M3 |
| Rank spread per team | Feed | 0 | M3 |
| Soul rate meter (2-min) | Feed ticks | 0 | M3 |
| Phase report (match history, post-match) | Each game's soul series against the rank gate's curve in laning (0–10), mid game (10–20) and late game (20+, never past the curve's last point), pooled over the page's games; the weakest phase needs 3 games behind | 0 beyond the rank gate's curves (the post-match report reads your rank from the cached history) | M6 |
| Rank gate (overlay line, soul timeline) | The benchmark curve for a whole rank tier (`RankGoal.Bracket`, e.g. Oracle = badges 80–89) on your hero, from the analytics provider's cached per-bracket call; the tier above yours by default, held at Alchemist unless `goal.includeLowRanks` | 8 analytics calls per tier per patch, shared by every hero | M6 |
| Pace benchmark, instant (analytics buckets) | analytics/hero-stats | \~8 per hero/bracket/patch | M3 |
| Pace benchmark, accurate (metadata sample) | match metadata | \~40 per hero/bracket/patch, background | M4 |
| Personal benchmark | Steam cache / metadata | 0–20 | M4 |
| Enemy pace board | Feed ticks + benchmark curves | 0 beyond benchmarks | M4 |
| Power-spike alerts | Direct feed's item list per player (`m_vecUpgrades`, whose tokens are the assets' item ids) and ultimate flag; spike set from `ItemSpikes` over the hero's item-stats table; the chip shows the item's icon | 1 analytics call per enemy hero, shared with the build hint's `items:` cache key; 1 icon per spike item, cached 30 days | M4 |
| Recurring opponents / teammates | enemy-stats, mate-stats | 1 per new player, cached 24 h | M4 |
| Smurf flag | hero-stats totals + feed rank | 0 | M4 |
| Matchup winrate | hero-counter-stats (whole bracket table) | 1 per bracket/patch | M4 |
| Item timing hint | item-stats bucketed by net worth at purchase (2,000-soul buckets) | 1 per hero/bracket/patch, warmed between matches | M5 |
| Ability order hint | ability-order-stats (most played full order, step times, first maxed) | 1 per hero/bracket/patch, warmed between matches | M5 |
| Post-match report | Match metadata from the API (cached forever once processed) + the benchmark curve at the match's average badge | 1 per match once processed; retried at 1, 2, 4, 8, 15, 30, 60, 120 min after the match | M5 |
| Match history | Your match history (shared 10 min cache) + bulk metadata for games not seen before: last hits, level, final items, both teams, your soul timeline and item purchase timeline; trends (rolling win rate and souls per minute, older half against newer half, per hero) over the last 100 history rows, computed at render time | 1 history call per 10 min, 1 bulk metadata call per opening with new games | M5 |
| Auto-ingest (salts) | Steam HTTP cache → `POST /v1/matches/salts` | 1 per run that finds new matches (after each match and hourly); 0 otherwise | M5 |
| Session: today's W/L, streak, rank change, loss-streak nudge | match-history (`ranked_display_badge` after each match; `players/mmr` is deprecated) | ≤1 per hourly refresh and after each match (10 min cache) | M5 |
| Hero pool vs meta | hero-stats (me, shared with scouting's cache) + analytics/hero-stats for your bracket | ≤2 per refresh, usually 0 (12 h and 1 day caches) | M5 |
| OCR sample source | Screen capture + OCR | 0 | M6 |
| Objective timers (urn, mid boss) | Direct feed entities: `CNPC_MidBoss` alive or not, the game rules' Rejuvenator counts, `CCitadelItemPickupIdol` on the map; times by the game clock so the broadcast delay does not shift them; the mid boss's return is estimated (no respawn timer in the broadcast) from the last observed delay in the match, else `objectives.midBossRespawnSeconds` (UNVERIFIED 7 min) | 0 | M6 |
| Pace sparkline | Meter samples + the curve: your 2-minute pace delta every 30 s over the last 10 min, computed when drawn | 0 | M6 |

Flags shown in the scout list:

- **New on hero**: fewer than 5 games on the hero
- **Smurf?**: fewer than 50 total games with rank above the lobby median, or hero WR above 70% on 10+ games
- **Stack**: party size above 1, when the feed or metadata exposes party ids (confirm in Open questions)
- **Met before**: any record in enemy-stats or mate-stats, with W–L

## Build order

&#91;embedded content: build order · M0–M6 with the gate between each\]

Each milestone ends with its gate checked on a real match, not a fixture. M3 is the first build worth running every game; everything after it adds depth without changing the interfaces.

## Risks & constraints

| Risk | Impact | Mitigation |
| --- | --- | --- |
| VAC / anti-cheat | Account ban | No process access, no injection, no input; external window and file reads only. Document this in the README. |
| Broadcast delay (60–90 s) | Meter and roster lag | Show sample time on the overlay; OCR source later for real-time souls |
| Console log format changes | Detection breaks on a patch | Regexes in one `ConsoleLogPatterns` class with fixture tests replayed from real logs; fallback to Steam content log |
| API coverage gaps | Enemies with few recorded games look new | Show sample size ("12 recorded") rather than a bare count; mark "unknown" below 3 recorded matches |
| Rate limits | 429s during a lobby | Per-endpoint limiters below the documented caps, bulk calls, background pause during matches |
| API outage or schema change | Scouting unavailable | Typed DTOs with tolerant JSON (`JsonExtensionData`), cached data served stale with an "offline" badge |
| Patch churn | Stale benchmarks and item names | Patch key invalidation; assets refresh on unknown ids |
| Protected / private players | Missing stats for some accounts | Analytics endpoints already drop protected users; show "private" instead of 0 |
| Borderless windowed requirement | Overlay invisible in fullscreen | First-run check reads the game's video settings and warns |
| Fairness and ToS | Community norms | Only public data from the delayed broadcast and files the game writes; no voice, no live chat scraping |

## Configuration

`%LOCALAPPDATA%\DeadlockScout\settings.json`, bound to a `ScoutSettings` record via `IOptionsMonitor` so edits apply without restart.

```json
{
  "steamPath": null,
  "deadlockPath": null,
  "accountId": null,
  "api": {
    "baseUrl": "https://api.deadlock-api.com",
    "assetsUrl": "https://assets.deadlock-api.com",
    "apiKey": null,
    "limits": { "matchHistoryPerMin": 2, "heroStatsPerMin": 30, "metadataPerMin": 10, "analyticsPerMin": 60 }
  },
  "cache": { "playerStatsTtlHours": 12, "benchmarkTtlDays": 7, "patchKey": "auto" },
  "meter": { "windowSeconds": 120, "soulSource": "Auto", "benchmarkBracket": "auto", "bracketWidth": 10, "prefetchHeroes": 3, "prefetchIntervalMinutes": 30 },
  "ocr": { "enabled": false, "engine": "WindowsMediaOcr", "regions": { "souls": { "x": 0.47, "y": 0.925, "width": 0.06, "height": 0.035 }, "timer": null, "enemySouls": [] }, "minHealthy": 0.8 },
  "overlay": {
    "enabled": true,
    "hotkeyToggle": "Ctrl+Shift+O",
    "hotkeyLayout": "Ctrl+Shift+L",
    "excludeFromCapture": true,
    "panels": { "pace": true, "lane": true, "scout": "auto", "enemyPace": true, "sparkline": true, "personal": true, "build": true, "session": true },
    "positions": {}
  },
  "flags": { "newOnHeroMaxGames": 5, "smurfMaxTotalGames": 50, "smurfMinWinRate": 0.7 },
  "ingest": { "autoSubmitSalts": true },
  "session": { "dayStartsAtHour": 5, "lossStreakNudge": 3, "heroPoolSize": 5, "refreshHours": 1 },
  "launch": { "ensureCondebug": true },
  "goal": { "rank": "next", "includeLowRanks": false, "overlay": true }
}
```

`benchmarkBracket: auto` reads your current badge from the feed or `players/mmr` and uses ± `bracketWidth` badge levels. `ensureCondebug` offers to add `-condebug` to the Steam launch options (it edits `localconfig.vdf` only while Steam is closed, otherwise it shows instructions).

## Open questions

Check each against the live OpenAPI at `https://api.deadlock-api.com/docs` and one real match before building on it.

- [ ] Exact JSON shape of the SSE live feed: entity names, whether `CCitadelPlayerController` carries lane and party ids, and the game-clock field
- [ ] Does `/v1/matches/{id}/live/url` work for every public match or only ingested ones?
- [ ] Net-worth sample cadence in match metadata (per minute?) and the field names in `players[].stats[]`
- [ ] Does `analytics/hero-stats` accept `min_duration_s`/`max_duration_s` together with `hero_ids` and badge filters in one query?
- [ ] Current per-endpoint rate limits and whether an API key is free; where to request one
- [ ] Is there a client/patch version in the assets API to key caches on?
- [ ] Console.log lines for hero lock, lane assignment and match end on the current build (collect a fixture log)
- [ ] Exact Steam HTTP cache layout for match metadata on the current client
- [ ] Does ETag / If-None-Match work on any endpoint?
- [ ] Confirm Deadlock's process name and window title on the current build
