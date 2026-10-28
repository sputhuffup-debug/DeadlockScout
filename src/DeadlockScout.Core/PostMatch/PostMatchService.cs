using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.PostMatch;

/// <summary>
/// Builds the post-match report from the match's metadata (cached forever once the API has it) and the benchmark curve
/// for your hero at the match's average rank. Returns null until the API has processed the match.
/// </summary>
public sealed class PostMatchService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPaceBenchmark _benchmark;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public PostMatchService(IDeadlockApi api, ICache cache, IPaceBenchmark benchmark, IOptionsMonitor<ScoutSettings> settings, ILogger<PostMatchService>? log = null)
    {
        _api = api;
        _cache = cache;
        _benchmark = benchmark;
        _settings = settings;
        _log = log ?? NullLogger<PostMatchService>.Instance;
    }

    /// <summary>Your most recent match in the last day, from the cached match history; for the report shown at startup.</summary>
    public async Task<long?> LatestMatchIdAsync(uint me, TimeSpan maxAge, CancellationToken ct)
    {
        if (me == 0) return null;
        var history = await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var latest = history.OrderByDescending(h => h.StartTime).FirstOrDefault();
        return latest is not null && DateTimeOffset.UtcNow - latest.StartTime <= maxAge ? latest.MatchId : null;
    }

    public async Task<PostMatchReport?> TryBuildAsync(long matchId, uint me, CancellationToken ct)
    {
        if (matchId <= 0 || me == 0) return null;
        // Null (not processed yet) is never cached, so the next attempt asks again. The bulk check comes first: asking
        // the single-match endpoint for an unprocessed match spends its Steam fallback (3 an hour), which the first real
        // run used up within minutes.
        var meta = await _cache.GetOrCreateAsync(CacheKeys.MatchMetadata(matchId), null,
            async c => await _api.MatchProcessedAsync(matchId, c).ConfigureAwait(false) ? await _api.MatchMetadataAsync(matchId, c).ConfigureAwait(false) : null,
            staleWhileRevalidate: false, ct).ConfigureAwait(false);
        if (meta is null) return null;
        var mine = meta.Players.FirstOrDefault(p => p.AccountId == me);
        if (mine is null) return null;

        var bracket = BracketPolicy.Resolve(_settings.CurrentValue.Meter, PostMatchReporter.AverageBadge(meta));
        PaceCurve? curve = null;
        try { curve = await _benchmark.GetAsync(mine.HeroId, bracket, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "No benchmark for the report of match {MatchId}", matchId); }
        return PostMatchReporter.Build(meta, me, curve, bracket);
    }
}
