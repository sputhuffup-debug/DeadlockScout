using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Tests.Core;

/// <summary>A synthetic lobby: made-up account ids, names and numbers.</summary>
public class TeamComparisonTests
{
    private static RosterPlayer P(uint id, int team, int hero, int? rank) => new(id, $"P{id}", team, hero, rank, null);

    private static ScoutRow Row(RosterPlayer p, int gamesOnHero, double? careerSpm, bool isPrivate = false)
        => new(p, gamesOnHero, 0.5, 100, p.Rank, null, null) { CareerSpmOnHero = careerSpm, Flags = isPrivate ? ScoutFlags.Private : ScoutFlags.None };

    private static PlayerHeroStats S(uint id, int hero, int games, int wins, double? spm) => new(id, hero, games, wins, 0, 0, 0) { NetWorthPerMin = spm };

    [Fact]
    public void A_team_line_pools_its_players_on_the_heroes_they_are_playing()
    {
        var a1 = P(1, Teams.Amber, 10, 72);
        var a2 = P(2, Teams.Amber, 11, 85);
        var a3 = P(3, Teams.Amber, 12, null);
        var s1 = P(4, Teams.Sapphire, 13, 60);
        var rows = new[] { Row(a1, 40, 800), Row(a2, 100, 700), Row(a3, 0, null, isPrivate: true), Row(s1, 5, 900) };
        var recent = new[]
        {
            S(1, 10, 6, 4, 880),  // +10% on their usual
            S(1, 99, 20, 20, 2000), // another hero: ignored
            S(2, 11, 2, 0, 630),  // -10%
            S(4, 13, 3, 1, 900),
        };

        var amber = TeamComparison.Summarize(Teams.Amber, rows, recent);
        Assert.Equal((72, 85), (amber.LowRank, amber.HighRank));
        Assert.Equal(70, amber.AverageGamesOnHero); // the private profile is left out, not counted as zero
        Assert.Equal((4, 4), (amber.RecentWins, amber.RecentLosses));
        // Weighted by recent games: (6·880 + 2·630) / (6·800 + 2·700) - 1.
        Assert.Equal((6 * 880 + 2 * 630) / (6.0 * 800 + 2 * 700) - 1, amber.FormDelta!.Value, 6);

        var sapphire = TeamComparison.Summarize(Teams.Sapphire, rows, recent);
        Assert.Equal((60, 60), (sapphire.LowRank, sapphire.HighRank));
        Assert.Equal(0, sapphire.FormDelta!.Value, 6);

        var none = TeamComparison.Summarize(Teams.Amber, rows, Array.Empty<PlayerHeroStats>());
        Assert.Equal(0, none.RecentGames);
        Assert.Null(none.FormDelta);
        Assert.Null(TeamComparison.Summarize(Teams.Amber, Array.Empty<ScoutRow>(), recent).AverageGamesOnHero);
    }

    [Fact]
    public async Task The_service_makes_one_call_per_match_and_keeps_only_the_current_heroes()
    {
        var api = new RecentApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var svc = new TeamFormService(api, cache, new Monitor());
        var roster = new Roster(42, new[] { P(1, Teams.Amber, 10, 70), P(4, Teams.Sapphire, 13, 70), P(0, Teams.Sapphire, 14, null) }, TimeSpan.Zero);
        var got = await svc.GetAsync(42, roster, CancellationToken.None);
        await svc.GetAsync(42, roster, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        Assert.Equal(new uint[] { 1, 4 }, api.Asked);       // no call for an account id of 0
        Assert.Equal(TeamComparison.RecentDays, api.Days);
        Assert.Equal(new[] { (1u, 10), (4u, 13) }, got.Select(s => (s.AccountId, s.HeroId)));
        Assert.True(cache.Store.ContainsKey("teamform:42"));
    }

    private sealed class Monitor : IOptionsMonitor<ScoutSettings>
    {
        public ScoutSettings CurrentValue { get; } = new();
        public ScoutSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
    }

    private sealed class RecentApi : ScoutingServiceTests.FakeApiBase, DeadlockScout.Core.Abstractions.IDeadlockApi
    {
        public int Calls, Days;
        public uint[] Asked = Array.Empty<uint>();
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlayerHeroStats>> RecentHeroStatsAsync(IEnumerable<uint> accountIds, int days, CancellationToken ct)
        {
            Calls++;
            Days = days;
            Asked = accountIds.ToArray();
            return Task.FromResult<IReadOnlyList<PlayerHeroStats>>(new[] { S(1, 10, 5, 3, 800), S(1, 11, 9, 9, 900), S(4, 13, 2, 1, 700) });
        }
    }
}
