# Roadmap to 1.0

M0–M6 built the app; this plan takes it to a mostly feature-complete 1.0, through a 0.2 release that checks it on
real matches, a 0.3 that steadies OCR, coaches during the match and does what 1.0 needs ahead of time, a 0.4 for spending, easier OCR setup and your own hero pool, and a 0.5 for a steadier setup, your rank ahead and sharing. Each line is one PR. After a merge, the next
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
- [x] Camp respawn timers, only if the replay shows the broadcast carries neutral camps: a camp's clear seen in the
  broadcast starts its respawn countdown (small 1:25, medium 4:50, large 5:35, UNVERIFIED), limited to the camps
  nearest your lane. Logs each observed respawn to verify the intervals. *Oct 31: the replay shows the broadcast
  carries the camps (note 38), so this is buildable. Nov 1: built on the Direct feed. Creatures group into camps by
  where they spawn, a camp's kind comes from its first spawn time, and the cleared camps nearest your hero count down
  in the timers panel with their distance (`camps.*`). Each respawn seen is logged against the setting, and
  ReplayCheck prints them for a replay; the intervals, the camp radius and the distances stay UNVERIFIED until a
  replay or a match checks them ("Still open" in the verification notes).*
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

## 0.2 · Trustworthy on real matches

A release on the way to 1.0, for the owner's own use (anyone else installs it at their own risk). M7–M10 are done;
0.2 fixes what the first real matches of 0.1.x showed and checks every number the overlay shows against a real match,
so 1.0 is left with the fresh-PC gate and the process hardening. Fixes first, then the checks, then the two small
features.

Fixes from the matches of Oct 31 – Nov 1:

- [x] OCR calibration check: the calibration tool reads each box live and says when a reading does not parse or has
  more characters than the number ("souls box reads 7,689 where 768 fits: draw it tighter"), so misreads are fixed
  where they start. The +449,896% match (verification note 41) read extra digits all game.
- [x] Memory back under the budget: the main window is released when it is closed to the tray and recreated from the
  tray or a hotkey (moved here from After 1.0; the tray's actions move out of it first). The match of Oct 31 peaked
  at 429 MB with OCR on, over the 400 MB budget. Measured with "Memory now" before and after. *Nov 2: `AppShell` holds
  the tray's actions and the other windows; closing the main window releases it, collects once and logs "Main window
  closed to the tray: memory X MB -> Y MB"; Ctrl+Shift+D (`overlay.hotkeyMain`) or the tray builds it again. The
  number on a real match is part of the 0.2 gate.*
- [x] The soul guard's hold runs on real time: held OCR readings are let through after two minutes of the PC's clock,
  never because a reading's own game clock says so. The clock guard already drops misread clocks (note 41); this
  makes the hold safe even if one slips through.
- [x] An update check skipped because a match was live runs a few minutes after the match ends, not six hours later.

Checks on the owner's PC (each closes a "Still open" item or a player check; step by step, with what to send back, in
[verification/checks-0.2.md](verification/checks-0.2.md)):

- [ ] Camp respawn timers: one match's "Camp respawn observed" log lines, or a current-build replay through
  `PatchCheck --replay`, to confirm or correct the intervals, the camp radius and the distance shown.
- [ ] Match ingest: a salt shows up in Steam's cache after opening a match in Deadlock's in-game match history, and
  "Send my matches to deadlock-api" gets the match stored. If Steam's cache never has it, find where the client keeps
  it (files only; no network capture).
- [ ] Steam `content_log.txt` lines for automatic updates turned off and on.
- [ ] An uninstall (the "remove settings" question, nothing left behind), and whether Smart App Control lets an
  installer built on the same PC run.
- [ ] The Direct feed on the current game build: a current replay through `PatchCheck --replay`.

Small features:

- [x] The deadlock-api key encrypted in settings.json with Windows DPAPI (current user), migrated from the plain value
  on first start, so a copied settings file does not carry it.
