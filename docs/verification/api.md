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
   (20/min per IP, one call per lobby, cached 12 h); the Stack flag has no data source yet.
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

- [ ] Console.log lines for lobby creation, hero lock, match end and disconnect. `ConsoleLogPatterns` marks every regex
      except the documented lobby line UNVERIFIED. Collect a fixture with `-condebug`, scrub the account id, add it under
      `tests/DeadlockScout.Tests/Fixtures/console/` and fix the patterns.
- [ ] Steam `content_log.txt` line shapes for "updates disabled/enabled" and app state changes.
- [ ] The live-events service's actual latency behind the game clock (the doc assumes 60–90 s) and how soon after
      `live/url` the `/sync` endpoint answers.
- [ ] Whether the hosted `live.deadlock-api.com` service exposes the same event payloads; if so, add a WebSocket
      `ILiveEventStream`.
- [ ] `time_stamp_s` cadence in `stats[]` (assumed roughly per minute; `CurveBuilder` interpolates so any cadence works).
- [ ] Deadlock's process name and window title for `GameWindowTracker` (`project8.exe` / "Deadlock" assumed).
- [ ] A free source for party ids (the Stack flag).
