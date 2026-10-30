using System.Text.Json;
using DeadlockScout.Api.Dto;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

/// <summary>A made-up game: souls rising 1,000 a minute, three items, made-up costs.</summary>
public class DeathReviewTests
{
    private static readonly IReadOnlyList<NetWorthPoint> Souls =
        Enumerable.Range(0, 31).Select(m => new NetWorthPoint(TimeSpan.FromMinutes(m), m * 1000)).ToList();

    private static readonly Dictionary<long, int> Cost = new() { [1] = 3200, [2] = 6400, [3] = 1600 };
    private static int CostOf(long id) => Cost.GetValueOrDefault(id);

    private static readonly IReadOnlyList<MatchItem> Bought = new[]
    {
        new MatchItem(3, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(12)), // sold at 12:00
        new MatchItem(1, TimeSpan.FromMinutes(5), null),
        new MatchItem(2, TimeSpan.FromMinutes(14), null),
    };

    /// <summary>A rank whose curve gains 900 souls a minute.</summary>
    private static readonly PaceCurve Gate = new(13, new RankBracket(80, 89), "p",
        Enumerable.Range(0, 7).Select(i => new PacePoint(i * 5, null, null, null, i * 5 * 900)).ToList(), 100, "analytics");

    [Fact]
    public void Each_death_reads_the_souls_unspent_and_what_the_gate_gained_meanwhile()
    {
        var deaths = new[] { new DeathRecord(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)), new DeathRecord(TimeSpan.FromMinutes(13), TimeSpan.FromSeconds(40)) };
        var r = DeathReview.Of(deaths, Souls, Bought, CostOf, Gate);
        // 10:00: 10,000 souls, holding the 1,600 and the 3,200 items.
        Assert.Equal(10_000 - 1600 - 3200, r[0].Unspent);
        // 13:00: the 1,600 item was sold at 12:00; the 6,400 not bought yet.
        Assert.Equal(13_000 - 3200, r[1].Unspent);
        Assert.Equal(900 * 20 / 60.0, r[0].GateGained!.Value, 6);
        Assert.Equal(900 * 40 / 60.0, r[1].GateGained!.Value, 6);

        // No gate: unspent only. No timeline: nothing to read.
        Assert.Null(DeathReview.Of(deaths, Souls, Bought, CostOf, null)[0].GateGained);
        Assert.Null(DeathReview.Of(deaths, Array.Empty<NetWorthPoint>(), Bought, CostOf, null)[0].Unspent);
        Assert.Empty(DeathReview.Of(null, Souls, Bought, CostOf, Gate));
        // Never below zero, whatever the sampling between timeline points makes of it.
        Assert.Equal(0, DeathReview.Of(new[] { new DeathRecord(TimeSpan.FromMinutes(5), TimeSpan.Zero) }, Souls,
            new[] { new MatchItem(2, TimeSpan.FromMinutes(1), null) }, CostOf, null)[0].Unspent);
    }

    [Fact]
    public void The_page_pools_deaths_and_counts_a_game_without_one_as_nothing_lost()
    {
        DeathResult D(int unspent, double gained) => new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), unspent, gained);
        var games = new IReadOnlyList<DeathResult>[]
        {
            new[] { D(4000, 600), D(1000, 400) },
            new[] { D(3500, 1000) },
            Array.Empty<DeathResult>(), // a clean game
        };
        var t = DeathReview.Total(games);
        Assert.Equal(3, t.Games);
        Assert.Equal(1.0, t.DeathsPerGame, 6);
        Assert.Equal(TimeSpan.FromSeconds(30), t.DeadPerGame);
        Assert.Equal(2000 / 3.0, t.GateGainedPerGame!.Value, 6); // the clean game counts as 0
        Assert.Equal(8500 / 3.0, t.AverageUnspent!.Value, 6);
        Assert.Equal(2, t.BigStash);
        Assert.True(t.BigStashShare >= DeathReview.BigStashCallout);

        var noGate = DeathReview.Total(new IReadOnlyList<DeathResult>[] { new[] { new DeathResult(TimeSpan.Zero, TimeSpan.FromSeconds(10), 500, null) } });
        Assert.Null(noGate.GateGainedPerGame);
    }

    [Fact]
    public void The_parser_reads_deaths_from_the_flat_columns_and_the_object_list()
    {
        using var flat = JsonDocument.Parse("""{"death_details_game_time_s": [1174, 1005], "death_details_death_duration_s": [49, 45]}""");
        Assert.Equal(new[] { new DeathRecord(TimeSpan.FromSeconds(1005), TimeSpan.FromSeconds(45)), new DeathRecord(TimeSpan.FromSeconds(1174), TimeSpan.FromSeconds(49)) },
            MetadataParser.ParseDeaths(flat.RootElement));
        using var objects = JsonDocument.Parse("""{"death_details": [{"game_time_s": 97, "killer_player_slot": 4, "death_duration_s": 8, "time_to_kill_s": 18.6}]}""");
        Assert.Equal(new DeathRecord(TimeSpan.FromSeconds(97), TimeSpan.FromSeconds(8)), MetadataParser.ParseDeaths(objects.RootElement)!.Single());
        using var clean = JsonDocument.Parse("""{"death_details_game_time_s": [], "death_details_death_duration_s": []}""");
        Assert.Empty(MetadataParser.ParseDeaths(clean.RootElement)!);
        using var none = JsonDocument.Parse("""{"deaths": 3}""");
        Assert.Null(MetadataParser.ParseDeaths(none.RootElement)); // not asked for: unknown, not "no deaths"
        Assert.Contains("death_details.game_time_s,death_details.death_duration_s", MetadataParser.Columns);
    }
}
