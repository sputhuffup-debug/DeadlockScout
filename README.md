# DeadlockScout

A self-improvement overlay for [Deadlock](https://store.steampowered.com/app/1422450/). It detects your match the moment the lobby forms, scouts the players, and shows your souls-per-minute against the pace of your hero at your rank, as a click-through window over the game.

- **Match detection** from the game's own `console.log` (with `-condebug`) and Steam's logs. No API calls.
- **Lobby scouting**: games on hero, win rate, rank, players you have met before, smurf and stack flags.
- **Soul-rate meter**: 2-minute rolling souls per minute against a per-hero, per-rank pace curve, with percentile when the data supports it, and a sparkline of your pace over the last ten minutes.
- **Lane matchups**: your hero's lane win rate against each lane opponent's hero at your rank.
- **Build hint**: what your hero's players buy at your current souls, with their win rate, and your next usual ability point.
- **Real-time souls by OCR** (M6, opt in): draw boxes over your soul count, the match clock and the enemy scoreboard souls, and the built-in Windows OCR reads them once a second, so your pace no longer lags the broadcast by a minute. Only those boxes are ever captured, never the rest of the screen, and nothing while OCR is off. Needs Windows 10 2004 or later and a Windows language with OCR support (English is).
- **Post-match report**: result, K/D/A, souls per minute, where you ranked on your team, your pace against the benchmark at 10, 20 and 30 minutes, and how your lane went, shown in the main window and as a tray notice once deadlock-api has the match. Replay salts from your Steam cache are sent to deadlock-api automatically (once per match; `ingest.autoSubmitSalts`) so your matches get on record.
- **Match history**: your last 20 games with your rank over 30 days, win rate, KDA, main heroes and the last 15 results, then a card per game (result, hero, K/D/A, final items, souls and souls per minute, last hits, where your souls ranked on your team, both teams). Open a game for its **soul timeline** (your souls through the match, with every item you bought marked where you bought it, coloured by slot) and its **item purchases** in order, sold ones included. Above the cards, **trends** over your last 20, 50 or 100 games, for all heroes or one: rolling win rate and souls per minute (each game a dot coloured by result), the older half against the newer half for win rate, souls per minute and KDA, and a row per hero (games, W–L, win rate, KDA, souls per minute, last played), all from the history already fetched. One bulk call fetches the games not seen before; a finished game is cached for good. From the main window or the tray.
- **Session view**: today's wins and losses, your streak, how your rank moved, a nudge after three losses in a row, and your most played heroes against their meta win rate at your rank. In the overlay only outside a match, and in the main window.
- **Benchmarks ready at match start**: between matches the app prefetches the accurate and personal pace curves for the heroes you play most.
- **Enemy pace board and lane card**, with **power-spike alerts**: when an enemy buys a 6,400-soul item, one of the items that lift their hero's win rate most at your rank, or unlocks their ultimate, their row flashes "just got Leech" for 30 seconds and then keeps the item as a chip with its icon. Items come from the Direct live feed (the hosted feed has none); `overlay.panels.spikes` turns it off (see the milestones in [`docs/architecture-seed.md`](docs/architecture-seed.md)).
- **Objectives**: when the mid boss died, who took its Rejuvenator and when it should be back, each team's Rejuvenator count, and when the urn appeared or was picked up. Times are game-clock times, so they line up with the in-game timer despite the broadcast delay. The broadcast has no respawn timers, so the mid boss's return is an estimate: 7 minutes until the app has seen one respawn in the match, then the observed delay (`objectives.midBossRespawnSeconds`; each observed delay is logged). Direct live feed only; `overlay.panels.objectives` turns it off.

## Screenshots

The overlay at 1080p, drawn over a plain backdrop in place of the game: lane card with your lane matchup win rate, pace strip with a second line for your own recent games on the hero, lobby scout list, enemy pace board (one enemy has just bought a spike item), objectives (the enemy took the mid boss at 11:20; the urn is out) and build hint.

![The overlay's panels at 1080p with a demo lobby](docs/screenshots/overlay.png)

Power-spike alerts on the enemy pace board, at twice the size: Seven has just bought Leech, so their chip is filled for 30 seconds; Vindicta's Headhunter, Paradox's Mystic Reverb and Lady Geist's ultimate stay on as quiet chips. Chips are coloured by kind: red for the ultimate, orange for weapon, blue for spirit, green for vitality. In the app each chip shows the item's icon; screenshots show its initial instead, because they never show game art.

![Close-up of the enemy pace board with a flashing "just got Leech" alert and two item chips](docs/screenshots/enemy-spikes.png)

The preview doubles as the layout editor: every movable panel gets a gold outline and can be dragged anywhere.

![The overlay preview with outlined panels and its Reset and Stop preview bar](docs/screenshots/overlay-editor.png)

The match history with demo games: the summary row, the trends over 50 games, then a card per game; the first is open on its soul timeline and item purchases. Item and hero squares show initials here, because screenshots never show game art; the app shows the icons.

![The match history window with demo games and an opened soul timeline](docs/screenshots/match-history.png)

The app's own window frame: a dark title bar with the gold-ruled DeadlockScout name and flat caption buttons (close turns red), and slim scroll bars that widen and turn gold under the mouse, in place of Windows' white caption and grey bars. Snapping, resizing and double-click to maximise still work; on Windows 11 the corners stay round.

![The main window as it shows on screen, with its dark title bar and slim scroll bar](docs/screenshots/window-frame.png)

The main window, with detection status, the Direct/Hosted live feed switch and the same demo lobby, spike chips included.

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
   - To check that the parser understands the current patch, run it on one of your replays or on a live broadcast URL (it also lists every objective change it reads, which is how the mid boss and urn reads get confirmed):
     ```
     dotnet run --project tools/DeadlockScout.ReplayCheck -- path\to\match.dem
     ```
3. Start DeadlockScout; it sits in the tray. Starting it mid-match is fine: it picks up a match already in progress from the game's log. Closing the main window hides it to the tray; **Exit** (in the window or the tray menu) quits, as does Ctrl+C in the terminal you started it from. Set Deadlock to **borderless windowed**.
4. To check the overlay outside a match, click **Preview overlay** in the main window (or the tray menu): it shows the overlay without the game in front, over your primary screen if the game is not running, filled with the demo lobby. Click **Stop preview** to go back to normal.
5. To move the overlay's panels, turn on **Preview overlay**. Only during a preview does the overlay take the mouse (over its panels; the rest of the screen still gets your clicks); in a match it is always click-through. The pace strip, lane card, scout list, enemy board, objectives, build hint and session card get a gold outline, and you drag them where you want. Each drop snaps to an 8 px grid, stays on screen, and is saved in `settings.json` (`overlay.positions`) for that screen shape, so a 1440p and an ultrawide layout are kept apart. **Reset to default** puts this screen shape's panels back; **Stop preview** (on the overlay or in the main window) ends it and makes the overlay click-through again.
6. Spectating works too: watch a live match from the Watch tab and the overlay shows every player's pace against their hero's curve (tagged A or S for the team) and, on Ctrl+Shift+L, the lobby stats. Panels about your own game stay empty, and no post-match report is made for someone else's match.
7. Hotkeys while you play: **Ctrl+Shift+L** shows or hides the lobby stats (the scout list; also in the tray menu), and **Ctrl+Shift+O** hides or shows the whole overlay. Both can be changed in `settings.json` (`overlay.hotkeyLayout`, `overlay.hotkeyToggle`); `overlay.panels.scout` set to `auto` brings back the scout list for the first 90 seconds of each match.

## Status

Pre-alpha. The solution builds on Windows and Linux and the logic is covered by unit tests. It has been run through real matches: match detection from console.log (start, end screen, leaving, starting the app mid-match), the Direct broadcast feed for a whole match, scouting, and the overlay over the game. [`docs/verification/api.md`](docs/verification/api.md) records what was checked against the API's source and real matches, what the design doc got wrong, and what is still open.

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
