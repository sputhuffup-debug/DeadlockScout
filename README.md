# DeadlockScout

A self-improvement overlay for [Deadlock](https://store.steampowered.com/app/1422450/). It detects your match the moment the lobby forms, scouts the players, and shows your souls-per-minute against the pace of your hero at your rank, as a click-through window over the game.

- **Match detection** from the game's own `console.log` (with `-condebug`) and Steam's logs. No API calls.
- **Lobby scouting**: games on hero, win rate, rank, players you have met before, smurf and stack flags.
- **Soul-rate meter**: 2-minute rolling souls per minute against a per-hero, per-rank pace curve, with percentile when the data supports it, and a sparkline of your pace over the last ten minutes.
- **Lane matchups**: your hero's lane win rate against each lane opponent's hero at your rank.
- **Build hint**: what your hero's players buy at your current souls, with their win rate, and your next usual ability point.
- **Real-time souls by OCR** (M6, opt in): draw boxes over your soul count, the match clock and the enemy scoreboard souls, and the built-in Windows OCR reads them once a second, so your pace no longer lags the broadcast by a minute. Only those boxes are ever captured, never the rest of the screen, and nothing while OCR is off. Needs Windows 10 2004 or later and a Windows language with OCR support (English is).
- **Post-match report**: result, K/D/A, souls per minute, where you ranked on your team, your pace against the benchmark at 10, 20 and 30 minutes, and how your lane went, shown in the main window and as a tray notice once deadlock-api has the match. Replay salts from your Steam cache are sent to deadlock-api automatically (once per match; `ingest.autoSubmitSalts`) so your matches get on record.
- **Session view**: today's wins and losses, your streak, how your rank moved, a nudge after three losses in a row, and your most played heroes against their meta win rate at your rank. In the overlay only outside a match, and in the main window.
- **Benchmarks ready at match start**: between matches the app prefetches the accurate and personal pace curves for the heroes you play most.
- **Enemy pace board and lane card** (see the milestones in [`docs/architecture-seed.md`](docs/architecture-seed.md)).

## Screenshots

The overlay at 1080p, drawn over a plain backdrop in place of the game: lane card with your lane matchup win rate, pace strip with a second line for your own recent games on the hero, lobby scout list, enemy pace board and build hint.

![The overlay's panels at 1080p with a demo lobby](docs/screenshots/overlay.png)

The main window, with detection status, the Direct/Hosted live feed switch and the same demo lobby.

![The DeadlockScout main window with a demo lobby](docs/screenshots/main-window.png)

The OCR region tool, with example boxes over a plain backdrop. On a real machine it sits over the game, and the preview shows the live pixels of the selected box and what OCR reads from them.

![The OCR region calibration tool with example boxes](docs/screenshots/ocr-calibration.png)

The overlay and main window shots use the app's demo lobby, with made-up player names. The Screenshots workflow renders them on a Windows runner (`DeadlockScout --screenshots <folder>`) whenever a branch changes the app, and commits them to that branch.

## Safety

DeadlockScout never touches the game process. It reads files the game and Steam write on purpose, draws an ordinary transparent window on top of the game, and talks to the public [deadlock-api.com](https://deadlock-api.com) service. No memory reading, no injection, no hooks, no input automation. The overlay requires **borderless windowed** mode; exclusive fullscreen hides every external window.

All match data comes from Valve's public spectator broadcast, which runs 60–90 seconds behind real time. The overlay labels every number with the game time it refers to.

## Setup

1. Add `-condebug` to Deadlock's launch options (Steam → Deadlock → Properties → Launch options), or use the app's **Add -condebug** button while Steam is closed. The console log is how the app notices your match.
2. Nothing to install for the live match feed. By default (`api.liveSource: "direct"` in settings) the app reads Valve's delayed public broadcast itself with the [DemoFile](https://github.com/saul/demofile-net) parser, vendored with fixes for the current patch (see `third_party/demofile-net/VENDORED.md`). Only the broadcast address comes from deadlock-api. If a game patch breaks parsing, the app switches that match to deadlock-api's hosted live query automatically.
   - `"hosted"` uses the hosted live query from the start.
   - The main window has a **Live feed: Direct / Hosted** switch that writes this setting. A change applies from the next match.
   - `"selfHosted"` uses the deadlock-api live-events container you run yourself, expected at `http://localhost:3000` (`api.liveUrl`). Setup guide: [live-events README](https://github.com/deadlock-api/deadlock-api/tree/master/live-events); image: [ghcr.io/deadlock-api/deadlock-live-events](https://github.com/orgs/deadlock-api/packages/container/package/deadlock-live-events).
     ```
     docker run -d --restart unless-stopped -p 3000:3000 ghcr.io/deadlock-api/deadlock-live-events:latest
     ```
   - The app writes every setting into `settings.json` on first run, so an install from before this default keeps `"liveSource": "hosted"` until you change it with the switch or in the file.
   - To check that the parser understands the current patch, run it on one of your replays or on a live broadcast URL:
     ```
     dotnet run --project tools/DeadlockScout.ReplayCheck -- path\to\match.dem
     ```
3. Start DeadlockScout; it sits in the tray. Closing the main window hides it to the tray; **Exit** (in the window or the tray menu) quits, as does Ctrl+C in the terminal you started it from. Set Deadlock to **borderless windowed**.
4. To check the overlay outside a match, click **Preview overlay** in the main window (or the tray menu): it shows the overlay without the game in front, over your primary screen if the game is not running, filled with the demo lobby. Click **Stop preview** to go back to normal.

## Status

Pre-alpha (milestones M0–M3 of the design doc are code-complete). The solution builds on Windows and Linux and the logic is covered by unit tests; the overlay has **not yet been run against a live match**. [`docs/verification/api.md`](docs/verification/api.md) records what was checked against the API's source, what the design doc got wrong, and what still needs a real match (above all the console.log line formats, which are marked UNVERIFIED in `ConsoleLogPatterns`).

## Building

Requires the .NET 10 SDK (10.0.100 or later; `global.json` pins the 10.0 band). `dotnet build` and `dotnet test` work on Windows, Linux and macOS (the WPF app builds everywhere with `EnableWindowsTargeting`, and runs on Windows 10/11).

```
dotnet build
dotnet test
dotnet run --project src/DeadlockScout.App     # Windows
```

Settings live in `%LOCALAPPDATA%\DeadlockScout\settings.json` and the cache in `cache.db` next to it. An API key is optional; set `api.apiKey` to raise your quota.

## Licence

BSD 3-Clause, see [LICENSE](LICENSE).
