using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>What one prefetch pass covered.</summary>
public sealed record WarmResult(RankBracket Bracket, IReadOnlyList<int> Heroes, int CurvesReady);

/// <summary>
/// Between matches, asks every benchmark provider for the curves of your most played recent heroes in your bracket, so
/// they sit in the cache when the next match starts. The accurate (metadata) and personal curves are background
/// requests, which wait while a match is live; without this they rarely arrive before the match is over.
/// </summary>
public sealed class BenchmarkWarmer
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IReadOnlyList<IPaceBenchmark> _providers;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly MatchupService? _matchups;
    private readonly ILogger _log;

    /// <param name="matchups">Also warms the bracket's lane matchup table (one large analytics call a week).</param>
    public BenchmarkWarmer(IDeadlockApi api, ICache cache, IEnumerable<IPaceBenchmark> providers, IOptionsMonitor<ScoutSettings> settings,
                           MatchupService? matchups = null, ILogger<BenchmarkWarmer>? log = null)
    {
        _api = api;
        _cache = cache;
        _providers = providers.OrderBy(p => p.Priority).ToList();
        _settings = settings;
        _matchups = matchups;
        _log = log ?? NullLogger<BenchmarkWarmer>.Instance;
    }

    /// <summary>Your most played heroes in recent history: by count, then by the most recent game.</summary>
    public static IReadOnlyList<int> PickHeroes(IEnumerable<MatchHistoryEntry> history, int count, int recentGames = 20)
        => history.OrderByDescending(h => h.StartTime).Take(recentGames)
            .Where(h => h.HeroId > 0)
            .GroupBy(h => h.HeroId)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(h => h.StartTime))
            .Take(Math.Max(0, count))
            .Select(g => g.Key)
            .ToList();

    public async Task<WarmResult?> WarmAsync(uint me, CancellationToken ct)
    {
        var cfg = _settings.CurrentValue.Meter;
        if (me == 0 || cfg.PrefetchHeroes <= 0) return null;

        var history = await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var heroes = PickHeroes(history, cfg.PrefetchHeroes);
        if (heroes.Count == 0) return new WarmResult(RankBracket.All, heroes, 0);

        int? badge = null;
        try
        {
            var ranks = await _cache.GetOrCreateAsync(CacheKeys.Ranks(new[] { me }), TimeSpan.FromHours(12),
                async c => (await _api.RanksAsync(new[] { me }, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
            badge = ranks.FirstOrDefault(r => r.AccountId == me)?.Badge;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Rank unavailable; prefetching for all badges"); }
        var bracket = BracketPolicy.Resolve(cfg, badge);

        if (_matchups is not null)
        {
            try { await _matchups.TableAsync(bracket, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Prefetch of the matchup table for {Bracket} failed", bracket); }
        }

        int ready = 0;
        foreach (var hero in heroes)
            foreach (var p in _providers)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await p.GetAsync(hero, bracket, ct).ConfigureAwait(false) is not null) ready++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Prefetch of {Provider} for hero {Hero} {Bracket} failed", p.Name, hero, bracket); }
            }
        _log.LogInformation("Prefetched {Ready} benchmark curves for heroes {Heroes} in {Bracket}", ready, string.Join(',', heroes), bracket);
        return new WarmResult(bracket, heroes, ready);
    }
}
