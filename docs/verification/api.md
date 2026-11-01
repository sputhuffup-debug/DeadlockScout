# deadlock-api verification (Oct 27, 2026)

The design doc asks for every API assumption to be checked against the live OpenAPI and one real match before
building on it. This session could not reach `api.deadlock-api.com` (the cloud environment's network policy denied the
host), so the check was made against the API's **source code** instead: the public monorepo
[deadlock-api/deadlock-api](https://github.com/deadlock-api/deadlock-api) (`api/` is the REST service,
`live-events/` the live match service), read on Oct 27, 2026. Everything below cites that source. Nothing has yet been
checked against a real match; see "Still open" at the end.

## What the design doc got right

| Claim | Verdict | Source |
| --- | --- | --- |
| `GET /v1/matches/{id}/live/url` returns a broadcast URL | Yes: `{broadcast_url, lobby_id}`. 400 "cannot be live" for matches older than any live one. | `api/src/routes/v1/matches/live_url.rs` |
| Live URL is scarce | Yes, worse than written: **6/h per IP** anonymous (10 per 10 min and 50/h with a key), cached server-side for 1 h. Our client keeps `live:{match_id}` forever and allows 1/h. | `live_url.rs:249-267` |
| `players/hero-stats?account_ids=` bulk, up to 1000 | Yes. Comma-separated, SteamID3 or SteamID64. Protected accounts are dropped; **403 when every requested account is protected** (our client treats that as "no data"). | `api/src/routes/v1/players/hero_stats.rs` |
| `enemy-stats`, `mate-stats` exist | Yes: `{enemy_id|mate_id, wins, matches_played, matches}`. `wins` are *your* wins. | `players/enemy_stats.rs`, `mate_stats.rs` |
| `analytics/hero-stats` accepts duration and badge filters together | Yes: `min_duration_s`, `max_duration_s`, `min_average_badge`, `max_average_badge` (0–116). | `api/src/routes/v1/analytics/hero_stats.rs` |
| `POST /v1/matches/salts` needs no auth | Yes. Body is an array of `{match_id, metadata_salt?, replay_salt?, cluster_id?, username?}`, max 1000. No rate limit. | `matches/ingest_salts.rs`, `matches/types.rs` |
| Match metadata carries a per-player net-worth series | Yes: `match_info.players[].stats[]` with `time_stamp_s` and `net_worth`. The sampling cadence is not stated anywhere; the code treats it as data-driven. | `valveprotos/.../citadel_gcmessages_common.proto` |
| API key as `X-API-KEY`, `?api_key=` or `Bearer` | Yes. The middleware copies the query/Bearer forms into the header. | `api/src/middleware/api_key.rs` |

## What the design doc got wrong, and what changed

1. **The live feed is not `GET /v1/matches/demo/live/query`.** That endpoint exists but is a SQL console over the
   broadcast (20/min per IP). The structured player feed is the separate **live-events** service
   (`live-events/`): `GET {live}/v1/matches/{match_id}/live/demo/events?subscribed_entities=player_controller`,
   Server-Sent Events. It is **self-hosted**: a Docker image (`ghcr.io/deadlock-api/deadlock-live-events`, port 3000)
   that itself calls the API's live URL endpoint. A hosted, metered WebSocket version at `live.deadlock-api.com` is
   advertised on the web but is not in the repo and was not reachable; it is not implemented here.
   *Change:* `ApiSettings.LiveUrl` defaults to `http://localhost:3000`; `SseLiveEventStream` is the transport,
   `LiveMatchFeed` the mapper. The README tells the user to run the container.
2. **Live event shape.** Every event is one JSON object: `{tick, game_time, event_type, delta, entity_index,
   entity_type, ...entity fields}`. `player_controller` carries `steam_id` (already a 32-bit account id), `steam_name`,
   `team`, `hero_id`, `player_slot`, `assigned_lane`, `net_worth`, `kills`, `deaths`, `assists`. **`rank` is always 0 and
   there is no party field.** `tick_end` marks a coherent snapshot. *Change:* ranks come from `GET /v1/players/rank?account_ids=`
   (20/min per IP, one call per lobby, cached 12 h); parties are inferred from public party history (note 22).
3. **`analytics/hero-stats` has no `hero_ids` filter.** One call returns every hero. *Change:* the instant benchmark
   makes its 8 duration-bucket calls **once per bracket and patch** and caches a curve for every hero, so the second hero
   costs nothing. Response fields are `hero_id, matches, wins, total_net_worth, ...`; mean final net worth is
   `total_net_worth / matches`.
4. **Sampling matches for the accurate benchmark is one call, not forty.** `GET /v1/matches/metadata` (bulk) takes
   `hero_ids`, `min/max_average_badge`, `include_player_stats=true`, `only_filtered_players=true`, `limit` (30/min per IP)
   and returns players' `stats` as an **object of parallel arrays** (`{time_stamp_s: [...], net_worth: [...]}`), unlike the
   single-match endpoint's array of objects. `MetadataParser` reads both.
5. **`players/mmr` and `mmr-history` are deprecated.** They now echo Valve's rank. *Change:* the session layer will use
   `players/{id}/rank` and the `ranked_display_badge`/`ranked_delta` columns of `match-history`.