- [x] A double tap of `` ` `` (or another key, `overlay.doubleTapKey`) hides or shows the whole overlay in game, without
  taking the key from the game (asked for on Nov 2).
- [x] Crate respawn timers next to the camp rows (Midtown crates especially), from the broadcast's breakable props,
  with each observed respawn logged like the camps'.

**Gate:** three real matches on an installed 0.2 with OCR on: no absurd pace numbers, memory under 400 MB, camp
respawns within 10 s of the settings (or the settings corrected), and one match stored through "Send my matches".

## 0.3 · Steadier OCR, coaching in the match, ready for 1.0

Planned on Nov 2, 2026, with 0.2 merged but its checks not yet run. Still for the owner's own use. In order: whatever
0.2's checks turn up, then the OCR reader (the source of the worst numbers so far), the coaching that builds on it,
the small conveniences, and last what 1.0 needs, so M11 is left with the release itself.

Carried over from 0.2:

- [ ] Fix what 0.2's checks and gate find (`docs/verification/checks-0.2.md`): camp and crate intervals, the crate
  radius, memory after the main-window release, the double tap in a real match, anything else they show. One line
  each here as they come in.
- [x] "Teach this box again" in the calibration tool: forgets the selected box's glyphs and soul icon so the next
  steady readings teach them afresh (after a patch changed the HUD font, or a box taught from a bad drawing), instead
  of deleting `glyphs.json`.
- [x] The enemy scoreboard boxes gone from the calibration tool (the owner, Nov 5): nothing read them, the scoreboard
  shows souls only to the thousand ("11k", which OCR read as "lilt"), and the enemy pace board has every enemy's exact
  souls from the broadcast. The tool has two boxes, your souls and the clock; an older settings file's `enemySouls` is
  ignored on load.
- [x] Whether the broadcast counts haunted souls, answered by the next match (verification note 43): readings that
  carried haunted souls (`SoulSample.Haunted`) are compared with the broadcast's net worth both counting them and
  leaving them out (`OcrJumpGuard.HauntedVerdict`), and Diagnostics' "OCR net worth" line names the one that agrees.
- [x] Clock misreads put right (0.2.1 logs: "20:01" for 29:01, "23:36" for 27:36, "6:27" for 6:17): a clock the
  guard refuses is corrected when exactly one of its digits is off from a clock the rule would accept now, the
  nearest to the expected one (`OcrClockGuard.Correct`), so the reading's souls are kept; further off, the reading is
  dropped as before. Corrected readings never teach the glyph reader.
- [x] Diagnostics → **Copy check results**: the lines the check sheet asks for after a match (memory, match ingest,
  glyph reader, OCR net worth and readings, clock corrections, process hardening, last match and performance), each
  named by its check, scrubbed like Copy diagnostics (`Core/Diagnostics/CheckResults`).
- [x] A box that stops reading mid-match is said at once: when the souls count or the clock reads nothing for 45 s
  while the game is in front (`OcrReadWatch`), the tray names the box once a match, and a click opens the calibration
  tool, instead of it showing only in Diagnostics after the match.
- [x] Haunted souls at risk (the owner, Nov 4): from `coaching.hauntedSouls` (1,000) haunted souls, the unspent-souls
  line adds "1.1k of it haunted, lost if you die", or warns on its own below the unspent threshold; read by OCR
  (`SoulSample.Haunted`), so only with OCR on.
- [x] Souls lost at a death and a spending recap (the owner, Nov 4): `SoulsRecap` reads the match's soul samples. A
  death that takes haunted souls (the haunted line gone and unspent souls down by about as much; secured ones leave
  them where they were) shows in the pace strip for 30 s ("Died at 14:20: lost 1,051 haunted souls"), and the last
  match's card adds the time spent on `coaching.unspentSouls`+ unspent souls, its longest stretch, and the haunted
  souls deaths took.
- [x] What OCR reads, shown, and the soul icon skipped (the owner, Nov 4): the calibration tool's preview of the souls
  box outlines the count it reads (blue), the soul icon it skips (grey) and the haunted line (red) (`SoulsLine.Outline`).
  The icon is learned, not guessed: a glyph left of the number in three readings whose text left it out (and that is
  no known digit) becomes the set's `Icon`, saved in `glyphs.json` and cut off before every read, glyphs and Windows OCR
  alike.
- [x] One glyph set per box (the owner, Nov 4): the clock and the souls count use different fonts, so one set mixed
  them. `GlyphBook` keeps a set for each (`glyphs.json` version 2; a version 1 file is dropped and both relearn), the
  OCR source names the box it reads (`OcrField`), and the souls count also learns during a match from readings that
  passed the clock guard and moved plausibly, only while its set still misses a character.
- [x] The souls box with haunted souls (verification note 43): the HUD moves the count up and adds a smaller "HAUNTED
  SOULS" line under it, and the "SOULS" label beside the count read as "501". `SoulsLine` keeps only the line with the
  tallest characters, cut to its full-height ones, before the souls box is read; the box is drawn to cover both
  positions (troubleshooting page, check 5). Haunted souls (unsecured: spendable, lost at a death) are read from the
  line under the count by Windows OCR, outside the glyph reader's statistics, and added to your unspent souls.
- [x] OCR pace counted unspent souls (verification note 42): the HUD's counter is the souls you have not spent, so
  every reading now goes to the meter as net worth, the counter plus the cost of the items the broadcast says you
  hold plus the purchases seen on the HUD since (item prices change only with a patch, so a purchase is a drop of
  about one price, read twice). The soul guard compares the counter with the broadcast's unspent souls, so a purchase
  no longer locks it out until you earn the souls back. Diagnostics' "OCR net worth" line gives the agreement with
  the broadcast.

Steadier OCR (moved here from After 1.0):

- [x] Our own HUD glyph reader in place of Windows OCR for the souls count and the clock: they use one fixed font, so
  a few KB of glyph templates (0–9, `:`, `.`, `,`, `k`) matched against each box read them with no OCR engine loaded.
  The templates are learned once in the calibration tool (Windows OCR labels the glyphs, then unloads) and kept in
  local app data; a box whose reads stop matching asks for a recalibration (a patch changed the HUD font). Windows OCR
  stays as the fallback. Behind the existing `IOcrEngine` seam; reads tested on
  Linux from synthetic frames. Measured: misreads per match against 0.2's, and "Memory now" with the OCR engine
  never loaded. *Nov 2: `GlyphSegmenter`, `GlyphSet` and `GlyphOcrEngine` in the Ocr project; the calibration tool
  teaches it from each steady reading of the souls or clock box and saves `glyphs.json` when it closes. Windows OCR
  does not unload within a run once the tool has used it; from the next start it loads only if a box falls back. On
  the synthetic font a glyph is about 0.01 from its own template at any size and 0.14 or more from any other; the
  real HUD's numbers, and the misread and memory measures, come with 0.3's gate.*

Coaching in the match (chosen Nov 2; only data the app already has, never where enemies are):

- [x] Unspent souls: over `coaching.unspentSouls` (3,000) the pace strip says "3.4k unspent: spend before you fight".
  *Nov 3: the HUD counter is unspent souls (note 42), so with OCR on this is the reading itself, live; without OCR,
  the broadcast's net worth less the items held, as every soul sample now carries it (`SoulSample.Unspent`), named
  with its game time since it is late. Settings → Overlay sets the threshold; 0 turns it off. Not while spectating.*
- [x] Team souls lead: one line with both teams' total souls from the broadcast and the gap's trend over the last
  three minutes ("Sapphire +4.2k, closing"), so a fight or an objective that swung the game shows. Delayed like
  everything from the broadcast, and labelled with its game time.
- [x] Pace to the next item: the next-item nudge gains a countdown at your current souls per minute ("Fortitude in
  ~1:40 at this pace · Oracle buys it at 16:30"), from the souls you have now, the item's price and your live rate.
  *Nov 2: done as net worth against net worth instead, so it waits on nothing: the time until your souls reach the
  rank curve's souls at the rank's buy time, at your two-minute rate ("Next: Fortitude ~2:10 at your pace · Oracle
  16:30", one line, since the overlay's right column clips what overflows). The souls-and-price
  version needs the unspent-souls check below.*
- [x] Your goals, live: the targets set in the match history (laning pace by 10:00, at most so many laning deaths, a
  phase to fix) checked during the match ("on track for 420 spm by 10:00", "one laning death left"), shown in the
  pace strip's phase row and hidden once the phase is over.

Small conveniences:

- [x] A "what's new" balloon after an update, linking the release notes (moved here from After 1.0).
- [x] Starting the app again while it runs opens the running one's main window, instead of exiting silently.
- [x] Back up and restore settings and the overlay layout to a file (Settings → Back up / Restore). The API key is
  left out, since it is encrypted for this Windows account and would not decrypt anywhere else. *Nov 3:
  `Core/Settings/SettingsBackup` writes a `.dsbackup.json` (kind, format, app version, when, the settings); a restore
  migrates it like settings.json and keeps this PC's key and Steam and Deadlock folders; a newer format is refused.*

Ready for 1.0:

- [x] Process hardening (moved here from M11, where the owner shelved it on Nov 1): at start-up, Windows' image-load
  and extension-point mitigations (system DLLs from System32 first, no DLLs from network shares or low-integrity
  files, no AppInit DLLs, global hooks or Winsock layered providers in our process); a process DACL that refuses
  other processes' memory writes and remote threads; the downloaded update held open, unwritable, from its hash check
  until the installer has started. Decide then whether to offer a per-machine install under Program Files.
  *Nov 3: `App/Interop/ProcessHardening` at start-up (DLL search limited to the app folder and System32 too), each
  step on its own and logged, Diagnostics' "Process hardening" line, `security.hardenProcess` to turn it off; the
  access list (`Core/Security/ProcessDacl`) also refuses memory reads, so the API key in memory is out of other
  programs' reach. The screenshot run applies it as well, so the Windows runner shows it holds and the windows still
  render. Per-machine install: decided by the owner on Nov 3, per-user only, never a system install (no
  administrator prompt for installs or updates; what Program Files would guard against, another program running as
  you replacing the app's files, could just as well change your shortcuts or startup entries).*
- [ ] A dry run of 1.0's gate on a fresh Windows install (a virtual machine is enough): install, first-run wizard,
  one match, update to a later build. Whatever it finds is fixed here, so M11 is only the release.

**Gate:** three real matches on an installed 0.3 with the glyph reader on: fewer misreads than 0.2's matches and no
absurd pace numbers, every coaching line seen at least once and none wrong when checked against the post-match report,
and the fresh-install dry run passed.

## 0.4 · Spending, easier OCR setup and your own pool

Chosen by the owner on Nov 5, 2026, while 0.3's checks wait on a match. Two lines for the match, three for between
matches (enemy item spikes and a lane phase summary were also picked, then dropped the same day: the power-spike
alerts and the live phase chips, sparkline legend and post-match lane line already do them; patch changes for you was
dropped too, since the app already rebuilds its benchmarks on each game build and the assets carry only names and
prices, which a patch rarely changes). Each uses only what the
app already reads: your own two OCR boxes, the delayed public broadcast, public match records and the assets
endpoints. In order:

In the match:

- [x] Affordable now: when the next-item nudge's item costs no more than your unspent souls (OCR, live), the nudge
  says so ("Fortitude 3,200: affordable now"), so a trip to the shop is not put off. Without OCR, from the
  broadcast's unspent souls and labelled with its game time; never while spectating. *Nov 5: `NextItem.Affordable`
  reads the newest soul sample's `Unspent` (haunted souls included); the nudge turns green and the overlay
  screenshot's demo shows it.*
- [x] OCR box presets: the calibration tool offers the souls and clock boxes for the game window's aspect ratio (16:9,
  16:10, 21:9), so the first setup is one click and a look at the preview instead of
  drawing two boxes. The boxes stay yours to move; a preset never overwrites a box already drawn. The 16:9 boxes come
  from the owner's calibrated ones (fractions of the window, nothing private); the other ratios are derived from how
  the HUD anchors to the screen's centre and bottom, marked UNVERIFIED until a match at that ratio confirms them.
  *Nov 5: `Ocr/HudPresets` from the owner's boxes at 2560×1440; other ratios keep the souls count's pixels from the
  left edge and the clock's from the centre, scaled by height, and the button says "estimated" for them.
  `RegionCalibration.FillFrom` fills only boxes not drawn yet.*

Between matches:

- [x] Hero pool by pace: the session view's hero pool (most played, win rate against the meta at your rank) gains each
  hero's souls per minute over your recent games on it against the rank gate, and its trend, so the heroes holding you
  back show even when their win rate looks fine. From the match history the app already keeps. *Nov 5:
  `SessionMath.Pace`: the last ten games of 10 minutes or more on the hero, each one's net worth over the gate curve's
  at its length; trend is the newer half against the older half, "steady" within 3%. The analytics provider's curves,
  one cached pass per bracket, so no new call.*
- [x] Tilt check: the session view's break nudge (three losses in a row, `session.lossStreakNudge`) also notices pace
  under the rank gate three games running, and is said once a session as a tray message between matches, never during
  one, instead of only in the session view. *Nov 5: `SessionMath.PaceNudge` over `RecentVersusGate` (the gate curves
  the hero pool already loads); `session.paceSlideNudge` (3, 0 off) and `session.nudgeInTray` in Settings → Session;
  the tray says it from `SessionRefreshService`, which never runs during a match.*
- [x] Teammate record: your record and average souls per minute with the players you queue with most (the party tags
  find them), from public match records; names only from those records, never kept beyond the cache. *Nov 5:
  `mate-stats?same_party=true` with its `matches` list, joined to your match history for the pace; names from
  `players/steam` (stored profiles, never a refresh; only the display name is kept, cached a week; UNVERIFIED until
  seen live, `docs/verification/api.md`). `session.teammates` (3, 0 off) in Settings → Session.*

**Gate:** two real matches on an installed 0.4: the affordable line seen at least once and right when checked against
the HUD, a first OCR setup from a preset, and the between-match cards checked against the match history.

## 0.5 · Steadier setup, your rank ahead, and sharing

Chosen by the owner on Nov 5, 2026, from ideas checked against the features page first. Two lines for the overlay
and its setup, three for between matches, and one that works toward 1.0's open checks. Same rules as before: only
your own OCR boxes, the delayed public broadcast, public match records and the assets endpoints. In order:

The overlay and its setup:

- [x] Boxes follow the window: when the game's client area changes size or shape (a new monitor, a resolution change,
  windowed to borderless), the OCR boxes are carried over the way `HudPresets` places them (the souls count's pixels
  from the left edge, the clock's from the centre, scaled by height) instead of reading the wrong spot, and the tray
  says so once ("OCR boxes moved for 3440×1440: check them in the calibration tool"). The window size the boxes were
  drawn on is saved with them; boxes from before this have none and are left alone. *Nov 5: `Ocr/HudCarry` (the
  presets now use it too), `HudRegions.DrawnOn` saved by the calibration tool, `SettingsHudRegionMap` carries live
  from the game window's client size and raises `Carried` once per shape for the tray. UNVERIFIED beyond 16:9.*
- [ ] Colour-blind colours: `overlay.palette` (normal, or blue/orange) swaps the green/red of ahead/behind and good/bad
  everywhere the overlay and the main window colour a number (pace delta, gate, phase chips, the affordable line,
  coaching lines), from one set of theme brushes so nothing is missed. Settings → Overlay; a screenshot of each.

Between matches:

- [ ] Rank forecast: from the ranked badge and its change in your match history, the session view says where your last
  20 ranked games point ("at this rate, Archon 4 in about 9 games", or "holding steady at Archon 3"), only with enough
  ranked games to say it, and never as a promise. A computed value, never stored.
- [ ] History filters: the match history window filters its games by hero, ranked only, or games with one of the
  friends from the teammate record, and its header (win rate, KDA, trends, the soul timeline's choice) is recomputed
  for the filtered games. The filter is a view; nothing is fetched again.
- [ ] Share card: **Copy as image** on the last match's report puts a picture of it on the clipboard (result, hero,
  K/D/A, souls per minute, pace at 10/20/30 and the phases against the gate) to paste into Discord. Only your own
  numbers: no other player's name, account id or rank, and no game art.

Toward 1.0:

- [ ] Close what code can of the API's "Still open" list (`docs/verification/api.md`) without the owner's PC: probe from
  the cloud session what a public endpoint can answer (the names endpoint's shape, without anyone's real account),
  and for each item left make the app or ReplayCheck print the exact line the owner's check needs, so each one is a
  copy and paste. M11's first line then only waits on those pastes.

**Gate:** two real matches on an installed 0.5: the boxes still read after the game window is resized, the
colour-blind palette checked on the overlay, the forecast and the filters checked against the match history, and a
share card pasted into Discord showing no one else's name.

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
- [x] Installer and updates: a signed installer with auto-update from GitHub releases; the tray offers the update
  between matches only. *Oct 31: the installer (`installer/`, Inno Setup, per user, no administrator rights, .NET 10
  runtime check, keeps settings on uninstall unless asked) and the in-app updater are in; releases carry a manifest
  signed with the owner's offline key (`docs/release.md`, fingerprints pinned), and installed copies refuse anything
  else. v0.1.1 was the first release: an installed 0.1.0 offered it, installed it and restarted as 0.1.1 (note 39).
  Build hardening (lock files, pinned Actions, minimal workflow permissions) is the next line.*
- [x] Build hardening: NuGet lock files with locked restore, GitHub Actions pinned to commit SHAs, and each workflow
  with the least permissions it needs, so a compromised dependency or Action cannot slip into a signed release.
  *Oct 31: every project, the vendored DemoFile included, has a `packages.lock.json` and CI restores locked;
  `nuget.config` maps every package to nuget.org only; checkout, setup-dotnet and upload-artifact are pinned to the
  commits of their current v4 tags; CI's token is read-only, the release job may only create its draft, the
  screenshots job only push its images (`docs/release.md`, "What protects players"). Later the same day: the release
  workflow attests the installer's provenance and the owner verifies it before signing; NuGet's audit fails the
  build on a moderate or worse advisory; Dependabot proposes weekly bumps a week after release; the API key goes only
  to deadlock-api.com's hosts and never follows a redirect elsewhere.*

- [ ] Process hardening: moved to 0.3 ("Ready for 1.0").

**Gate:** a fresh Windows PC installs 1.0, plays three matches with every panel working, and updates to 1.0.1.

## After 1.0

Not part of the release; picked up only once 1.0 has shipped.

- [ ] Code-signing the installer and the app (Authenticode, e.g. Azure Trusted Signing), so Windows names the publisher
  and Smart App Control lets a downloaded installer run. Not planned while DeadlockScout is a personal project
  (decided Nov 2, 2026); `docs/troubleshooting.md` covers installing without it.

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
