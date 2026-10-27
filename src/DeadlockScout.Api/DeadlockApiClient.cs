using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeadlockScout.Api.Dto;
using DeadlockScout.Api.Http;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api;

/// <summary>
/// deadlock-api.com over one <see cref="HttpClient"/> whose pipeline is ApiKey → Retry → RateLimit. Paths and fields
/// are the ones verified against the API source in docs/verification/api.md. Methods throw <see cref="HttpRequestException"/>
/// on failure; callers decide how to degrade.
/// </summary>
public sealed class DeadlockApiClient : IDeadlockApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public DeadlockApiClient(HttpClient http, IOptionsMonitor<ScoutSettings> settings, ILogger<DeadlockApiClient>? log = null)
    {
        _http = http;
        _settings = settings;
        _log = log ?? NullLogger<DeadlockApiClient>.Instance;
    }

    private string Base => _settings.CurrentValue.Api.BaseUrl.TrimEnd('/');

    // ---- Players ----

    public async Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<PlayerHeroStats>();
        var result = new List<PlayerHeroStats>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var url = $"{Base}/v1/players/hero-stats?account_ids={string.Join(',', chunk)}";
            // 403 = every requested account is protected; that is "no data", not an error.
            var rows = await GetAsync<List<HeroStatsDto>>(url, ct, treatForbiddenAsEmpty: true).ConfigureAwait(false) ?? new();
            result.AddRange(rows.Select(r => new PlayerHeroStats(r.AccountId, r.HeroId, (int)r.MatchesPlayed, (int)r.Wins, (int)r.Kills, (int)r.Deaths, (int)r.Assists)));
        }
        return result;
    }

    public async Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<PlayerRank>();
        var rows = await GetAsync<List<AccountRankDto>>($"{Base}/v1/players/rank?account_ids={string.Join(',', ids)}", ct, treatForbiddenAsEmpty: true).ConfigureAwait(false) ?? new();
        return rows.Select(r => new PlayerRank(r.AccountId, r.Badge, r.Rank, r.Subrank)).ToList();
    }

    public Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct)
        => SharedAsync($"{Base}/v1/players/{accountId}/enemy-stats", asEnemy: true, ct);

    public Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct)
        => SharedAsync($"{Base}/v1/players/{accountId}/mate-stats", asEnemy: false, ct);

    private async Task<IReadOnlyList<SharedHistory>> SharedAsync(string url, bool asEnemy, CancellationToken ct)
    {
        var rows = await GetAsync<List<SharedStatsDto>>(url, ct, treatForbiddenAsEmpty: true).ConfigureAwait(false) ?? new();
        return rows.Select(r =>
        {
            uint other = r.EnemyId ?? r.MateId ?? 0;
            int wins = (int)r.Wins, matches = (int)r.MatchesPlayed;
            return new SharedHistory(other, matches, wins, Math.Max(0, matches - wins), asEnemy);
        }).Where(s => s.AccountId != 0).ToList();
    }

    public async Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct)
    {
        // The endpoint answers 429 with the stored history as its body when the Steam refresh is throttled, so read the body either way.
        using var response = await _http.GetAsync($"{Base}/v1/players/{accountId}/match-history", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<MatchHistoryDto>>(Json, ct).ConfigureAwait(false) ?? new();
        return rows.Select(r => new MatchHistoryEntry(r.MatchId, r.HeroId, DateTimeOffset.FromUnixTimeSeconds(r.StartTime),
                TimeSpan.FromSeconds(r.MatchDurationS), r.PlayerTeam is 0 or 1 ? r.PlayerTeam + 2 : r.PlayerTeam,
                r.Kills, r.Deaths, r.Assists, r.NetWorth, r.Outcome, r.RankedBadge, r.RankedDelta))
            .OrderByDescending(e => e.MatchId).ToList();
    }

    // ---- Matches ----

    public async Task<string> LiveUrlAsync(long matchId, CancellationToken ct)
    {
        var dto = await GetAsync<LiveUrlDto>($"{Base}/v1/matches/{matchId}/live/url", ct).ConfigureAwait(false);
        return dto?.BroadcastUrl ?? throw new HttpRequestException($"No broadcast URL for match {matchId}");
    }

    public async Task<ActiveMatch?> ActiveMatchAsync(long matchId, CancellationToken ct)
    {
        var rows = await GetAsync<List<ActiveMatchDto>>($"{Base}/v1/matches/active", ct).ConfigureAwait(false) ?? new();
        var m = rows.FirstOrDefault(r => r.MatchId == matchId);
        if (m is null) return null;
        return new ActiveMatch(m.MatchId, m.LobbyId, DateTimeOffset.FromUnixTimeSeconds(m.StartTime), TimeSpan.FromSeconds(m.DurationS),
            (m.Players ?? new()).Select(p => new ActiveMatchPlayer(p.AccountId, p.Team is 0 or 1 ? p.Team + 2 : p.Team, p.HeroId)).ToList());
    }

    public async Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct)
    {
        using var doc = await GetDocumentAsync($"{Base}/v1/matches/{matchId}/metadata", ct, background: true, notFoundAsNull: true).ConfigureAwait(false);
        return doc is null ? null : MetadataParser.ParseSingle(doc.RootElement);
    }

    public async Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct)
    {
        var url = $"{Base}/v1/matches/metadata?hero_ids={heroId}&min_average_badge={bracket.MinBadge}&max_average_badge={bracket.MaxBadge}" +
                  $"&include_info=true&include_player_info=true&include_player_stats=true&only_filtered_players=true" +
                  $"&order_by=match_id&order_direction=desc&limit={Math.Clamp(limit, 1, 200)}";
        using var doc = await GetDocumentAsync(url, ct, background: true, notFoundAsNull: true).ConfigureAwait(false);
        return doc is null ? Array.Empty<MatchMetadata>() : MetadataParser.ParseBulk(doc.RootElement);
    }

    public async Task SubmitSaltsAsync(IEnumerable<MatchSalts> salts, CancellationToken ct)
    {
        var body = salts.Select(s => new SaltsDto { MatchId = s.MatchId, ClusterId = s.ClusterId, MetadataSalt = s.MetadataSalt, ReplaySalt = s.ReplaySalt, Username = "DeadlockScout" }).ToList();
        if (body.Count == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/matches/salts") { Content = JsonContent.Create(body, options: Json) };
        request.Options.Set(RateLimitedHandler.PriorityOption, RequestPriority.Background);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ---- Analytics ----

    public async Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var qs = new List<string>();
        if (q.MinDurationSeconds is { } mind) qs.Add($"min_duration_s={mind}");
        if (q.MaxDurationSeconds is { } maxd) qs.Add($"max_duration_s={maxd}");
        if (q.MinAverageBadge is { } minb) qs.Add($"min_average_badge={minb}");
        if (q.MaxAverageBadge is { } maxb) qs.Add($"max_average_badge={maxb}");
        if (q.MinUnixTimestamp is { } mint) qs.Add($"min_unix_timestamp={mint}");
        if (q.MaxUnixTimestamp is { } maxt) qs.Add($"max_unix_timestamp={maxt}");
        var url = $"{Base}/v1/analytics/hero-stats" + (qs.Count > 0 ? "?" + string.Join('&', qs) : "");
        var rows = await GetAsync<List<AnalyticsHeroStatsDto>>(url, ct).ConfigureAwait(false) ?? new();
        IEnumerable<AnalyticsHeroStatsDto> filtered = rows;
        if (q.HeroIds is { Count: > 0 } heroes) filtered = rows.Where(r => heroes.Contains(r.HeroId)); // the endpoint has no hero filter
        return filtered.Select(r => new AnalyticsHeroStats(r.HeroId, (int)r.Matches, (int)r.Wins, r.TotalNetWorth, r.TotalKills, r.TotalDeaths, r.TotalAssists)).ToList();
    }

    public async Task<IReadOnlyList<ItemStat>> ItemStatsAsync(int heroId, RankBracket bracket, int? minNetWorth, int? maxNetWorth, CancellationToken ct)
    {
        var url = $"{Base}/v1/analytics/item-stats?hero_ids={heroId}&min_average_badge={bracket.MinBadge}&max_average_badge={bracket.MaxBadge}";
        if (minNetWorth is { } a) url += $"&min_networth={a}";
        if (maxNetWorth is { } b) url += $"&max_networth={b}";
        var rows = await GetAsync<List<ItemStatsDto>>(url, ct).ConfigureAwait(false) ?? new();
        return rows.Select(r => new ItemStat(r.ItemId, (int)r.Matches, (int)r.Wins)).ToList();
    }

    public async Task<IReadOnlyList<HeroMatchup>> HeroCounterStatsAsync(RankBracket bracket, CancellationToken ct)
    {
        // No hero filter exists: the endpoint returns every pair (~1,900 rows, ~700 KB) for the bracket, which the caller caches.
        // same_lane_filter=true (also the server default) keeps only pairs that were assigned the same lane.
        var url = $"{Base}/v1/analytics/hero-counter-stats?same_lane_filter=true&min_matches=20" +
                  $"&min_average_badge={bracket.MinBadge}&max_average_badge={bracket.MaxBadge}";
        var rows = await GetAsync<List<HeroCounterDto>>(url, ct).ConfigureAwait(false) ?? new();
        return rows.Where(r => r.HeroId > 0 && r.EnemyHeroId > 0 && r.MatchesPlayed > 0)
            .Select(r => new HeroMatchup(r.HeroId, r.EnemyHeroId, (int)r.MatchesPlayed, (int)r.Wins)).ToList();
    }

    // ---- Assets ----

    public async Task<IReadOnlyList<HeroAsset>> HeroesAsync(CancellationToken ct)
    {
        var rows = await GetAsync<List<HeroAssetDto>>($"{Base}/v1/assets/heroes", ct).ConfigureAwait(false) ?? new();
        return rows.Select(r => new HeroAsset(r.Id, r.ClassName ?? $"hero_{r.Id}", r.Name ?? r.ClassName ?? $"Hero {r.Id}",
            r.Images is not null && r.Images.TryGetValue("icon_image_small", out var img) && img.ValueKind == JsonValueKind.String ? img.GetString() : null,
            (r.PlayerSelectable ?? true) && !(r.Disabled ?? false))).ToList();
    }

    public async Task<IReadOnlyList<ItemAsset>> ItemsAsync(CancellationToken ct)
    {
        var rows = await GetAsync<List<ItemAssetDto>>($"{Base}/v1/assets/items", ct).ConfigureAwait(false) ?? new();
        return rows.Where(r => r.Type == "upgrade")
            .Select(r => new ItemAsset(r.Id, r.ClassName ?? $"item_{r.Id}", r.Name ?? r.ClassName ?? $"Item {r.Id}", r.SlotType ?? "", r.Tier ?? 0, r.Cost ?? 0, r.ShopImageSmall ?? r.Image)).ToList();
    }

    public async Task<SteamInfo?> SteamInfoAsync(CancellationToken ct)
    {
        var dto = await GetAsync<SteamInfoDto>($"{Base}/v1/assets/steam-info", ct).ConfigureAwait(false);
        return dto is null ? null : new SteamInfo(dto.ClientVersion, dto.ServerVersion, dto.VersionDate);
    }

    // ---- plumbing ----

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct, bool treatForbiddenAsEmpty = false, bool background = false) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (background) request.Options.Set(RateLimitedHandler.PriorityOption, RequestPriority.Background);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (treatForbiddenAsEmpty && response.StatusCode == HttpStatusCode.Forbidden) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> GetDocumentAsync(string url, CancellationToken ct, bool background, bool notFoundAsNull)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (background) request.Options.Set(RateLimitedHandler.PriorityOption, RequestPriority.Background);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (notFoundAsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = "";
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
        if (body.Length > 300) body = body[..300];
        _log.LogWarning("{Status} from {Url}: {Body}", (int)response.StatusCode, response.RequestMessage?.RequestUri?.AbsolutePath, body);
        throw new HttpRequestException($"{(int)response.StatusCode} from {response.RequestMessage?.RequestUri?.AbsolutePath}: {body}", null, response.StatusCode);
    }
}
