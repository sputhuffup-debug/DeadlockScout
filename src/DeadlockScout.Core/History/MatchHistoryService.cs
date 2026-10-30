using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.History;

/// <summary>
/// Builds the match history page. The history is the cached list the session view and post-match report share (one
/// call per 10 minutes at most). Each finished match's detail is cached for good under <see cref="CacheKeys.MatchDetail"/>,
/// so only matches not seen before are asked for, all in one bulk metadata call; a match the API has not processed yet
/// shows without detail and is asked for again next time. The page works without any detail at all.
/// </summary>
public sealed class MatchHistoryService
{
    /// <summary>How far back the rank chart reaches.</summary>
    public static readonly TimeSpan RankWindow = TimeSpan.FromDays(30);
    /// <summary>How many history rows the trends can cover; they come from the same cached history call, so no extra cost.</summary>
    public const int TrendGames = 100;

    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IClock _clock;
    private readonly ILogger _log;

    public MatchHistoryService(IDeadlockApi api, ICache cache, IClock clock, ILogger<MatchHistoryService>? log = null)
    {
        _api = api;
        _cache = cache;
        _clock = clock;
        _log = log ?? NullLogger<MatchHistoryService>.Instance;
    }

    /// <param name="since">Only games that started at or after it (the weekly summary's two weeks); null for the newest <paramref name="count"/>.</param>
    public async Task<MatchHistoryPage> GetAsync(uint me, int count, CancellationToken ct, DateTimeOffset? since = null)
    {
        var history = me == 0 ? new List<MatchHistoryEntry>() : await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var recent = history.Where(g => since is null || g.StartTime >= since).Take(count).ToList();

        var details = new Dictionary<long, MatchDetail>();
        var missing = new List<long>();
        foreach (var g in recent)
        {
            if (await _cache.GetAsync<MatchDetail>(CacheKeys.MatchDetail(me, g.MatchId), ct).ConfigureAwait(false) is { } d) details[g.MatchId] = d;
            else missing.Add(g.MatchId);
        }
        if (missing.Count > 0)
        {
            try
            {
                foreach (var m in await _api.MatchesAsync(missing, ct).ConfigureAwait(false))
                {
                    if (HistoryMath.Detail(m, me) is not { } d) continue;
                    details[m.MatchId] = d;
                    await _cache.SetAsync(CacheKeys.MatchDetail(me, m.MatchId), d, null, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Match details unavailable for {Count} games; showing the history alone", missing.Count);
            }
        }

        return new MatchHistoryPage(
            HistoryMath.Summarize(recent),
            HistoryMath.Rank(history, _clock.UtcNow - RankWindow),
            recent.Select(g => new HistoryGame(g, details.GetValueOrDefault(g.MatchId))).ToList(),
            history.Take(TrendGames).ToList());
    }
}