6. **`party-stats` does not exist.** Removed from the plan.
7. **Rate limits.** The real per-IP ceilings are per bucket, not per endpoint, and several endpoints have **no**
   application-level limit at all (`hero-stats`, `enemy-stats`, `mate-stats`, `salts`, assets, `matches/active`, cached
   metadata); their "100 req/s" is an edge figure in doc strings. Limited buckets: analytics 200/min, rank 20/min,
   metadata-from-S3 100 per 10 s (Steam fallback 3/h), bulk metadata 30/min, `match-history` 10/h **only for accounts the
   API's bot is friends with** (unlimited otherwise, and a 429 there still carries the stored history as its body, which the
   client reads). Our defaults in `RateLimitSettings` stay well under all of these. Every limited response carries
   `RateLimit-Limit/Period/Remaining/Reset` and `Retry-After` headers; the retry handler honours `Retry-After`.
8. **Assets live on the main host.** `assets.deadlock-api.com/v2/...` is legacy prose; the routes are
   `GET /v1/assets/heroes` and `GET /v1/assets/items` on `api.deadlock-api.com` (1 h cache header). The patch key comes
   from `GET /v1/assets/steam-info` → `client_version` (`ClientVersionPatchKeyProvider`), with the weekly date key as
   fallback.
9. **No ETag anywhere.** Conditional GETs are impossible; the `etag` column stays unused. Cache-busting query
   parameters (`_`, `nocache`, ...) are rejected with 400, so the client never adds them.
10. **Steam HTTP cache salts.** The website's uploader scans cache files for
    `http://replay{cluster}.valve.net/1422450/{match}_{salt}.(meta|dem).bz2`; `SteamHttpCache.ScanSalts` does the same.
    Full match metadata is not in that cache in a form the repo documents, so the post-match report reads the API.
11. **Matchups are `analytics/hero-counter-stats`, not `hero-comb-stats`.** `hero-comb-stats` returns same-team hero
    combinations and rejects `comb_size=1` with `400 "Combination size must be between 2 and 6"` (checked live
    2026-10-27), so the old matchup call could never have worked. `hero-counter-stats` takes no hero filter: one call
    returns every `hero_id` × `enemy_hero_id` pair for the badge range over the last 30 days, with `wins`,
    `matches_played` and per-side totals (live: 1,892 rows, ~720 KB for badges 60–80; pairs under `min_matches`, default
    20, are dropped). `same_lane_filter` defaults to true, which is the lane matchup the lane card wants.
    *Change:* `HeroCounterStatsAsync` fetches the bracket's whole table and `MatchupService` caches it per bracket and
    patch (`counters:{bracket}:{patch}`, 7 days), so a lobby's matchups cost one analytics call a week at most. The
    between-match prefetch warms it.
