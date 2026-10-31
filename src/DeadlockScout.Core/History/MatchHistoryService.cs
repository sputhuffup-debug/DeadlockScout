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
/// Matches the app saw you play (<see cref="PlayedMatches"/>) that the history does not list yet are asked for in the
/// same bulk call: once processed they join the history from their metadata, until then they are processing cards.
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
    private readonly PlayedMatches? _played;
    private readonly ILogger _log;

    public MatchHistoryService(IDeadlockApi api, ICache cache, IClock clock, ILogger<MatchHistoryService>? log = null, PlayedMatches? played = null)
    {
        _api = api;
        _cache = cache;
        _clock = clock;
        _played = played;
        _log = log ?? NullLogger<MatchHistoryService>.Instance;
    }

    /// <param name="since">Only games that started at or after it (the weekly summary's two weeks); null for the newest <paramref name="count"/>.</param>
    public async Task<MatchHistoryPage> GetAsync(uint me, int count, CancellationToken ct, DateTimeOffset? since = null)
    {
        var history = me == 0 ? new List<MatchHistoryEntry>() : await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        history = history.ToList(); // a copy: the cached list itself is shared
        // Matches you played that the history does not list yet: a row built from their metadata once it came, else pending.
        var listed = history.Select(g => g.MatchId).ToHashSet();
        var unlisted = _played is null || me == 0
            ? new List<PlayedMatch>()
            : (await _played.RecentAsync(me, ct).ConfigureAwait(false)).Where(p => !listed.Contains(p.MatchId)).ToList();
        var pending = new List<PlayedMatch>();
        foreach (var p in unlisted)
        {
            if (await _cache.GetAsync<MatchHistoryEntry>(CacheKeys.PlayedEntry(me, p.MatchId), ct).ConfigureAwait(false) is { } e) Insert(history, e);
            else pending.Add(p);
        }
        var recent = Recent(history, count, since);

        var details = new Dictionary<long, MatchDetail>();
        var missing = new List<long>();
        foreach (var g in recent)
        {
            if (await _cache.GetAsync<MatchDetail>(CacheKeys.MatchDetail(me, g.MatchId), ct).ConfigureAwait(false) is { } d) details[g.MatchId] = d;
            else missing.Add(g.MatchId);
        }
        missing.AddRange(pending.Select(p => p.MatchId));
        if (missing.Count > 0)
        {
            try
            {
                foreach (var m in await _api.MatchesAsync(missing, ct).ConfigureAwait(false))
                {
                    if (HistoryMath.Detail(m, me) is not { } d) continue;
                    details[m.MatchId] = d;
                    await _cache.SetAsync(CacheKeys.MatchDetail(me, m.MatchId), d, null, ct).ConfigureAwait(false);
                    if (pending.Find(p => p.MatchId == m.MatchId) is { } played && HistoryMath.Entry(m, me) is { } entry)
                    {
                        pending.Remove(played);
                        Insert(history, entry);
                        await _cache.SetAsync(CacheKeys.PlayedEntry(me, m.MatchId), entry, PlayedMatches.Keep, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Match details unavailable for {Count} games; showing the history alone", missing.Count);
            }
            recent = Recent(history, count, since);
        }

        return new MatchHistoryPage(
            HistoryMath.Summarize(recent),
            HistoryMath.Rank(history, _clock.UtcNow - RankWindow),
            recent.Select(g => new HistoryGame(g, details.GetValueOrDefault(g.MatchId))).ToList(),
            history.Take(TrendGames).ToList(),
            pending.Where(p => since is null || p.StartedAt >= since).ToList());
    }

    /// <summary>Slots a row in by start time, keeping the API's own order for the rest.</summary>
    private static void Insert(List<MatchHistoryEntry> history, MatchHistoryEntry entry)
    {
        int at = history.FindIndex(g => g.StartTime < entry.StartTime);
        history.Insert(at < 0 ? history.Count : at, entry);
    }

    private static List<MatchHistoryEntry> Recent(IEnumerable<MatchHistoryEntry> history, int count, DateTimeOffset? since)
        => history.Where(g => since is null || g.StartTime >= since).Take(count).ToList();
}
