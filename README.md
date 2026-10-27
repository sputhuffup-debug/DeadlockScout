# DeadlockScout

A self-improvement overlay for [Deadlock](https://store.steampowered.com/app/1422450/). It detects your match the moment the lobby forms, scouts the players, and shows your souls-per-minute against the pace of your hero at your rank, as a click-through window over the game.

- **Match detection** from the game's own `console.log` (with `-condebug`) and Steam's logs. No API calls.
- **Lobby scouting**: games on hero, win rate, rank, players you have met before, smurf and stack flags.
- **Soul-rate meter**: 2-minute rolling souls per minute against a per-hero, per-rank pace curve, with percentile when the data supports it.
- **Enemy pace board, lane card, build hints, post-match report, session view** (see the milestones in [`docs/architecture-seed.md`](docs/architecture-seed.md)).

## Safety

DeadlockScout never touches the game process. It reads files the game and Steam write on purpose, draws an ordinary transparent window on top of the game, and talks to the public [deadlock-api.com](https://deadlock-api.com) service. No memory reading, no injection, no hooks, no input automation. The overlay requires **borderless windowed** mode; exclusive fullscreen hides every external window.

All match data comes from Valve's public spectator broadcast, which runs 60–90 seconds behind real time. The overlay labels every number with the game time it refers to.

## Setup

1. Add `-condebug` to Deadlock's launch options (Steam → Deadlock → Properties → Launch options), or use the app's **Add -condebug** button while Steam is closed. The console log is how the app notices your match.
2. Nothing to install for the live match feed. By default (`api.liveSource: "direct"` in settings) the app reads Valve's delayed public broadcast itself with the [DemoFile](https://github.com/saul/demofile-net) parser, vendored with fixes for the current patch (see `third_party/demofile-net/VENDORED.md`). Only the broadcast address comes from deadlock-api. If a game patch breaks parsing, the app switches that match to deadlock-api's hosted live query automatically.
   - `"hosted"` uses the hosted live query from the start.
   - `"selfHosted"` uses the deadlock-api live-events container you run yourself, expected at `http://localhost:3000` (`api.liveUrl`). Setup guide: [live-events README](https://github.com/deadlock-api/deadlock-api/tree/master/live-events); image: [ghcr.io/deadlock-api/deadlock-live-events](https://github.com/orgs/deadlock-api/packages/container/package/deadlock-live-events).
     ```
     docker run -d --restart unless-stopped -p 3000:3000 ghcr.io/deadlock-api/deadlock-live-events:latest
     ```
   - The app writes every setting into `settings.json` on first run, so an install from before this default keeps `"liveSource": "hosted"` until you change it.
   - To check that the parser understands the current patch, run it on one of your replays or on a live broadcast URL:
     ```
     dotnet run --project tools/DeadlockScout.ReplayCheck -- path\to\match.dem
     ```
3. Start DeadlockScout; it sits in the tray. Set Deadlock to **borderless windowed**.

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
