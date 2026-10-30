# Roadmap to 1.0

M0–M6 built the app; this plan takes it to a mostly feature-complete 1.0. Each line is one PR. After a merge, the next
unchecked line in the current milestone is the next PR; nobody has to brainstorm. Reorder or cut lines here, in a PR
of their own, when priorities change.

Rules for every line:

- It follows `CLAUDE.md`: VAC-safe (files the game writes, public APIs, screen capture of our own boxes only),
  computed values never stored, every API call cached and rate limited.
- A user-facing line ships with its screenshot from demo data and a README line.
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
- [ ] Verify the timers on a real match. *The clock anchor is verified (verification note 21).* Still open: run
  ReplayCheck on a current replay (jungle section) on Windows and correct the spawn defaults from what it shows.
- [x] Objective countdowns on the live clock: the mid boss's estimated return and the urn as "in 1:40" countdowns
  (today they are game-clock times), with the same gold lead as the timers.
- [ ] Camp respawn timers, only if the replay shows the broadcast carries neutral camps: a camp's clear seen in the
  broadcast starts its respawn countdown (small 1:25, medium 4:50, large 5:35, UNVERIFIED), limited to the camps
  nearest your lane. Logs each observed respawn to verify the intervals.
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
- [ ] Goals: set targets (souls/min by 10:00, deaths in laning, a phase to fix); each match marks them hit or
  missed, and the history shows the hit rate over the last 20.
- [ ] Weekly summary: rank change, the phase and items that improved most, and the one thing to work on next week,
  in the main window every Monday.

**Gate:** after a real match, the post-match report and the history show soul sources, deaths and goals for it.

## M11 · Release 1.0

- [ ] Installer and updates: a signed installer with auto-update from GitHub releases; the tray offers the update
  between matches only.
- [ ] Close every "Still open" item in `docs/verification/api.md`, or drop the feature that depends on it.
- [ ] Patch-day checklist: ReplayCheck on a fresh replay, the timers' spawn defaults, the item and hero assets, and
  the benchmarks' patch key, as a script that prints what changed.
- [ ] Performance budget: under 2% CPU and 200 MB during a match on a mid-range PC, measured and logged.
- [ ] README and troubleshooting rewritten for players, not developers.

**Gate:** a fresh Windows PC installs 1.0, plays three matches with every panel working, and updates to 1.0.1.
