using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>
/// How a game went against your build plan for its hero (0.7): of the plan's first <see cref="Considered"/> items (as many
/// as you bought big items that game, so a short game is not blamed for the late ones), how many you bought, and the
/// first one you skipped or bought out of order (<see cref="OffAt"/>, null when you kept to it).
/// </summary>
public sealed record PlanCheck(int Considered, int Kept, long? OffAt)
{
    public double Score => Considered == 0 ? 0 : (double)Kept / Considered;
    /// <summary>Kept to the plan: three in four of its items that the game had room for.</summary>
    public bool Followed => Considered > 0 && Score >= BuildPlans.FollowedShare;
}

/// <summary>A hero's games over the page, split by whether they followed the plan: wins and games each way.</summary>
public sealed record PlanRecord(int HeroId, int FollowedWins, int FollowedGames, int OtherWins, int OtherGames);

/// <summary>
/// Your own item order per hero (0.7), kept in settings.json under <see cref="Settings.ScoutSettings.BuildPlans"/> by hero
/// id, written in the match history and checked against every game on that hero. Local only. Pure, so tested.
/// </summary>
public static class BuildPlans
{
    /// <summary>Items in a plan at most: a full build.</summary>
    public const int MaxItems = 12;
    /// <summary>The share of the plan's items a game must keep to count as following it.</summary>
    public const double FollowedShare = 0.75;

    /// <summary>A plan as stored: distinct item ids, in order, at most <see cref="MaxItems"/>.</summary>
    public static List<long> Clean(IEnumerable<long>? items)
        => (items ?? Array.Empty<long>()).Where(i => i > 0).Distinct().Take(MaxItems).ToList();

    /// <summary>A copy of <paramref name="plans"/> with <paramref name="heroId"/>'s plan set; an empty plan removes it.</summary>
    public static Dictionary<int, List<long>> With(IReadOnlyDictionary<int, List<long>>? plans, int heroId, IEnumerable<long>? items)
    {
        var copy = new Dictionary<int, List<long>>();
        foreach (var (hero, plan) in plans ?? new Dictionary<int, List<long>>())
            if (hero > 0 && hero != heroId && Clean(plan) is { Count: > 0 } p) copy[hero] = p;
        if (heroId > 0 && Clean(items) is { Count: > 0 } mine) copy[heroId] = mine;
        return copy;
    }

    /// <summary>True when both hold the same plans (settings records compare the dictionary by reference).</summary>
    public static bool Same(IReadOnlyDictionary<int, List<long>>? a, IReadOnlyDictionary<int, List<long>>? b)
    {
        a ??= new Dictionary<int, List<long>>();
        b ??= new Dictionary<int, List<long>>();
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));
    }

    /// <summary>A game's build as a plan: its big items (by <paramref name="cost"/>) in the order bought, sold ones included.</summary>
    public static List<long> FromGame(IEnumerable<MatchItem> purchases, Func<long, int> cost)
        => Clean(purchases.Where(p => cost(p.ItemId) >= BuildReview.MinCost).OrderBy(p => p.BoughtAt).Select(p => p.ItemId));

    /// <summary>How a game kept to <paramref name="plan"/>; null without a plan or without a big item bought.</summary>
    public static PlanCheck? Check(IReadOnlyList<long>? plan, IEnumerable<MatchItem> purchases, Func<long, int> cost)
    {
        if (plan is not { Count: > 0 }) return null;
        var bought = FromGame(purchases, cost);
        if (bought.Count == 0) return null;
        int considered = Math.Min(plan.Count, bought.Count);
        var wanted = plan.Take(considered).ToList();
        int kept = wanted.Count(bought.Contains);
        // Off plan at the first place where your buys of the plan's items stop matching it: the item due there was skipped
        // or bought later than another one.
        var inOrder = bought.Where(wanted.Contains).ToList();
        long? off = null;
        for (int i = 0; i < wanted.Count; i++)
            if (i >= inOrder.Count || inOrder[i] != wanted[i]) { off = wanted[i]; break; }
        return new PlanCheck(considered, kept, off);
    }

    /// <summary>The record with and without the plan per hero, from (hero, won, check) per game; heroes without a check are left out.</summary>
    public static IReadOnlyList<PlanRecord> Records(IEnumerable<(int HeroId, bool? Won, PlanCheck? Check)> games)
        => games.Where(g => g.Check is not null && g.Won is not null)
            .GroupBy(g => g.HeroId)
            .Select(h => new PlanRecord(h.Key,
                h.Count(g => g.Check!.Followed && g.Won == true), h.Count(g => g.Check!.Followed),
                h.Count(g => !g.Check!.Followed && g.Won == true), h.Count(g => !g.Check!.Followed)))
            .OrderByDescending(r => r.FollowedGames + r.OtherGames).ThenBy(r => r.HeroId).ToList();
}
