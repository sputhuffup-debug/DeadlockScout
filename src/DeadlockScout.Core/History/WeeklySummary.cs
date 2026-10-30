using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.History;

/// <summary>Your souls a minute in one phase, last week against the week before (null when a week had no game reaching it).</summary>
public sealed record PhaseChange(GamePhase Phase, double? Before, double? After)
{
    public double? Delta => Before is > 0 && After is { } a ? a / Before.Value - 1 : null;
}

/// <summary>An item you bought in both weeks: your average first purchase in each (after the first minute).</summary>
public sealed record ItemChange(long ItemId, TimeSpan Before, TimeSpan After, int Games)
{
    /// <summary>Negative: you got it earlier last week.</summary>
    public TimeSpan Shift => After - Before;
}

/// <summary>What the next week should be about, and why.</summary>
public enum FocusKind { Target, Stash, Phase, Item, KeepGoing }

/// <param name="Text">The advice; an item's name goes where <c>{item}</c> stands (the app knows the names).</param>
public sealed record WeeklyFocus(FocusKind Kind, string Text, long? ItemId = null);

/// <summary>The last full week (Monday to Sunday, local time) against the week before. Derived whenever shown, never stored.</summary>
public sealed record WeeklyReport(
    DateOnly Monday,
    int Games, int Wins, int Losses,
    int? RankStart, int? RankEnd,
    double? Spm, double? PreviousSpm,
    IReadOnlyList<PhaseChange> Phases,
    PhaseChange? BestPhase,
    ItemChange? BestItem,
    WeeklyFocus Focus)
{
    public double? SpmDelta => PreviousSpm is > 0 && Spm is { } s ? s / PreviousSpm.Value - 1 : null;
}

/// <summary>
/// The weekly summary in the main window: the last full week's games, rank change and souls a minute against the week
/// before, the phase and the item that improved most, and the one thing to work on next week. Built from the match
/// history and its cached details (soul timelines, purchases, deaths). Pure, so it is unit tested.
/// </summary>
public static class WeeklySummary
{
    /// <summary>A phase or an item needs this many games in each week to be compared.</summary>
    public const int MinGames = 2;
    /// <summary>A phase change smaller than this is noise, not an improvement or a drop.</summary>
    public const double PhaseNoise = 0.03;
    /// <summary>An item has to move this much to be worth naming.</summary>
    public static readonly TimeSpan ItemNoise = TimeSpan.FromSeconds(30);
    /// <summary>A target hit in less than this share of last week's games is the focus.</summary>
    public const double TargetMiss = 0.5;

