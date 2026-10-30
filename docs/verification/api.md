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

**Verified on a finished replay** (build 6819, match 124927700, a 23:26 match): the checker parsed the whole `.dem` in
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

## Still open (needs a real match on the current build)

- [ ] A full match through the Direct feed in the app, end to end.

- [ ] The hosted live query against a real live match: row volume, how soon rows start after `live/url`, and that the
      quoted table names resolve.

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
- [ ] The live-events service's actual latency behind the game clock (the doc assumes 60–90 s) and how soon after
      `live/url` the `/sync` endpoint answers.
- [ ] Whether the hosted `live.deadlock-api.com` service exposes the same event payloads; if so, add a WebSocket
      `ILiveEventStream`.
- [ ] `time_stamp_s` cadence in `stats[]` (assumed roughly per minute; `CurveBuilder` interpolates so any cadence works).
- [x] Deadlock's process name for `GameWindowTracker`: `game\bin\win64\deadlock.exe` (checked on the player's install,
      Oct 27, 2026). The assumed `project8.exe` is the pre-2024 name; looking only for it kept the overlay hidden for a
      whole real match while the preview worked. The tracker now takes either name, falls back to a window titled
      "Deadlock" whose program is in the game's `bin` folder, and the main window shows whether it found the game.
- [ ] The laning timers' first spawns (UNVERIFIED community values, Oct 2026): run ReplayCheck on a current replay
  and check the jumps in its "Jungle" section at 2:00, 3:00, 5:00 and 8:00; if it shows no denizens, the broadcast
  does not carry neutral camps and camp respawns cannot be tracked from it.
- [x] The urn schedule: 10:00, then every 5:00 (note 23).
- [ ] Deadlock's `game/citadel/cfg/video.txt`: `setting.fullscreen` "1" is exclusive fullscreen (players' forum posts);
  `setting.nowindowborder` telling borderless from a framed window is taken from other Source 2 games. Check a real
  file in each display mode and replace the synthetic fixture `Fixtures/steam/video.txt`.
- [ ] Party tags on a live lobby: after a match, compare the tags the overlay showed with who you know queued
      together (the app logs how many likely parties it found and from how many players' histories).
20. **`item-stats` without a bucket** (Oct 29, 2026). Live, Haze at badges 80–89 with `min_matches=20`: 133 rows
    (~33 KB), one per item, every `bucket` 0, each with `matches`, `wins`, `avg_buy_time_s`, `avg_sell_time_s` and
    their match-relative percentages. The item timings compare your first purchase of an item with `avg_buy_time_s`;
    it averages every match the item was bought in, so long games pull it later.
