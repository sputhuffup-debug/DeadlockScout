using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class ItemTimingsTests
{
    private const long Headhunter = 1, Leech = 2, Starter = 3, Rare = 4;

    private static readonly IReadOnlyDictionary<long, ItemTiming> Oracle = new Dictionary<long, ItemTiming>
    {
        [Headhunter] = new(Headhunter, 900, 750),  // 12:30
        [Leech] = new(Leech, 400, 1380),           // 23:00
        [Starter] = new(Starter, 3000, 20),        // bought in the base
        [Rare] = new(Rare, 12, 600),               // too few matches to compare with
    };

    private static MatchItem Buy(long id, int seconds, int? sold = null)
        => new(id, TimeSpan.FromSeconds(seconds), sold is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void Compares_first_purchases_with_the_rank_and_skips_starters_rebuys_and_thin_items()
    {
        var results = ItemTimings.Compare(new[]
        {
            Buy(Starter, 24), Buy(Headhunter, 852, sold: 1500), Buy(Rare, 500), Buy(Leech, 1300), Buy(Headhunter, 1700), Buy(99, 900),
        }, Oracle);
        Assert.Equal(new long[] { Headhunter, Leech }, results.Select(r => r.ItemId));
        Assert.Equal(TimeSpan.FromSeconds(102), results[0].Difference); // 14:12 against 12:30
        Assert.Equal(TimeSpan.FromSeconds(-80), results[1].Difference);
        Assert.Equal(TimeSpan.FromSeconds(11), ItemTimings.AverageGap(results));
        Assert.Null(ItemTimings.AverageGap(Array.Empty<ItemTimingResult>()));
    }

    [Fact]
    public void Totals_average_each_item_over_the_games_and_name_the_latest()
    {
        var games = new[]
        {
            ItemTimings.Compare(new[] { Buy(Headhunter, 900), Buy(Leech, 1400) }, Oracle),
            ItemTimings.Compare(new[] { Buy(Headhunter, 960) }, Oracle),
        };
        var totals = ItemTimings.Total(games);
        Assert.Equal(Headhunter, totals[0].ItemId);
        Assert.Equal(2, totals[0].Games);
        Assert.Equal(TimeSpan.FromSeconds(930), totals[0].You);
        Assert.Equal(TimeSpan.FromSeconds(180), totals[0].Difference);
        Assert.Equal(Headhunter, ItemTimings.Latest(totals)!.ItemId);
        Assert.Null(ItemTimings.Latest(totals, minGames: 3));
    }
}
