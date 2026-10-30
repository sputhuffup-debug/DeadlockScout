using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

/// <summary>Made-up games in UTC: "now" is Friday Oct 30, 2026, so last week is Mon Oct 19 to Sun Oct 25.</summary>
public class WeeklySummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly Dictionary<long, int> Cost = new() { [1] = 3200, [2] = 6400 };
    private static int CostOf(long id) => Cost.GetValueOrDefault(id);

    /// <summary>A 30-minute game on a day of October 2026, earning <paramref name="laning"/> a minute to 10:00 and <paramref name="later"/> after.</summary>
    private static HistoryGame G(int day, bool won, int laning, int later, int? badge = null, double headhunterAt = 15,
                                 IReadOnlyList<DeathRecord>? deaths = null)
    {
        var souls = Enumerable.Range(0, 31).Select(m => new NetWorthPoint(TimeSpan.FromMinutes(m), m <= 10 ? m * laning : 10 * laning + (m - 10) * later)).ToList();
        int nw = souls[^1].NetWorth;
        var start = new DateTimeOffset(2026, 10, day, 20, 0, 0, TimeSpan.Zero);
        var entry = new MatchHistoryEntry(day * 10 + (won ? 1 : 0), 13, start, TimeSpan.FromMinutes(30), Teams.Amber, 5, 3, 7, nw, won ? 1 : 2, badge, null,
            won ? Teams.Amber : Teams.Sapphire);
        var detail = new MatchDetail(entry.MatchId, 150, 5, 30, Array.Empty<long>(), 1, 6, 70, Array.Empty<HistoryPlayer>(), Array.Empty<HistoryPlayer>(), souls,
            new[] { new MatchItem(1, TimeSpan.FromMinutes(headhunterAt), null), new MatchItem(2, TimeSpan.FromMinutes(25), null) },
            null, deaths ?? Array.Empty<DeathRecord>());
        return new HistoryGame(entry, detail);
    }

    [Fact]
    public void Last_week_runs_from_the_monday_before_this_one()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), WeeklySummary.LastWeekStart(Now, Utc));
        // On a Monday, last week is the one that just ended.
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), WeeklySummary.LastWeekStart(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), Utc));
        // A Sunday belongs to the week it ends.
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), WeeklySummary.LastWeekStart(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), Utc));
    }

    [Fact]
    public void The_week_is_set_against_the_one_before()
    {
        var games = new[]
        {
            G(28, true, 900, 900, 75),                     // this week: not in the summary
            G(24, true, 520, 800, 74, headhunterAt: 14),   // last week
            G(22, false, 500, 800, 73, headhunterAt: 14.5),
            G(20, true, 510, 800, 73, headhunterAt: 13.5),
            G(16, false, 450, 820, 72, headhunterAt: 15.5), // the week before
            G(14, true, 460, 820, null, headhunterAt: 16),
        };
        var r = WeeklySummary.Build(games, Now, Utc, new TargetSettings(), CostOf)!;
        Assert.Equal(new DateOnly(2026, 10, 19), r.Monday);
        Assert.Equal((3, 2, 1), (r.Games, r.Wins, r.Losses));
        Assert.Equal((72, 74), (r.RankStart, r.RankEnd)); // into the week from the last ranked game before it
        Assert.Equal(GamePhase.Laning, r.BestPhase!.Phase);
        Assert.Equal(510, r.BestPhase.After!.Value, 6);
        Assert.Equal(455, r.BestPhase.Before!.Value, 6);
        Assert.Equal(1, r.BestItem!.ItemId);
        Assert.Equal(TimeSpan.FromMinutes(14) - TimeSpan.FromMinutes(15.75), r.BestItem.Shift);
        Assert.True(r.SpmDelta > 0);
        Assert.Null(WeeklySummary.Build(games.Take(1).ToList(), Now, Utc, new TargetSettings(), CostOf)); // no games last week
    }

    [Fact]
    public void The_focus_takes_a_missed_target_first_then_a_dropped_phase_then_a_slipped_item()
    {
        var before = new[] { G(16, true, 500, 900), G(14, true, 500, 900) };
        var week = new[] { G(24, true, 420, 820, headhunterAt: 17), G(22, true, 430, 830, headhunterAt: 17.5) };
        var games = week.Concat(before).ToList();

        var target = WeeklySummary.Build(games, Now, Utc, new TargetSettings { LaningSpm = 480 }, CostOf)!;
        Assert.Equal(FocusKind.Target, target.Focus.Kind);
        Assert.Contains("hit in 0 of 2", target.Focus.Text);

        var phase = WeeklySummary.Build(games, Now, Utc, new TargetSettings(), CostOf)!;
        Assert.Equal(FocusKind.Phase, phase.Focus.Kind);
        Assert.StartsWith("Laning", phase.Focus.Text); // laning dropped most (-15%) against mid and late (-9%)

        var flat = new[] { G(24, true, 500, 900, headhunterAt: 17), G(22, true, 500, 900, headhunterAt: 17.5) }.Concat(before).ToList();
        var item = WeeklySummary.Build(flat, Now, Utc, new TargetSettings(), CostOf)!;
        Assert.Equal(FocusKind.Item, item.Focus.Kind);
        Assert.Equal(1, item.Focus.ItemId);
        Assert.Contains("{item}", item.Focus.Text);

        var same = WeeklySummary.Build(new[] { G(24, true, 500, 900), G(22, true, 500, 900) }.Concat(before).ToList(), Now, Utc, new TargetSettings(), CostOf)!;
        Assert.Equal(FocusKind.KeepGoing, same.Focus.Kind);
    }

    [Fact]
    public void Dying_with_a_big_stash_is_the_focus_before_a_phase()
    {
        // Deaths at 9:00, 19:00 and 29:00: holding one 3,200 item from 15:00, so 4,500 then 11,000+ souls unspent.
        var deaths = new[] { new DeathRecord(TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(15)), new DeathRecord(TimeSpan.FromMinutes(19), TimeSpan.FromSeconds(30)) };
        var games = new[] { G(24, true, 500, 800, deaths: deaths), G(22, true, 500, 800, deaths: deaths), G(16, true, 600, 900), G(14, true, 600, 900) };
        var r = WeeklySummary.Build(games, Now, Utc, new TargetSettings(), CostOf)!;
        Assert.Equal(FocusKind.Stash, r.Focus.Kind);
        Assert.Contains("4 of 4 deaths", r.Focus.Text);
    }
}