12. **Item and ability ids do not fit an `int`.** They are u32 and many exceed 2,147,483,647 (Soul Urn is 2521299219,
    Haze's Sleep Dagger 2948410412). The asset DTO read them as `int`, so the whole `/v1/assets/items` response failed to
    parse and item names never loaded. *Change:* item and ability ids are `long` end to end.
14. **`POST /v1/matches/salts`** (checked against `ingest_salts.rs`): a JSON array of `{match_id, cluster_id,
    metadata_salt, replay_salt, username}`, at most 1,000 per request, 100 req/s per IP. An empty array is a 400. Salts
    the API already has are skipped server side; the answer is `{"status":"success","salts_ingested":n}`. Our client
    sends `username: "DeadlockScout"` and never sends an empty array; `SaltIngest` also remembers what it sent.
15. **Bulk `/v1/matches/metadata` changed** (seen on the first real run, 2026-10-27): `only_filtered_players=true` now
    needs `account_ids`, else `400 "account_ids is required when using only_filtered_players"`. *Change:* the sample asks
    for all players with just `extra_player_columns=stats.time_stamp_s,stats.net_worth`, which come back as flat
    `stats_time_stamp_s` / `stats_net_worth` arrays per player (180 s cadence; ~2.2 MB for 40 matches of 12 players).
16. **Match history outcomes** (checked against a real account): `player_match_outcome` is 0 (unset) for most games
    before late 2026, and 3 ("penalized") marks games you left (`abandoned_time_s` set). The API's own win test is
    `match_result == player_team`. *Change:* wins and losses come from `match_result`; outcome 5 (not scored) counts as
    neither; games you left count as losses but are kept out of the personal curve, whose series stop early. The single
    match metadata endpoint answers 503 and then 429 with `Retry-After` of about an hour for old matches (its Steam
    fallback is 3/h); the retry handler no longer waits out a Retry-After over 20 s, and the personal curve only uses the
    last 30 days and skips a match that fails.
13. **`item-stats` `min_networth`/`max_networth` filter the buyer's *final* net worth**, not their net worth when they
    bought the item, so the old filtered call could not answer "what do players buy at my souls". The
    `bucket=net_worth_by_2000` option groups purchases by net worth at buy time (live, Haze at badges 60–80: 1,871
    item × bucket rows, ~460 KB, bucket = floor(net worth / 2000) × 2000, with `matches`, `wins`, `avg_buy_time_s`).
    `ability-order-stats?hero_id=` returns complete upgrade orders, one entry per ability point including unlocks, with
    `avg_upgrade_times_s` per step (live: 243 orders, ~130 KB). Ability names are in `/v1/assets/items` with
    `type: "ability"`. *Change:* `BuildHintService` caches both tables per hero, bracket and patch; the prefetch warms them.

17. **Second real run (a full match, Oct 27, 2026).** Two more findings from the app's log:
    - *The relay answers 405 until the broadcast starts.* Every `GET {url}/sync` answered 405, and the Direct feed
      gave up after six attempts (about a minute), before the broadcast of a match still loading existed. *Change:*
      `DirectBroadcastStream` keeps asking every 10 s for up to 6 minutes until the relay first serves data, then throws
      `BroadcastUnavailableException` so the selector switches to the hosted feed. It also reads the relay over HTTPS
      (live/url hands out `http://`), the scheme every verified read used. *Confirmed on the next match (Oct 27,
      2026):* over HTTPS the relay again answered 405 six times, from the lobby line until about 25 s after the game
      started, then served the whole 42-minute match with no drop; the roster and scouting followed at once. A finished
      match's relay answers 404.
    - *The report spent the Steam fallback.* Asking `/v1/matches/{id}/metadata` for a match the API has not processed
      yet goes to Steam, 3 requests an hour per IP; the report's 1/2/4/8-minute schedule hit 429 by the fourth try.
      The bulk endpoint filtered by id (`/v1/matches/metadata?match_ids={id}&limit=1`) answers 404 "No matches found"
      until the match is processed and 200 afterwards, without the Steam fallback. *Change:* the report checks there
      first and asks for the full metadata only once the match is processed.

18. **Starting the app mid-match** (Oct 28, 2026). The console tailer starts at the end of the file, so an app
    started after the lobby line never saw the match; the overlay then showed nothing but Preview's demo. *Change:* at
    start-up `ConsoleLogTailer` reads the last 16 MB of console.log once (a whole session was about 1.5 MB) and resumes
    the last match that has a start line but no end line, then tails from exactly where that read stopped. It does not
    resume a log quiet for more than 3 minutes: the game writes a frame-time line every minute even when idle (the
    longest silence in a real session was 61 s), so a quieter log belongs to a game that closed or crashed. The age
    comes from the lines' own `MM/DD HH:MM:SS` stamps, not the file's modified time, which Windows can leave stale while
    the game holds the file open. Checked on a real log: cut mid-match it resumes that match; whole, it resumes nothing.

19. **Spectating a live match** (Watch tab, Oct 27, 2026). The game logs no lobby or ticket line, so nothing was
    detected. It does log the broadcast it plays, with the match id in the URL: `[HostStateManager]
    HostStateRequest::Start(HSR_GAME): ... desc(Playing Broadcast (http://dist1-ord1.steamcontent.com/tv/<match_id>_<token>))`,
    and ends with `Disconnecting from server: NETWORK_DISCONNECT_HLTVSTOP`. A spectate whose broadcast is not up yet logs
    `/sync ... failed. Error 404`, then `Error 405`, then `Stream State Sync -> Stop`, and never the playing line: the
    game's own client sees the same 405 for a broadcast that has not started (item 17). *Change:* the playing line
    starts a spectated match (`MatchContext.Spectating`); the session treats no player as you, uses the lobby's average
    rank for curves, and the pace board covers all twelve players; no post-match report. Scrubbed as
    `Fixtures/console/spectate.log`; the real log replays to one spectated match, started and ended.

21. **The match clock starts at `GameInProgress`** (Oct 29, 2026, from console.log lines of three real matches and
    their `duration_s` from `/v1/matches/metadata`). From `OnGameStateChanged: GameInProgress (7)` to `PostGame (8)`
    the wall clock ran 28:36 and 42:24 for matches of 1,716 s and 2,544 s, equal to the second, and 39:14 for a
    2,337 s match (17 s long, most likely a pause: the game clock stops, the wall clock does not). So the laning
    timers' live clock takes that line as 0:00; a pause makes it run ahead by the pause's length, which OCR of the HUD
    clock avoids. The jungle's spawn times could not be checked here: Valve's replay servers serve plain HTTP only,
    which the cloud session cannot reach, so they wait for ReplayCheck on a replay on Windows.

22. **Party tags come from `mate-stats?same_party=true`** (Oct 30, 2026). Neither the broadcast nor the live-events
    feed carries a party id (the broadcast's player controller only has an "in party chat" flag, which the app does not
    read), and `players[].party` in a recent match's metadata was null for all twelve players. `mate-stats` takes
    `same_party` (default false): two players count as partied when they were on the same team and Steam friends at
    the match's start. Probed live for every player of one real lobby: twelve 200s in under a second each; six
    accounts with no party history (`[]`), two same-team pairs found, each pair's shared `matches` list including that
    match, and one pair listed by only one of its two players. So the app asks for every player and links two
    teammates when either history lists the other (`PartyGroups`). It is an inference: friends placed on one team by
    chance would be tagged too, which is why the overlay calls them likely parties.

23. **A full match on the Direct feed, Oct 29, 2026** (the app's log and console.log of one 35:51 match, kept off the
    repo). The urn came onto the map at 10:00, 15:00, 20:00, 25:00, 30:00 and 35:00, so the deadlock.wiki schedule
    (10:00, then every 5:00) holds; one more sighting at 31:05 is most likely the urn dropped by a carrier who died.
    The broadcast answered 405 for its first minute (the app asks again every 10 s) and then ran 34.1 s behind the
    game-log clock anchor, well under the 75 s assumed when no anchor is known. The player also saw the soul meter jump
    while dead: the game spectates a teammate and the HUD's soul counter shows theirs, which OCR read as yours. OCR
    readings now pass `OcrJumpGuard` (see the design doc's OCR section).

24. **Counter items from `item-stats` with `enemy_hero_ids`** (Oct 30, 2026, live). The filter works and answers in
    1–3.5 s (~30–40 KB). Two things decided the method. At one rank range (badges 80–89) the games are too few:
    Haze with Lady Geist on the other team had 50–500 games per item, and the "best" items were noise (a +10 point
    item on 54 games). Across badges 70–99, ordinary items all looked about 2.5 points better against her, which is
    Haze's edge in the matchup, not the items. So the app uses every rank (the endpoint's default 30 days), subtracts
    the games-weighted average lift across all items (the matchup shift), and keeps an item only with ≥300 games, a
    lift of ≥0.5 points and ≥2.5 standard errors. With that, Haze against Seven: Inhibitor +1.3 points (z 5.7, 44k
    games); against Ivy: Frenzy +3.3 (z 5.2, 5.7k games); against Abrams and Lady Geist: nothing clear. Real counters
    are one to three points; most pairs have none, and the tag then stays off.
25. **One player's items from `item-stats` with `account_id`** (Oct 30, 2026, live). The filter works with
    `hero_ids` and `min_unix_timestamp`: about 0.8 s and ~8 KB for a lobby player, one row per item they bought on
    the hero, with `matches` (their games with the item) and `avg_buy_time_s` (over those games). The answer has no
    total game count, so the app takes the most-bought item's count as the player's games on the hero (every game buys
    some cheap item); an estimate, close enough for "in at least half their games". The sampled player had only four
    games on their hero in 90 days, so many enemies will show nothing; three games is the minimum.
26. **`players/hero-stats` takes `min_unix_timestamp`** (Oct 30, 2026, live). One call for a lobby's account ids with
    a 30-day window answered in 2.5 s with 48 rows for six players with recent games (the rest none): the same fields
    as the career call (`matches_played`, `wins`, `networth_per_min`...), counted over that window only. The team
    lines use it for recent form instead of a match-history call per player, whose limit is far lower.
27. **Souls by source in the match metadata** (Oct 30, 2026, live, public matches). Every `stats[]` sample (taken at
    3, 6, 9, 12, 15 and 20 minutes and at the end; the last one's `time_stamp_s` equals `duration_s` and its
    `net_worth` the final one) carries `gold_player`, `gold_lane_creep`, `gold_neutral_creep`, `gold_boss`,
    `gold_treasure`, `gold_denied` and `gold_death_loss`, and a `gold_sources` list keyed by the protobuf's
    `EGoldSource` (players, lane creeps, neutrals, bosses, treasure, assists, denies, team bonus, ability assassinate,
    three items, breakables). The bulk call takes the flat `stats.gold_*` columns in `extra_player_columns` but answers
    400 to `stats.gold_sources`, so assists, team bonus, crates and items cannot be told apart there and go into
    "other". The itemized sources come to about three quarters of the net worth; the rest is not itemized anywhere in
    the record (UNVERIFIED what it is; it grows through the match for every player). For Haze at badges 80–89 (40
    matches, 1,320 minutes): kills 251, troopers 267 + denies 7, jungle 240, bosses 110, urn/treasure 52, other 342
    souls a minute, 41 lost on death. The same filtered sample without a time window answered 504 after 20 s; with
    `min_unix_timestamp` two weeks back it answered in about a second (2.4 MB with the gold columns).
28. **Deaths and net worth** (Oct 30, 2026, live, public matches). `death_details[]` holds `game_time_s`,
    `death_duration_s` (the respawn wait), `time_to_kill_s`, `killer_player_slot` and both positions; one entry per
    death. The bulk call takes `death_details.game_time_s,death_details.death_duration_s` in `extra_player_columns` and
    answers flat `death_details_game_time_s` / `death_details_death_duration_s` arrays. Net worth is the value of the
    held items plus unspent souls, not souls ever earned: across a 12-player match it was never below the cost of the
    items held at the end (78 to 8,000 above it) and always below the cost of everything ever bought. So the souls
    unspent at a death are net worth then less the items held then; net worth between the 3-minute samples is
    interpolated, so it is an estimate. UNVERIFIED: whether an item consumed by an upgrade is marked sold (if not, its
    cost would be counted twice and the estimate would come out low; the final numbers show no sign of it).
29. **The hosted live query fails server-side** (Oct 30, 2026, live). On two live matches (one by `broadcast_url`
    from `live/url`, one by `match_id`), `GET /v1/matches/demo/live/query` answered at once with
    `{"status":400,"error":"Invalid query: DataFusion error: Error during planning: table
    'datafusion.public.CCitadelPlayerController' not found"}`, and the same for `CCitadelGameRulesProxy` and lower-case
    names, although the endpoint's documentation and `GET /v1/matches/demo/schema` still list those tables. So the
    live engine has no tables registered for a broadcast; nothing in the app's query can fix it. The Hosted feed (the
    fallback when DemoFile cannot read a build) would get no rows until deadlock-api fixes it; the Direct feed does not
    use it.
    *Oct 31, 2026: not server-side after all; see note 34.*
