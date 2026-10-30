using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Scouting;

/// <summary>
/// One team at a glance: its rank spread, how much its players have played their heroes, and how they have done on them
/// lately. <see cref="RecentSpm"/> and <see cref="CareerSpm"/> are averaged over the same players, weighted by their
/// recent games, so <see cref="FormDelta"/> compares like with like.
/// </summary>
public sealed record TeamSummary(int Team, int? LowRank, int? HighRank, double? AverageGamesOnHero,
                                 int RecentWins, int RecentLosses, double? RecentSpm, double? CareerSpm)
{
    public int RecentGames => RecentWins + RecentLosses;
    /// <summary>Recent souls per minute on their heroes against their usual on them: +0.04 is 4% faster lately.</summary>
    public double? FormDelta => RecentSpm is { } r && CareerSpm is > 0 ? r / CareerSpm.Value - 1 : null;
}

/// <summary>The scout list's two team lines, from the scouting rows and one bulk call for the last 30 days. Pure, so it is unit tested.</summary>
public static class TeamComparison
{
    public const int RecentDays = 30;

    /// <param name="rows">The lobby's scouting rows (both teams; this picks <paramref name="team"/>'s).</param>
    /// <param name="recent">Per-hero stats of the lobby's players over the last <see cref="RecentDays"/> days; only each player's current hero counts.</param>
    public static TeamSummary Summarize(int team, IReadOnlyList<ScoutRow> rows, IReadOnlyList<PlayerHeroStats> recent)
    {
        var mine = rows.Where(r => r.Player.Team == team).ToList();
        var ranks = mine.Select(r => r.Rank).Where(r => r is > 0).Select(r => r!.Value).ToList();
        // Private profiles have no counts: leave them out rather than count them as zero games.
        var counted = mine.Where(r => !r.Flags.HasFlag(ScoutFlags.Private)).ToList();
        double? onHero = counted.Count == 0 ? null : counted.Average(r => r.GamesOnHero);

        int wins = 0, losses = 0;
        double weight = 0, recentSum = 0, careerSum = 0;
        var byKey = recent.GroupBy(s => (s.AccountId, s.HeroId)).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in mine)
        {
            if (!byKey.TryGetValue((r.Player.AccountId, r.Player.HeroId), out var s) || s.Matches <= 0) continue;
            wins += s.Wins;
            losses += s.Matches - s.Wins;
            if (s.NetWorthPerMin is { } spm && r.CareerSpmOnHero is > 0)
            {
                weight += s.Matches;
                recentSum += s.Matches * spm;
                careerSum += s.Matches * r.CareerSpmOnHero.Value;
            }
        }
        return new TeamSummary(team, ranks.Count == 0 ? null : ranks.Min(), ranks.Count == 0 ? null : ranks.Max(), onHero,
                               wins, losses, weight > 0 ? recentSum / weight : null, weight > 0 ? careerSum / weight : null);
    }
}

/// <summary>
/// The lobby's last 30 days on their heroes: one players/hero-stats call for all twelve (the same bulk endpoint scouting
/// uses, with a time filter), cached per match so a restart mid-match does not repeat it.
/// </summary>
public sealed class TeamFormService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public TeamFormService(IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings)
    {
        _api = api;
        _cache = cache;
        _settings = settings;
    }

    public async Task<IReadOnlyList<PlayerHeroStats>> GetAsync(long matchId, Roster roster, CancellationToken ct)
    {
        var players = roster.Players.Where(p => p.AccountId != 0 && p.HeroId > 0).ToList();
        if (players.Count == 0) return Array.Empty<PlayerHeroStats>();
        var heroes = players.Select(p => (p.AccountId, p.HeroId)).ToHashSet();
        async Task<List<PlayerHeroStats>> Fetch(CancellationToken c) =>
            (await _api.RecentHeroStatsAsync(players.Select(p => p.AccountId), TeamComparison.RecentDays, c).ConfigureAwait(false))
            .Where(s => heroes.Contains((s.AccountId, s.HeroId))).ToList(); // only the heroes they are on
        if (matchId == 0) return await Fetch(ct).ConfigureAwait(false);
        var ttl = TimeSpan.FromHours(_settings.CurrentValue.Cache.SharedHistoryTtlHours);
        return await _cache.GetOrCreateAsync(CacheKeys.TeamForm(matchId), ttl, Fetch, staleWhileRevalidate: false, ct).ConfigureAwait(false);
    }
}
