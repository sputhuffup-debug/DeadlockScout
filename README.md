# DeadlockScout

A self-improvement overlay for [Deadlock](https://store.steampowered.com/app/1422450/). It notices your match the
moment the lobby forms, tells you who you are playing with and against, and shows how fast you are earning souls
against players of your rank on your hero, in a click-through window over the game. After the match it shows where
the souls went.

![The overlay's panels at 1080p with a demo lobby](docs/screenshots/overlay.png)

## What you get

- **Lobby scouting**: each player's rank, games and win rate on their hero, players you have met before, likely
  parties, possible smurfs, and the item that counters an enemy's hero when one clearly does. One line per team
  compares the two lobbies.
- **Your pace, live**: souls per minute against the average of your rank on your hero, the rank above as the gate to
  beat, a sparkline of the last ten minutes, and how laning, mid game and late game are going.
- **Enemy pace and power spikes**: every enemy's souls against yours, a flash when one buys a big item or unlocks
  their ultimate, and the item they usually buy next.
- **Laning timers and objectives**: the jungle's first spawns with a "push now" nudge, the mid boss's return and the
  urn as countdowns, and optional sound cues.
- **Match history**: every game with its soul timeline against your rank's, items and their timings, where your souls
  came from, your deaths and what you were carrying, trends over 20 to 100 games, goals you set yourself, and a weekly
  summary. A game you just played shows straight away, even before it is on record.

Everything else, in detail and with every screenshot: [docs/features.md](docs/features.md).

![The match history window with demo games and an opened soul timeline](docs/screenshots/match-history.png)

## Is it safe?

DeadlockScout never touches the game. It reads only the log files that Deadlock and Steam write on purpose, draws an
ordinary transparent window on top of the game, and fetches public data from [deadlock-api.com](https://deadlock-api.com).
No memory reading, no injection, no hooks into the game's graphics, no input automation, nothing a cheat would do.

Everything it knows about a live match comes from Valve's public spectator broadcast, which runs 30–60 seconds
behind the game; the overlay labels its numbers with the game time they belong to. With OCR switched on it also reads
your own soul count and the clock from two small boxes of your screen, nothing else.

## What you need

- Windows 10 (version 2004 or later) or Windows 11, 64-bit.
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
- Deadlock in **Fullscreen windowed (borderless)** or windowed mode. Nothing can draw over exclusive fullscreen.
- `-condebug` in Deadlock's launch options. The setup wizard adds it for you.

## Install

There is no installer yet. Until 1.0:

1. Open the [latest successful CI run on main](https://github.com/sputhuffup-debug/DeadlockScout/actions/workflows/ci.yml?query=branch%3Amain+is%3Asuccess)
   (you need to be signed in to GitHub) and download the **DeadlockScout-win-x64** artifact at the bottom of the page.
2. Unzip it into a folder of its own, for example `C:\Games\DeadlockScout`.
3. Run **DeadlockScout.exe**. Windows may warn that it is from an unknown publisher: it is not signed yet.

To build it yourself instead, see [docs/development.md](docs/development.md).

## First start

The **setup wizard** opens on the first start (and from the tray menu later). It finds Steam, Deadlock and your
account, adds `-condebug` to Deadlock's launch options while Steam is closed, checks the game's display mode, offers OCR
with its calibration tool, and ends on a preview of the overlay. Restart Deadlock afterwards so `-condebug` takes effect.

DeadlockScout then sits in the tray. Closing its window only hides it; **Exit** in the window or the tray menu quits.
Starting it in the middle of a match is fine.

To see the overlay without playing, click **Preview overlay** in the main window: it fills the overlay with a made-up
lobby, and you can drag the panels where you want them and set their size.

## While you play

- **Ctrl+Shift+L** shows or hides the lobby stats (scout list and lane card).
- **Ctrl+Shift+O** hides or shows the whole overlay.
- The overlay never takes your clicks during a match.
- Both hotkeys, every panel and every option are in **Settings** (main window or tray).
- **Spectating** works too: watch a match from the Watch tab and the overlay shows every player's pace.

## Privacy

- DeadlockScout sends no telemetry and has no account of its own.
- It asks deadlock-api.com about the players in your lobby (by their public account ids) and about your own matches,
  and fetches item and hero art from deadlock-api's asset server and the match broadcast from Valve's relay.
- Everything it keeps stays on your PC, under `%LOCALAPPDATA%\DeadlockScout`: your settings, a cache of what it
  fetched, and a log (a week's worth).
- **Copy diagnostics** (for a bug report) leaves out account ids, player names, your Windows user name and your API
  key.

## Something wrong?

See [docs/troubleshooting.md](docs/troubleshooting.md): the app does not notice a match, the overlay does not show,
numbers stay empty, a game is missing from the history, and how to report a bug.

## Status

Pre-1.0. It has been played with through real matches, from match detection to the post-match history, and the
remaining work to 1.0 is in [docs/roadmap.md](docs/roadmap.md). For how it is built and tested, and the tools that
check it after a game patch, see [docs/development.md](docs/development.md).

## Licence

BSD 3-Clause, see [LICENSE](LICENSE). Not affiliated with Valve. Match data from [deadlock-api.com](https://deadlock-api.com).