30. **Build 6826 and 6827, Oct 30, 2026** (found by the patch check and ReplayCheck on live broadcasts). Matches on
    6826 appeared around 21:00 UTC and 6827 a quarter of an hour later, while the assets' steam-info still said 6821:
    the live match list (`compat_version`) shows a patch within minutes, steam-info hours later, so the patch check
    now prints both. On 6826 the vendored DemoFile threw `IndexOutOfRangeException` on every broadcast's first
    entity (a new vector-of-vectors field; fixed as local change 8 in `third_party/demofile-net/VENDORED.md`, after which 6826
    and 6827 broadcasts decode with all 12 players and the clock at +0.0 s), so the Direct feed read nothing, and the hosted fallback was down (note 29). The live match list also sends
    `"duration_s": null` for running matches, which threw in `ActiveMatchAsync` (the roster fallback); it is nullable
    now. While the API's spectator caught up, `live/url` answered `k_eInvalidClientVersion` for many matches of both
    builds.

31. **A real match on build 6833, Oct 30–31, 2026** (the player's log; ids kept off the repo). The app was started
    mid-match and resumed it from console.log; the Direct feed decoded it (broadcast delay 54.9 s by the game log's
    start line, the urn read at 10:00, 15:00, 20:00, 25:00 and 30:00), with no warning, error or 429 in the log. A
    live 6833 broadcast also decodes through ReplayCheck (12 players, clock +0.0 s). Spectating two matches and then
    joining one carried their players into the next roster; fixed in the live feed (pump generations).

32. **Party tags and enemy build tendencies, checked against the same match's records** (Oct 31, 2026).
    - *Party tags.* `mate-stats?same_party=true` lists, for each mate, the ids of the matches played together in a
      party, so who queued together in one match can be read back afterwards. Two pairs did (one on each team), and
      `PartyGroups` over the histories from before the match (this match taken out of the counts) gives exactly those
      two, nothing more: the "2 likely parties" the app logged.
    - *Enemy build tendencies.* Rebuilt for each enemy from `item-stats` with `account_id` and `hero_ids` over the 90
      days before the match, with `EnemyTendencies.Usual`'s rules. Four of the six enemies had one or two games on
      their hero and got no prediction. The other two (9 and 14 games) had four usual items each, and **all eight
      were bought**: five within a minute of the usual time (11:19 against 11:16, 22:49 against 23:06, 28:38 against
      29:19, 17:16 against 17:42, 19:21 against 17:43), one two minutes early (24:11, 22:19), two three to five minutes
      late (19:43 bought at 24:15, after the next usual item; 26:12 at 29:43). The board names one item at a time, so
      the late one meant "next: Rapid Recharge" showed while that player bought Infuser first; the order is a usual
      order, not a promise.
33. **Party tags on a second match, and a party the method cannot see** (Oct 31, 2026; ids kept off the repo). The
    app logged "1 likely party". The match's own records (`mate-stats?same_party=true` for all twelve players, which
    lists the matches each pair played as a party) show two: a trio on one team with 7 to 16 party games together
    before this match, and a duo on the other that partied for the first time in it. The app tagged the trio. A
    first-time party has no history to read in the lobby, so it cannot be tagged; the tag means "has partied before",
    which the scout list's tooltip already says. The bulk metadata for this match carried no `party` field,
    so the check has to go through mate-stats.