    /// <summary>The Monday 00:00 (in <paramref name="zone"/>) that starts the last full week before <paramref name="now"/>.</summary>
    public static DateTimeOffset LastWeekStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        int sinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var thisMonday = local.Date.AddDays(-sinceMonday);
        var lastMonday = thisMonday.AddDays(-7);
        return new DateTimeOffset(lastMonday, zone.GetUtcOffset(lastMonday));
    }

    /// <summary>
    /// The summary of the last full week; null when you played no game in it. <paramref name="games"/> are the history's
    /// games (newest first) with whatever details are cached; <paramref name="cost"/> prices items for the death review.
    /// </summary>
    public static WeeklyReport? Build(IReadOnlyList<HistoryGame> games, DateTimeOffset now, TimeZoneInfo zone, TargetSettings targets, Func<long, int> cost)
    {
        var start = LastWeekStart(now, zone);
        var end = start.AddDays(7);
        var week = games.Where(g => g.Entry.StartTime >= start && g.Entry.StartTime < end).OrderBy(g => g.Entry.StartTime).ToList();
        if (week.Count == 0) return null;
        var before = games.Where(g => g.Entry.StartTime >= start.AddDays(-7) && g.Entry.StartTime < start).ToList();

        // The rank going into the week is the last ranked game before it, else the week's first.
        var ranked = games.Where(g => g.Entry.RankedBadge is > 0 && g.Entry.StartTime < end).OrderBy(g => g.Entry.StartTime).ToList();
        int? rankEnd = ranked.LastOrDefault()?.Entry.RankedBadge;
        int? rankStart = ranked.LastOrDefault(g => g.Entry.StartTime < start)?.Entry.RankedBadge
                         ?? ranked.FirstOrDefault(g => g.Entry.StartTime >= start)?.Entry.RankedBadge;

        var phases = new[] { GamePhase.Laning, GamePhase.Mid, GamePhase.Late }
            .Select(p => new PhaseChange(p, PhaseSpm(before, p), PhaseSpm(week, p))).ToList();
        var bestPhase = phases.Where(p => p.Delta is > PhaseNoise).MaxBy(p => p.Delta);
        var items = ItemChanges(before, week);
        var bestItem = items.Where(i => i.Shift <= -ItemNoise).MinBy(i => i.Shift);

        return new WeeklyReport(DateOnly.FromDateTime(start.Date), week.Count, week.Count(g => g.Entry.IsWin), week.Count(g => g.Entry.IsLoss),
            rankStart, rankEnd, Spm(week), before.Count > 0 ? Spm(before) : null, phases, bestPhase, bestItem,
            Focus(week, targets, cost, phases, items));
    }

    /// <summary>Souls a minute over whole games, pooled.</summary>
    private static double? Spm(IReadOnlyList<HistoryGame> games)
    {
        double minutes = games.Sum(g => g.Entry.Duration.TotalMinutes);
        return minutes > 0 ? games.Sum(g => (double)g.Entry.NetWorth) / minutes : null;
    }

    /// <summary>Souls a minute in <paramref name="phase"/> over the games whose timeline covers enough of it, pooled.</summary>
    public static double? PhaseSpm(IReadOnlyList<HistoryGame> games, GamePhase phase)
    {
        double souls = 0, minutes = 0;
        int counted = 0;
        foreach (var g in games)
        {
            var series = g.Detail?.Souls;
            if (series is not { Count: >= 2 }) continue;
            var from = phase switch { GamePhase.Laning => TimeSpan.Zero, GamePhase.Mid => Phases.LaningEnd, _ => Phases.MidEnd };
            var to = Phases.End(phase) ?? TimeSpan.MaxValue;
            var until = new[] { to, g.Entry.Duration, series[^1].GameTime }.Min();
            if (until - from < Phases.MinLength) continue;
            souls += CurveBuilder.InterpolateSeries(series, until) - CurveBuilder.InterpolateSeries(series, from);
            minutes += (until - from).TotalMinutes;
            counted++;
        }
        return counted >= MinGames && minutes > 0 ? souls / minutes : null;
    }

    /// <summary>Items bought (after the first minute) in at least <see cref="MinGames"/> games of each week, with your average first purchase in each.</summary>
    public static IReadOnlyList<ItemChange> ItemChanges(IReadOnlyList<HistoryGame> before, IReadOnlyList<HistoryGame> after)
    {
        static Dictionary<long, List<TimeSpan>> FirstBuys(IEnumerable<HistoryGame> games)
        {
            var map = new Dictionary<long, List<TimeSpan>>();
            foreach (var g in games)
            foreach (var first in (g.Detail?.Purchases ?? Array.Empty<MatchItem>()).Where(p => p.BoughtAt >= ItemTimings.StartingItems)
                         .GroupBy(p => p.ItemId).Select(x => (Id: x.Key, At: x.Min(p => p.BoughtAt))))
            {
                if (!map.TryGetValue(first.Id, out var list)) map[first.Id] = list = new List<TimeSpan>();
                list.Add(first.At);
            }
            return map;
        }
        var b = FirstBuys(before);
        var a = FirstBuys(after);
        return a.Where(kv => kv.Value.Count >= MinGames && b.TryGetValue(kv.Key, out var old) && old.Count >= MinGames)
            .Select(kv => new ItemChange(kv.Key, Average(b[kv.Key]), Average(kv.Value), kv.Value.Count))
            .ToList();
    }

    private static TimeSpan Average(List<TimeSpan> xs) => TimeSpan.FromTicks((long)xs.Average(x => x.Ticks));

    /// <summary>
    /// The one thing to work on, in this order: a target of yours missed in most of last week's games; dying with a big
    /// item's worth unspent in a quarter of deaths or more; the phase that dropped most; the item that slipped most.
    /// </summary>
    private static WeeklyFocus Focus(IReadOnlyList<HistoryGame> week, TargetSettings targets, Func<long, int> cost,
                                     IReadOnlyList<PhaseChange> phases, IReadOnlyList<ItemChange> items)
    {
        if (targets.Any)
        {
            // The phase target needs a rank gate the summary does not load, so only the other two are judged here.
            var checks = week.Select(g => (IReadOnlyList<TargetCheck>)Targets.Check(targets with { FixPhase = null },
                HistoryMath.FromZero(g.Detail?.Souls ?? Array.Empty<NetWorthPoint>()), g.Entry.Duration, g.Detail?.DeathTimes, Array.Empty<PhaseResult>())).ToList();
            var worst = Targets.Rates(checks).Where(r => r.Games >= MinGames && r.Rate < TargetMiss).MinBy(r => r.Rate);
            if (worst is not null)
                return new WeeklyFocus(FocusKind.Target, $"{Targets.Describe(worst.Kind, targets)}: hit in {worst.Hits} of {worst.Games} games last week.");
        }
        var deaths = DeathReview.Total(week.Where(g => g.Detail?.DeathTimes is not null)
            .Select(g => DeathReview.Of(g.Detail!.DeathTimes, HistoryMath.FromZero(g.Detail.Souls ?? Array.Empty<NetWorthPoint>()),
                g.Detail.Purchases ?? Array.Empty<MatchItem>(), cost, null)).ToList());
        if (deaths.WithUnspent >= 4 && deaths.BigStashShare >= DeathReview.BigStashCallout)
            return new WeeklyFocus(FocusKind.Stash,
                $"Spend before you fight: {deaths.BigStash} of {deaths.WithUnspent} deaths came with {DeathReview.BigStash:N0} or more souls unspent.");
        var dropped = phases.Where(p => p.Delta is < -PhaseNoise).MinBy(p => p.Delta);
        if (dropped is not null)
            return new WeeklyFocus(FocusKind.Phase,
                $"{Phases.Name(dropped.Phase)}: {dropped.After:0} souls a minute, {Math.Abs(dropped.Delta!.Value) * 100:0}% down on the week before.");
        var slipped = items.Where(i => i.Shift >= ItemNoise).MaxBy(i => i.Shift);
        if (slipped is not null)
            return new WeeklyFocus(FocusKind.Item, $"Get {{item}} earlier: {Clock(slipped.After)} last week, {Clock(slipped.Shift)} later than the week before.", slipped.ItemId);
        return new WeeklyFocus(FocusKind.KeepGoing, "Nothing slipped against the week before: keep the same plan, or raise a target.");
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
