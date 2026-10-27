# DeadlockScout

A local Windows tray app (C#, .NET 10, WPF) that detects your Deadlock match from the game's own log files, scouts the lobby through the community deadlock-api.com service, and draws a click-through overlay with your souls-per-minute against the pace of the hero you are playing. See `docs/architecture-seed.md` for the founding design document and `docs/verification/` for what has been checked against the live API and a real match.

## Hard rules (VAC safety and fairness)
1. Never touch the game process: no memory reads, no injection, no DirectX hooks, no input automation. Only files the game and Steam write on purpose, the registry key for the Steam path, screen capture of our own window region, and public HTTP APIs.
2. The overlay is an ordinary top-level transparent window. It only works with the game in borderless windowed mode; the app says so, it never tries to work around it.
3. Only data from the delayed public broadcast and public match records. No voice, no chat scraping.
4. Never commit real logs, account ids, API keys or cache databases. Fixtures under `tests/DeadlockScout.Tests/Fixtures` are synthetic or scrubbed.

## Layout
- `src/DeadlockScout.Core` contracts (`Abstractions/`), records (`Models/`), and pure logic: `SoulRateMeter`, `PaceMath`, `CurveBuilder`, `CompositeBenchmark`, `CompositeMatchDetector`, `ScoutingService`/`ScoutRules`, `ScoutSettings`, `CacheKeys`. No I/O, no HttpClient, no WPF.
- `src/DeadlockScout.Steam` local adapters: `ConsoleLogTailer` (primary match signal), `SteamContentLogWatcher` (fallback), `SteamLocator` (registry, loginusers.vdf, libraryfolders.vdf), `LaunchOptions` (-condebug), `VdfParser`, `SteamHttpCache`.
- `src/DeadlockScout.Api` the only project with an `HttpClient`: `DeadlockApiClient`, rate limiting and retry handlers, two-layer cache (memory + SQLite), live feed (`DirectBroadcastStream` reading Valve's broadcast relay with DemoFile by default, `HostedLiveQueryStream` over api.deadlock-api.com, `SseLiveEventStream` for the self-hosted live-events container, chosen by `LiveEventStreamSelector`, which falls back from Direct to Hosted on a parse failure), benchmark providers.
- `src/DeadlockScout.Ocr` the OCR seam (`IRegionCapture`, `IHudRegionMap`, `IOcrEngine`, `OcrSoulSource`) and `RegionCalibration`, the calibration tool's model. Regions are fractions of the game's client area; only those rectangles are ever captured. The engine arrives next in M6.
- `src/DeadlockScout.App` the WPF tray app, overlay window and panels, DI wiring, `Calibration/CalibrationWindow` (the OCR region tool) and `ScreenRegionCapture` (GDI BitBlt of one region; our windows set WDA_EXCLUDEFROMCAPTURE so they never appear in it).
- `third_party/demofile-net` vendored, patched DemoFile (MIT) used by the Direct feed. Don't edit it casually: every local change is listed in its `VENDORED.md` and kept as a patch file there.
- `tests/DeadlockScout.Tests` xunit; runs on Linux and Windows.
- `tools/DeadlockScout.ReplayCheck` runs a replay through the Direct feed's snapshot code (`BroadcastSnapshotter`) and prints roster, net worth by minute and a clock check. Use it after a game patch to see whether DemoFile still decodes the game. Replays are gitignored: they hold every player's name and account id.

## Rules every project obeys
1. Seams stay clean: `ISoulSampleSource` implementations know nothing about the overlay or the benchmark; `IPaceBenchmark` providers are stateless over `ICache`; only the Api project holds `HttpClient`s (`DeadlockApiClient` and the two live-feed streams), and every request to api.deadlock-api.com goes through the API key and rate-limit handlers; `IMatchDetector` merges signal sources and dedupes.
2. Every API call goes through the per-endpoint rate limiter and the cache; limits stay below the documented per-IP caps (`RateLimitSettings`). Add a key to `CacheKeys` and a row to the caching table in the design doc when you add a cached call.
3. All regexes that read a game or Steam log live in `ConsoleLogPatterns` / `SteamContentLogWatcher` and have a fixture test. Patterns not yet confirmed from a captured log are marked UNVERIFIED in a comment; replace them from a real fixture, never guess silently.
4. DTOs are tolerant: unknown JSON fields are kept in `JsonExtensionData`, missing fields become null, never an exception for the overlay.
5. Computed values (SPM, delta, percentile) are never stored; they are derived from samples and curves at render time.
6. Background work (benchmark sampling, auto-ingest, session refresh) pauses while a match is live.

## Commands
- `dotnet build` builds everything, including the WPF app on Linux thanks to `EnableWindowsTargeting`. Needs the .NET 10 SDK (`global.json` pins the 10.0 band); Ubuntu 24.04's `dotnet-sdk-10.0` package includes the WindowsDesktop targets.
- `dotnet test` runs the unit tests.
- `dotnet run --project src/DeadlockScout.App` on Windows starts the tray app.
- `dotnet publish src/DeadlockScout.App -c Release -r win-x64 --self-contained false` builds the distributable.
- `dotnet run --project src/DeadlockScout.App -- --screenshots docs/screenshots` (Windows only) renders the README screenshots from the demo lobby. The Screenshots workflow does this on a Windows runner for every non-main push that touches the app and commits the images; pull before pushing again. Look at the images before merging. Shots never show game art or real players.

## Milestones
M0 scaffold · M1 match detection · M2 roster and scouting · M3 soul meter, instant benchmark, overlay · M4 accurate/personal benchmarks, enemy pace, flags, matchups · M5 build hints, post-match report, ingest, session · M6 OCR. Each gate is checked on a real match, not a fixture; see the build order in the design doc.