34. **The hosted live query works with bare table names** (Oct 31, 2026, live, build 6835). Retesting note 29 on a
    live match: `SELECT * FROM CCitadelGameRulesProxy` answered, `SELECT * FROM CCitadelPlayerController` answered,
    but `FROM "CCitadelPlayerController"` (quoted, as the app sent it) still gave "table not found". The engine
    resolves table names only unquoted; the mixed-case columns still need their quotes. With the table names bare,
    the app's two queries return rows: the player query streamed 26,890 rows in its first 25 s (the catch-up from the
    broadcast's start, then a row per change), the rules query a few a second. Run through the app's own
    `HostedLiveQueryStream` and `LiveQueryTranslator` for 60 s: first event 1.8 s after `live/url`, 10 controller
    creates, 48,000 updates over 4,799 ticks, game time reaching 10:53. The schema endpoint still describes a
    replay (an older match), not the live broadcast. The Direct feed on build 6835 could not be checked from here
    (Valve's relay does not resolve from the test container); a player's `PatchCheck --replay` covers it.
35. **Deadlock no longer keeps `video.txt` up to date** (Oct 31, 2026, the player's PC). The file was last written on
    July 15 although the display mode had been changed since, and quitting from the menu did not write it; no other
    video file exists under the game's `cfg` folder. (Steam's `userdata` was not searched after all: the search looked
    in the default Steam folder, and this player's Steam lives elsewhere.) Players report the same on the forums
    (video settings "not written to video.txt or video.txt.bak", fullscreen selected but the game behaving as
    borderless). Its lines, from when it was last written in borderless: `setting.fullscreen` "0",
    `setting.coop_fullscreen` "1", `setting.nowindowborder` "1", which `VideoConfig` reads as borderless. Because a
    stale file kept the fullscreen warning silent whatever the game did, the overlay now asks Windows while the game is
    in front (`SHQueryUserNotificationState`: 3 is an exclusive-mode Direct3D program) and uses the file only when
    Windows cannot say. Checked on the same PC the same day: with the game in front in borderless, Diagnostics read
    "Display mode: borderless (from Windows, with the game in front)". Exclusive fullscreen could not be checked:
    selecting it in Deadlock does not take effect on that PC (what the forum posts report too).
36. **Performance budget, first real match** (Oct 31, 2026, build 6835, a 42:17 match, Direct feed, OCR on). The
    meter read CPU 0.4% average (p95 0.5%, peak 1.0%), well within 2%, and memory 363 MB peak working set (private
    318 MB), over the 200 MB budget. The same match: roster 1:16 after the start, broadcast delay 34.6 s, the
    broadcast answering "not yet" six times first. The peak alone cannot say whether the app weighs that much before
    the match or the match adds it, or whether it is .NET objects (our code, the broadcast decoder) or native memory
    (WPF, Windows OCR), so the meter now also reports the working set at the match's start and the .NET heap's peak.
37. **Performance budget, second real match, and the budget revised** (Oct 31, 2026, a 43:28 match, OCR on). CPU 0.3%
    average (p95 0.4%, peak 2.1%); memory 337 MB peak, private 269 MB, .NET heap 68 MB, 249 MB already at the
    match's start. At rest, with the OCR engine loaded: 276 MB, private 237 MB, .NET heap 41 MB. So the match adds
    about 90 MB and the floor is native (WPF, the .NET runtime, Windows OCR), not our objects or the broadcast
    decoder. The memory budget moves from 200 MB, set before any measurement, to 400 MB with OCR on; both matches are
    within it. A glyph reader of our own in place of Windows OCR is on the roadmap's After 1.0 list.
38. **Laning timers' first spawns, from a replay** (Oct 31, 2026; the build 6819 replay of the Direct feed check above,
    run again through `PatchCheck --replay` on the player's PC, 1,498 snapshots). The jungle, sampled every 15 s:
    denizens 0 → 12 at 2:15 (small camps, default 2:00); breakables 3 → 549 at 3:15 (crates, 3:00); denizens 96 and
    breakables 650 at 5:15 (medium camps and Midtown crates, 5:00); denizens 137 and Sinner's Sacrifice 15 at 8:15
    (large camps and Sinner's, 8:00). All four first spawns hold. The broadcast carries the neutral camps (their
    count falls as they are cleared), which the camp respawn timers need; small camps cleared around 2:30–3:30 came
    back 3:45–4:45, about 1:15–1:30 later, which fits the 1:25 default. The urn appeared at 10:00, 15:00 and 20:00
    (as note 23), and the HUD clock fell 52.3 s behind the tick count after a pause, as before. The current build
    (6835, and 6839 out the same day) still needs a replay through the Direct feed.
39. **Installer and signed update on a real PC** (Oct 31, 2026, the player's Windows PC). The first installer stopped at
    "needs the .NET 10 Desktop Runtime" on a PC that has it: the .NET installer records `10.0.3` only in the 32-bit
    registry view (`WOW6432Node`), and setup read the 64-bit one. Setup now reads both views and the user's, then the
    runtime folder, and installed per user without administrator rights. Then the first release, v0.1.1, signed on the
    player's PC with the pinned signing key: the installed 0.1.0 asked GitHub two minutes after it started, logged
    "Update 0.1.1 is available (signed, valid until 2027-01-29)", offered it in the tray, installed it silently on a
    click and came back as 0.1.1 with its settings. The same release checked from outside:
    `ReleaseSign verify` passes and the app's `UpdateClient` answers Offer for 0.1.0 and UpToDate for 0.1.1, with the
    download matching the signed size and SHA-256. An earlier check, before the release was published, got GitHub's
    404 and offered nothing, as it should. Still unchecked: an uninstall, and a refused release (unsigned or tampered)
    on a real PC; the tests cover both refusals.
40. **The hardened release pipeline, end to end** (Oct 31, 2026, v0.1.2, the player's PC). The owner pushed the tag as
    the repository admin through the `v*` tag ruleset (other accounts are refused). The release workflow's new
    provenance step ran on its first try, and `gh attestation verify` on the owner's PC printed "Verification
    succeeded!" for the installer downloaded from the draft before it was signed. Checked from outside afterwards: one
    attestation for the installer's SHA-256 (`8a463d94…`) naming `release.yml`, `refs/tags/v0.1.2` and the release
    commit; the same hash in the signed manifest and in `SHA256SUMS.txt`; `ReleaseSign verify` passes; and the app's
    `UpdateClient` answers Offer for 0.1.1 and UpToDate for 0.1.2. Windows 11's Smart App Control blocked the
    installer downloaded with the browser (it has no Authenticode signature), which led to the troubleshooting entry
    "Windows blocks the installer"; the PC reached 0.1.2 anyway. Still unchecked: whether Smart App Control lets an
    installer built on the player's own PC run, and the tray's update path with Smart App Control on.

## Hosted live feed (added Oct 27, 2026)

The default live feed no longer needs the self-hosted container. `HostedLiveQueryStream` opens two long-lived
`GET /v1/matches/demo/live/query?broadcast_url=...&query=...` streams per match (SSE, one JSON row per `message`
event, `status`/`error`/`end` events, 2 h cap; 20/min per IP with `broadcast_url`). Verified against
`api/src/routes/v1/matches/demo/live_query.rs` and the query engine in `demofusion/`, and the column names against the
schema served by `GET /v1/matches/demo/schema` (captured from match 125072949, 990 tables):

| Table | Columns used |
| --- | --- |
| `CCitadelPlayerController` | `tick`, `entity_index`, `delta_type`, `m_steamID` (UInt64, SteamID64), `m_iszPlayerName`, `m_iTeamNum`, `m_nAssignedLane`, `m_PlayerDataGlobal__m_nHeroID`, `m_PlayerDataGlobal__m_iGoldNetWorth`, `m_PlayerDataGlobal__m_iPlayerKills`, `m_PlayerDataGlobal__m_iDeaths`, `m_PlayerDataGlobal__m_iPlayerAssists` |
| `CCitadelGameRulesProxy` | `tick`, `delta_type`, `m_pGameRules__m_flGameStartTime`, `m_pGameRules__m_nTotalPausedTicks` |

- Every create/update row carries the entity's full decoded state, not just changed fields; delete/leave rows are all
  nulls (`entity_batch_builder.rs`). Null columns are omitted from the JSON (Arrow's line-delimited writer).
- Game time uses the live-events formula: `(tick - total_paused_ticks) / 64 - game_start_time`
  (`live-events/src/demo_parser/visitor.rs`). The tick rate is `api.liveTickRate`.
- Identifiers are mixed case, so every table and column name in the query is double-quoted.
- `m_nCurrentRank` exists but ranks still come from `players/rank`.

## Direct broadcast feed (added Oct 27, 2026)

`DirectBroadcastStream` reads Valve's HTTP broadcast relay (`{broadcast_url}/sync`, `/0/start`, `/{n}/full`,
`/{n}/delta`) with [DemoFile](https://github.com/saul/demofile-net), vendored in `third_party/demofile-net` and patched.
Only the broadcast URL comes from the API (`live/url`, cached). It cannot be guessed: relay URLs carry a token,
`http://dist1-ord1.steamcontent.com/tv/{match_id}_{token}`.

**Verified on a live match** (build 6821, match 125706310, Oct 27, 2026): `tools/DeadlockScout.ReplayCheck` read the
broadcast for 90 s, 114 snapshots, with all 12 players' account, team, net worth and K/D/A; the HUD match clock agreed
with the tick-from-start time to 0.0 s; hero ids and teams match the API's record of the match exactly. Assigned lanes
decode (1/4/6) early in the match and read 0 later, which looks like the game clearing them after the laning phase.

**Verified on a finished replay** (build 6819, a player's 23:26 match): the checker parsed the whole `.dem` in
12 s (1,498 snapshots). Against the API's metadata for the match, all 12 heroes and K/D/A lines match exactly, and final
net worth matches exactly for the six winners and within about 1.5% for five of the six losers (one player who went
0/5/0 read 12,031 against 14,265; the replay's last snapshot comes before the match-end record). The HUD match clock
fell 52.3 s behind the tick count at minute 12, where the metadata records about 50 s of pauses: the clock excludes
pauses, as intended.

Unpatched DemoFile 0.44.1 failed on this build in five places, each found by replaying a recording through DemoFile
and through haste (deadlock-api's parser) and comparing bits per field; `third_party/demofile-net/VENDORED.md` lists
the fixes. The selector still falls back to the hosted feed if a future patch breaks decoding.

- Fields read: `CCitadelPlayerController` `SteamID`, `PlayerName`, `TeamNum`, `AssignedLane`, and from
  `PlayerDataGlobal` `HeroID`, `GoldNetWorth`, `PlayerKills`, `Deaths`, `PlayerAssists`; game rules `GameStartTime`,
  `MatchClockUpdateTick`, `MatchClockAtLastUpdate`, `ServerPaused`.
- The relay is plain HTTP on players' PCs. This build environment only proxies HTTPS, and the relay also serves HTTPS,
  which is what the verification used.

## Player checks

What only a player's PC can confirm, in the order that costs least. Each closes an item below or above.

1. ~~Laning timers~~: done (note 38). Still wanted: the same run on a replay of the current build, to show the
   Direct feed decodes it (the replay checked was build 6819). Send only the build line, the Replay summary and the
   Jungle section: the roster above them lists every player's name and account id.
2. ~~Party tags~~ and ~~enemy build tendencies~~: done from the match's own records (note 32); nothing to do.
3. ~~Display mode~~: borderless read correctly from Windows (note 35); exclusive fullscreen cannot be checked while
   Deadlock's own fullscreen setting does not take effect.
4. **Steam content log.** Turn automatic updates off and on for Deadlock in Steam, then copy the new lines at the end
   of `<Steam>\logs\content_log.txt` (they name the app id, not you).
5. **Diagnostics.** After a lobby, Diagnostics → Copy diagnostics: no 429s means the scouting calls (parties, counter
   items, tendencies, team lines) stay under the limits. The player's log of Oct 30 already showed none (note 31).
6. **Performance budget.** Play a whole match (not spectate) with the overlay on, then Diagnostics → Copy diagnostics
   before the next one starts, and send the "performance:" line under the last match (CPU average, p95 and peak,
   memory peak; no names or ids), with the PC's CPU and RAM. The log file (Diagnostics → Open log folder) has the same line at each match's end.
   Two matches, one with OCR on, close the M11 budget line.
   *Memory split, no match needed:* Diagnostics' setup lines show "Memory now" each second. Read it (a) with OCR off
   after a fresh start, (b) after turning OCR on and opening the calibration tool with the game running (the engine
   loads on its first read), (c) with the main window hidden to the tray, and (d) with the overlay preview on; each
   difference is that part's cost.

## Still open (needs a real match on the current build)

- [x] A full match through the Direct feed in the app, end to end: the player's 35:51 match of Oct 29, 2026 (note 23).

- [x] The hosted live query against a real live match: row volume, how soon rows start after `live/url`, and that the
      table names resolve (note 34, Oct 31, 2026). The quoted table names were the fault all along: only bare names
      resolve. With them, the app's own `HostedLiveQueryStream` gave its first event 1.8 s after `live/url` and caught
      up from the broadcast's start to 10:53 of game time within a minute (4,799 ticks of player updates).
- [x] Console.log lines for lobby creation, server assignment, disconnects and the return to the hideout, from a real
      `-condebug` log (Oct 27, 2026; scrubbed as `Fixtures/console/real-match.log`). The first real run lost the match a
      second after detecting it: the engine logs `Disconnecting from server: NETWORK_DISCONNECT_LOOPDEACTIVATE` on every
      loop switch, including hideout to match server, and the old pattern read that as a match end. Loop switches are
      now ignored; leaving (`NETWORK_DISCONNECT_DISCONNECT_BY_USER`) or loading `dl_hideout` ends the match.
- [x] The match end, from the same match played to the end screen: `OnGameStateChanged: PostGame (8)` when the end
      screen opens, `Lobby <id> for Match <id> destroyed` a second later, then `End (11)`, the disconnect and the
      hideout load. The first of these ends the match; the rest are no-ops. A matchmade match never logged the local
      hero (`Attempting to spawn hero` appears only in bot matches); the hero comes from the live roster.
- [ ] Steam `content_log.txt` line shapes for "updates disabled/enabled" and app state changes.
- [ ] The live-events service's actual latency behind the game clock (the doc assumed 60–90 s; the Direct feed measured 34–55 s) and how soon after
      `live/url` the `/sync` endpoint answers. *Kept open on purpose (Oct 31, 2026): the self-hosted feed stays, by the
      player's choice, and only someone running that container can measure it.*
- [x] ~~Whether the hosted `live.deadlock-api.com` service exposes the same event payloads (for a WebSocket feed).~~
      Dropped: the Direct feed and the hosted live query (`demo/live/query`) took its place.
- [x] `time_stamp_s` cadence in `stats[]`: every 3 minutes to 15:00, then every 5 minutes, plus one at the match's
      end (40 public matches, Oct 30, 2026; note 27). `CurveBuilder` interpolates, so the cadence does not matter.
- [x] Deadlock's process name for `GameWindowTracker`: `game\bin\win64\deadlock.exe` (checked on the player's install,
      Oct 27, 2026). The assumed `project8.exe` is the pre-2024 name; looking only for it kept the overlay hidden for a
      whole real match while the preview worked. The tracker now takes either name, falls back to a window titled
      "Deadlock" whose program is in the game's `bin` folder, and the main window shows whether it found the game.
- [x] The laning timers' first spawns: the four defaults hold on a replay (note 38), and the broadcast carries the
  neutral camps, so camp respawns can be tracked from it.
- [x] The urn schedule: 10:00, then every 5:00 (note 23).
- [x] The display mode as Windows reports it (`SHQueryUserNotificationState`): borderless confirmed on a player's PC
  (note 35). Exclusive fullscreen (expected 3, Direct3D exclusive; UNVERIFIED for the Vulkan renderer) is left
  unchecked on purpose: Deadlock's fullscreen setting does not take effect, so no player can be in it today. If a
  patch fixes that, check it then; until then the warning also keeps its saved-file fallback.
  `video.txt` is now only the fallback: the player's file was months old while the game ran (note 35). Its borderless
  shape is confirmed by a real file (`fullscreen` "0", `coop_fullscreen` "1", `nowindowborder` "1"); exclusive
  fullscreen's is still the players' forum reading (`fullscreen` "1").
- [x] Party tags on a live lobby: the two parties that queued together in the player's match are exactly the two the
      app tagged (note 32).
- [x] Enemy build tendencies on a live lobby: all eight predicted items of the two enemies with a prediction were
      bought, most within a minute of the usual time, and the player's log shows no 429 (notes 31, 32).
20. **`item-stats` without a bucket** (Oct 29, 2026). Live, Haze at badges 80–89 with `min_matches=20`: 133 rows
    (~33 KB), one per item, every `bucket` 0, each with `matches`, `wins`, `avg_buy_time_s`, `avg_sell_time_s` and
    their match-relative percentages. The item timings compare your first purchase of an item with `avg_buy_time_s`;
    it averages every match the item was bought in, so long games pull it later.
