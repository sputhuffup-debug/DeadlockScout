using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>Where souls come from, as the match history groups them.</summary>
public enum SoulSource { Troopers, Jungle, Kills, Objectives, Other }

/// <summary>A rank's souls a minute from each source on one hero, from a sample of its matches; <see cref="Players"/> is the sample.</summary>
public sealed record SourceRates(int HeroId, IReadOnlyDictionary<SoulSource, double> PerMinute, double LostOnDeathPerMinute, int Players);

/// <summary>One source in one game against the rank: your souls from it, and the rank's rate over the same minutes.</summary>
public sealed record SourceResult(SoulSource Source, int Gained, double Expected, double Minutes)
{
    public double? Delta => Expected > 0 ? Gained / Expected - 1 : null;
    public double Spm => Minutes > 0 ? Gained / Minutes : 0;
    public double ExpectedSpm => Minutes > 0 ? Expected / Minutes : 0;
}

/// <summary>A source over many games, pooled: total souls against total expected, so long games weigh by their minutes.</summary>
public sealed record SourceTotal(SoulSource Source, int Games, double Gained, double Expected, double Minutes)
{
    public double? Delta => Expected > 0 ? Gained / Expected - 1 : null;
    public double Spm => Minutes > 0 ? Gained / Minutes : 0;
    public double ExpectedSpm => Minutes > 0 ? Expected / Minutes : 0;
    /// <summary>Souls a game you are short of the rank from this source (negative: ahead of it).</summary>
    public double ShortfallPerGame => Games > 0 ? (Expected - Gained) / Games : 0;
}

/// <summary>
/// Splits a game's souls by source (troopers, jungle, kills, objectives, the rest) and sets each against a rank's rate on
/// the hero, so a run of games shows where the souls you are missing would come from. Pure, so it is unit tested.
/// </summary>
public static class SoulSourceStats
{
    /// <summary>Games a source needs before the summary names it the costliest.</summary>
    public const int MinGamesForWeakest = 3;
    /// <summary>A game shorter than this (a remake) says nothing about where souls come from.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(5);

    public static readonly SoulSource[] All = { SoulSource.Troopers, SoulSource.Jungle, SoulSource.Kills, SoulSource.Objectives, SoulSource.Other };

    public static string Name(SoulSource source) => source switch
    {
        SoulSource.Troopers => "Troopers",
        SoulSource.Jungle => "Jungle",
        SoulSource.Kills => "Kills",
        SoulSource.Objectives => "Objectives",
        _ => "Other",
    };

    /// <summary>What each group holds, for tooltips.</summary>
    public static string Holds(SoulSource source) => source switch
    {
        SoulSource.Troopers => "lane troopers, denies included",
        SoulSource.Jungle => "neutral camps",
        SoulSource.Kills => "hero kills (assists are under Other)",
        SoulSource.Objectives => "guardians, walkers, the mid boss and the urn",
        _ => "assists, team bonuses, crates, items and souls the match record does not itemize",
    };

    /// <summary>A player's souls by group.</summary>
    public static IReadOnlyDictionary<SoulSource, int> Split(SoulSources s, int netWorth) => new Dictionary<SoulSource, int>
    {
        [SoulSource.Troopers] = s.Troopers + s.Denies,
        [SoulSource.Jungle] = s.Jungle,
        [SoulSource.Kills] = s.Kills,
        [SoulSource.Objectives] = s.Bosses + s.Treasure,
        [SoulSource.Other] = s.Other(netWorth),
    };

    /// <summary>
    /// The rank's rate from a sample of its matches: every player on <paramref name="heroId"/> with souls by source in a
    /// game of at least <see cref="MinDuration"/>, pooled (total souls over total minutes). Null without any.
    /// </summary>
    public static SourceRates? Rates(int heroId, IReadOnlyList<MatchMetadata> sample)
    {
        double minutes = 0, lost = 0;
        int players = 0;
        var sums = All.ToDictionary(s => s, _ => 0.0);
        foreach (var m in sample)
        {
            if (m.Duration < MinDuration) continue;
            foreach (var p in m.Players)
            {
                if (p.HeroId != heroId || p.Sources is not { } src) continue;
                players++;
                minutes += m.Duration.TotalMinutes;
                lost += src.LostOnDeath;
                foreach (var (source, souls) in Split(src, p.NetWorth)) sums[source] += souls;
            }
        }
        if (players == 0 || minutes <= 0) return null;
        return new SourceRates(heroId, sums.ToDictionary(kv => kv.Key, kv => kv.Value / minutes), lost / minutes, players);
    }

    /// <summary>One game's sources against the rank's rates over its length; empty without sources or for a remake.</summary>
    public static IReadOnlyList<SourceResult> Of(SoulSources? sources, int netWorth, TimeSpan duration, SourceRates? rates)
    {
        if (sources is null || rates is null || duration < MinDuration) return Array.Empty<SourceResult>();
        double minutes = duration.TotalMinutes;
        var split = Split(sources, netWorth);
        return All.Select(s => new SourceResult(s, split[s], rates.PerMinute.GetValueOrDefault(s) * minutes, minutes)).ToList();
    }

    /// <summary>Each source pooled over <paramref name="games"/>, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<SourceTotal> Total(IEnumerable<IReadOnlyList<SourceResult>> games)
        => games.SelectMany(g => g)
            .GroupBy(r => r.Source)
            .OrderBy(g => Array.IndexOf(All, g.Key))
            .Select(g => new SourceTotal(g.Key, g.Count(), g.Sum(r => (double)r.Gained), g.Sum(r => r.Expected), g.Sum(r => r.Minutes)))
            .ToList();

    /// <summary>
    /// The source that costs you the most souls a game against the rank, among those with enough games; null when you
    /// are level or ahead everywhere. By souls, not percent: a small source far behind matters less than a big one a little behind.
    /// </summary>
    public static SourceTotal? Costliest(IReadOnlyList<SourceTotal> totals)
        => totals.Where(t => t.Games >= MinGamesForWeakest && t.ShortfallPerGame > 0).OrderByDescending(t => t.ShortfallPerGame).FirstOrDefault();
}

/// <summary>
/// A rank's souls by source on a hero: one bulk metadata call (the same sample the metadata pace benchmark uses, with the
/// gold columns), reduced on fetch and cached for the patch. Background priority, like the sample itself.
/// </summary>
public sealed class SoulSourceService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly TimeSpan _ttl;
    private readonly int _sampleSize;

    public SoulSourceService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, TimeSpan ttl, int sampleSize)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _ttl = ttl;
        _sampleSize = sampleSize;
    }

    public async Task<SourceRates?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        // A rank with no sample is cached too (as an empty rate), so a window does not ask again for it.
        var rates = await _cache.GetOrCreateAsync(CacheKeys.SourceRates(heroId, bracket, patch), _ttl,
            async c => SoulSourceStats.Rates(heroId, await _api.SampleMatchesAsync(heroId, bracket, _sampleSize, c).ConfigureAwait(false))
                       ?? new SourceRates(heroId, new Dictionary<SoulSource, double>(), 0, 0),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
        return rates.Players > 0 ? rates : null;
    }
}
