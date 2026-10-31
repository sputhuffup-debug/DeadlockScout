# Roadmap to 1.0

M0–M6 built the app; this plan takes it to a mostly feature-complete 1.0. Each line is one PR. After a merge, the next
unchecked line in the current milestone is the next PR; nobody has to brainstorm. Reorder or cut lines here, in a PR
of their own, when priorities change.

Rules for every line:

- It follows `CLAUDE.md`: VAC-safe (files the game writes, public APIs, screen capture of our own boxes only),
  computed values never stored, every API call cached and rate limited.
- A user-facing line ships with its screenshot from demo data and a line in `docs/features.md` (the README too when it
  is a headline feature).
- Anything the line assumes about the game or the API that no real match or replay has confirmed is marked
  UNVERIFIED in code and listed under "Still open" in `docs/verification/api.md`.
- A milestone is done when its gate passes on a real match, not on a fixture.

## Done

- **M0–M6** (see the build order in `docs/architecture-seed.md`): match detection, scouting, soul meter and
  benchmarks, enemy pace, flags and matchups, build hints, post-match report, ingest, session, OCR.
- **After M6**: demo/live state fix, objective timers, match history with trends, themed window frame and controls,
  rank gate (timeline and overlay), phase report, item timings vs rank, live phase tracker.

## M7 · Laning and macro timers

Everything that tells you *when* to do something during a match.

- [x] Laning timers: jungle first spawns with a 30 s "push now" warning, on a live clock (OCR, else the console
  log's game start, else the broadcast plus a delay). sputhuffup-debug/DeadlockScout#43.
- [x] Verify the timers on a real match. The clock anchor is verified (verification note 21), and a replay's jungle
  confirms all four first spawns (note 38), so the defaults stand unchanged.
- [x] Objective countdowns on the live clock: the mid boss's estimated return and the urn as "in 1:40" countdowns
  (today they are game-clock times), with the same gold lead as the timers.
- [ ] Camp respawn timers, only if the replay shows the broadcast carries neutral camps: a camp's clear seen in the
  broadcast starts its respawn countdown (small 1:25, medium 4:50, large 5:35, UNVERIFIED), limited to the camps
  nearest your lane. Logs each observed respawn to verify the intervals. *Oct 31: the replay shows the broadcast
  carries the camps (note 38), so this is buildable.*
- [x] Next item nudge: your usual next big item on this hero (from your history) and when the rank gate's players
  have it ("Headhunter: Oracle has it by 12:07, in 1:40"), from the item timings table fetched between matches.
- [x] Audio cues (opt-in, our own sounds, never the game's): push now, mid boss soon, pace under the gate for a
  minute. One switch per cue in settings.

**Gate:** in one real match, the push-now warnings arrive within 5 s of 30 s before each spawn on the in-game
timer, and the objective countdowns match what the game shows.

## M8 · Settings and first run

`settings.json` now holds over forty options; nobody should need to edit it by hand.

- [x] Settings window: overlay panels on/off, rank gate (rank, Initiate & Seeker), timers (lead, spawns), live feed,
  OCR, hotkeys. Writes `settings.json` through the existing monitor, so every change applies live.
- [x] First-run wizard: finds Steam and the account, adds `-condebug`, checks borderless windowed mode, offers OCR
  calibration, shows the demo lobby so the player knows what to expect.
- [x] Overlay layout presets and a panel size slider. The overlay already scales with the game window (1080p = 1.0)
  and keeps positions per screen shape, so presets are whole layouts rather than per-resolution copies: Full, Laning,
  Minimal and any saved by name, each with its panels, positions and size.
- [x] Diagnostics page: last match's feed, clock source, broadcast delay, API errors and rate-limit waits, and a
  "copy diagnostics" button that leaves out account ids and names. The main window's setup lines moved onto it.

**Gate:** a new player goes from a fresh install to a working overlay in a real match without opening
`settings.json`.

## M9 · Lobby intelligence

What you learn about the other eleven players before laning starts. Deadlock has no draft (heroes are chosen before
the queue and assigned by matchmaking), so this milestone is about the lobby you got, not the one you pick.

- [x] Lane opponent card: your lane opponents' recent form on their hero (last 10 games, record, souls/min against
  their career souls/min on it, which the hero-stats call scouting already makes now carries), from one cached
  match-history call each (only their games on that hero are cached, a day).
- [x] Enemy build tendencies: each enemy's usual big items on their hero (3,200 souls and up, bought in at least half
  of their games over 90 days), shown on the enemy pace board as "next: Boundless Spirit ~34:08" until they buy it.
  One analytics call per enemy (item-stats with account_id), cached a day: 6 a lobby against our 60 a minute.
