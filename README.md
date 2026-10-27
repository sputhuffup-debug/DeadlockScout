# DeadlockScout

A self-improvement overlay for [Deadlock](https://store.steampowered.com/app/1422450/). It detects your match the moment the lobby forms, scouts the players, and shows your souls-per-minute against the pace of your hero at your rank, as a click-through window over the game.

- **Match detection** from the game's own `console.log` (with `-condebug`) and Steam's logs. No API calls.
- **Lobby scouting**: games on hero, win rate, rank, players you have met before, smurf and stack flags.
- **Soul-rate meter**: 2-minute rolling souls per minute against a per-hero, per-rank pace curve, with percentile when the data supports it.
- **Enemy pace board, lane card, build hints, post-match report, session view** (see the milestones in [`docs/architecture-seed.md`](docs/architecture-seed.md)).

## Safety

DeadlockScout never touches the game process. It reads files the game and Steam write on purpose, draws an ordinary transparent window on top of the game, and talks to the public [deadlock-api.com](https://deadlock-api.com) service. No memory reading, no injection, no hooks, no input automation. The overlay requires **borderless windowed** mode; exclusive fullscreen hides every external window.

All match data comes from Valve's public spectator broadcast, which runs 60–90 seconds behind real time. The overlay labels every number with the game time it refers to.

## Status

Pre-alpha. The solution builds and the pure logic is unit tested; the overlay has not yet been run against a live match. `docs/verification/` records what has been checked against the live API and what is still an open question.

## Building

Requires the .NET 8 SDK. `dotnet build` and `dotnet test` work on Windows, Linux and macOS (the WPF app builds everywhere with `EnableWindowsTargeting`, and runs on Windows 10/11).

```
dotnet build
dotnet test
dotnet run --project src/DeadlockScout.App     # Windows
```

Settings live in `%LOCALAPPDATA%\DeadlockScout\settings.json` and the cache in `cache.db` next to it. An API key is optional; set `api.apiKey` to raise your quota.

## Licence

BSD 3-Clause, see [LICENSE](LICENSE).