- [x] Party tags: likely parties as "Team 1".."Team 6" chips next to the names on the scout list and in the main window,
  from each player's public party history (mate-stats with same_party, one cached call each). Asked for by the player.
- [x] Team comparison: rank spread, average games on hero and recent form for both teams in one line each, above the
  scout list and in the main window ("Archon 2 – Oracle 1 · 84 games on hero · 30 days 41–29, +4% souls/min"). Recent
  form is the record and souls/min on their current heroes over 30 days, from one bulk players/hero-stats call a match
  (the endpoint takes `min_unix_timestamp`), not ten match-history calls each.

**Gate:** in a real lobby, all of it shows within 30 s of the roster arriving, with no 429s in the log.

## M10 · Post-match coaching

Turning finished matches into a short list of things to change.

- [x] Counter items on the scout list (counter notes, part 1; replaces the hero pool advisor: with no draft, advice
  at queue time has nowhere to go). Item counters only, since they are measured, not opinion: "[!] Inhibitor" on an
  enemy when an item wins clearly more for your hero with theirs on the other team (`analytics/item-stats` with
  `enemy_hero_ids`, every rank, last 30 days, the matchup's own shift taken out, ≥300 games and ≥2.5 standard errors).
- [x] Counter notes in match history (part 2): on an opened game, each enemy hero's counter items, marked bought or
  not in that game; over the page's games, the heroes you lose to most (more losses than wins, two games or more),
  with their counter items for the hero you played most against them.
- [x] Soul sources: an opened game lists where its souls came from (troopers with denies, jungle, kills, objectives,
  the rest, and souls lost on death), each against the rank gate; a card pools the page's games and names the source
  that costs the most souls a game. From the metadata's `stats[].gold_*` columns (checked on public matches: about a
  quarter of a player's souls is not itemized, so "other" is large), and for the rank one cached sample of its matches
  per hero and tier (the metadata benchmark's call, now with a 14-day window: without one it times out).
- [x] Death review: on an opened game, each death with the souls you carried unspent (net worth then, less the
  items held; red at 3k or more) and a red cross on the soul timeline, and how many souls the rank gate gained while
  you were dead; a deaths card over the page (deaths and time dead a game, the gate's souls meanwhile, unspent at
  death and how often it was a big item's worth). From `death_details` (`game_time_s`, `death_duration_s`), asked
  for as flat columns in the same bulk call.
- [x] Goals: set targets (souls/min by 10:00, deaths in laning, a phase to fix) in the match history's goals card,
  saved as `targets` in settings; each opened game marks them hit or missed (a game too short, without death times or
  without a rank gate for the phase is left unchecked rather than missed), and the card shows each target's hit
  rate over the page's games (your last 20) with a dot per game and the hardest one named.
- [x] Weekly summary: a "last week" card in the main window, under today's: the last full week (Monday to Sunday) against
  the week before, with games, record and rank change, souls a minute, the phase and the item that improved most, and
  the one thing to work on next week (a goal target missed in most games, then dying with a big stash, then the phase
  that dropped most, then the item that slipped most). Refreshed with the session view, never during a match; the
  games come from the cached history and match details, a new game's detail in one bulk call.

**Gate:** after a real match, the post-match report and the history show soul sources, deaths and goals for it.

## M11 · Release 1.0

- [ ] Close every "Still open" item in `docs/verification/api.md`, or drop the feature that depends on it. *Oct 30:
  closed the Direct feed end to end (note 23), the stats cadence and the WebSocket idea (superseded). Oct 31: the
  hosted live query works again with bare table names (note 34), so the Direct feed's fallback is back. What is left needs a player's PC: the list is in "Player checks"
  in that file.*
- [x] Patch-day checklist: `tools/DeadlockScout.PatchCheck` prints what changed since its last run (game build, heroes
  and items, from the assets endpoints), the benchmarks' patch key and how to roll it, and the timers' spawn
  defaults, and with `--replay` runs a replay or live broadcast through ReplayCheck; exits 1 when something changed.
- [x] Performance budget: under 2% CPU and 400 MB (OCR on) during a match on a mid-range PC, measured and logged.
  `PerformanceMeter` reads our process's CPU time, working set and .NET heap every 5 s while a match is live and sums
  them up against the budget in Diagnostics and the log at each match's end; "Memory now" in Diagnostics shows it
  live. Two real 42–43 minute matches with OCR on (notes 36, 37): CPU 0.3–0.4% average, memory 337–363 MB peak.
  The budget was 200 MB before anything was measured; the floor is WPF, the .NET runtime and Windows OCR (about
  275 MB at rest, a .NET heap of only 41–95 MB), so it moved to 400 MB rather than 200 MB being chased by trimming.
  Windows OCR now loads on first use, so with OCR off it costs nothing.
- [x] README and troubleshooting rewritten for players, not developers: the README covers what it does, whether it
  is safe, what you need, install, first start, hotkeys and privacy; `docs/features.md` has every feature and
  screenshot, `docs/troubleshooting.md` goes symptom by symptom, `docs/development.md` has building and the tools.
- [ ] Installer and updates: a signed installer with auto-update from GitHub releases; the tray offers the update
  between matches only. Last, by the player's choice: the app runs from a build until then.

**Gate:** a fresh Windows PC installs 1.0, plays three matches with every panel working, and updates to 1.0.1.

## After 1.0

Not part of the release; picked up only once 1.0 has shipped.

- [ ] Our own HUD glyph reader in place of Windows OCR: the souls count and the clock use one fixed font, so a few KB
  of glyph templates (0–9, `:`, `.`, `k`) matched against each box would read them with no OCR engine loaded. It
  learns the templates once during calibration (Windows OCR labels the glyphs, then unloads), and asks for a
  recalibration when matches start failing (a patch changed the HUD font). Saves whatever the Windows OCR engine
  costs, measured with "Memory now" (OCR engine loaded vs not), and makes reads faster and testable on Linux with
  saved frames. Behind the existing `IOcrEngine` seam.
- [ ] Release the main window when it is closed to the tray (today it hides, keeping its visual tree), recreating it
  from the tray or a hotkey; the tray's actions move out of it first. Saves what "Memory now" shows between the
  window open and hidden.

- [ ] Statlocker data (statlocker.gg/api), only if a key is ever on hand: the API needs one, and keys are given out by
  hand on application, which the owner chose not to make for 1.0. What it would add on top of deadlock-api:
  - Lobby scouting: one batch profile call (up to 100 accounts) for each player's performance-based estimated rank,
    Season 1 ranked/casual ranks and Valve rank with their calibration games, to flag a player whose estimate sits
    well above or below their badge.
  - Post-match: MVP score and team placement, damage per minute, kill participation, accuracy, lane stats and the
    damage matrix, none of which deadlock-api's match data has.
  - Constraints: a user-supplied key in the settings (never shipped in the app); limits per key of 1,000 match and
    10,000 account requests an hour on a sliding window; a "Powered by Statlocker" credit wherever its data shows;
    an early-access API whose field names will change in v2, so tolerant DTOs. A second HTTP client means the seam
    rule in `CLAUDE.md` names it alongside `DeadlockApiClient`, behind the same rate limiter, cache and match gate.
