using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// One death in a finished game: when, for how long, about how many souls you were carrying unspent, and how many souls
/// the rank gate's curve gained while you waited. Derived whenever it is shown, never stored.
/// </summary>
/// <param name="Unspent">Your net worth at the moment, read off the soul timeline between its samples, less the cost of
/// the items you held; null without a timeline. An estimate: the timeline is sampled every few minutes.</param>
/// <param name="GateGained">The rank's souls over the death's duration; null without a rank gate.</param>
public sealed record DeathResult(TimeSpan At, TimeSpan Duration, int? Unspent, double? GateGained);

/// <summary>Deaths over many games, pooled for the page's deaths card.</summary>
public sealed record DeathTotal(int Games, int Deaths, TimeSpan Dead, int WithUnspent, double UnspentSum, int BigStash,
                                int WithGate, double GateGained)
{
    public double DeathsPerGame => Games > 0 ? (double)Deaths / Games : 0;
    public TimeSpan DeadPerGame => Games > 0 ? Dead / Games : TimeSpan.Zero;
    /// <summary>Average unspent souls at a death; null when no death had a timeline to read.</summary>
    public double? AverageUnspent => WithUnspent > 0 ? UnspentSum / WithUnspent : null;
    /// <summary>The rank's souls a game while you were dead; null without a rank gate.</summary>
    public double? GateGainedPerGame => WithGate > 0 ? GateGained / WithGate : null;
    /// <summary>The share of deaths carrying <see cref="DeathReview.BigStash"/> or more unspent.</summary>
    public double BigStashShare => WithUnspent > 0 ? (double)BigStash / WithUnspent : 0;
}

/// <summary>
/// The death review: deaths from the match metadata's <c>death_details</c> set against your soul timeline and purchases
/// (souls you died with unspent) and the rank gate's curve (souls it gained while you were dead). Net worth in the
/// metadata is the value of held items plus unspent souls (checked on public matches: it is never below the held items'
/// cost and always below the total ever bought), so unspent = net worth − held items. Pure, so it is unit tested.
/// </summary>
public static class DeathReview
{
    /// <summary>Unspent souls this high at a death are about a big item you could have had in the fight.</summary>
    public const int BigStash = 3000;
    /// <summary>The share of deaths with a big stash that makes the card call it out.</summary>
    public const double BigStashCallout = 0.25;

    public static IReadOnlyList<DeathResult> Of(IReadOnlyList<DeathRecord>? deaths, IReadOnlyList<NetWorthPoint> souls,
                                                IReadOnlyList<MatchItem> purchases, Func<long, int> cost, PaceCurve? gate)
    {
        if (deaths is null || deaths.Count == 0) return Array.Empty<DeathResult>();
        return deaths.Select(d =>
        {
            int? unspent = souls.Count >= 2 && HistoryMath.SoulsAt(souls, d.At) is { } worth
                ? Math.Max(0, worth - Held(purchases, cost, d.At))
                : null;
            double? gained = gate is { Points.Count: > 1 } curve
                ? PaceMath.NetWorthAt(curve, d.At + d.Duration) - PaceMath.NetWorthAt(curve, d.At)
                : null;
            return new DeathResult(d.At, d.Duration, unspent, gained);
        }).ToList();
    }

    /// <summary>The cost of the items held at <paramref name="at"/>: bought by then and not sold before it.</summary>
    public static int Held(IReadOnlyList<MatchItem> purchases, Func<long, int> cost, TimeSpan at)
        => purchases.Where(p => p.BoughtAt <= at && (p.SoldAt is not { } sold || sold > at)).Sum(p => cost(p.ItemId));

    /// <summary>Every game's deaths pooled; <paramref name="games"/> counts games with a death record, deaths or not.</summary>
    public static DeathTotal Total(IReadOnlyList<IReadOnlyList<DeathResult>> games)
    {
        var all = games.SelectMany(g => g).ToList();
        var unspent = all.Where(d => d.Unspent is not null).Select(d => d.Unspent!.Value).ToList();
        // A game without a death lost nothing while dead; it counts once any game has a gate to measure against.
        bool anyGate = all.Any(d => d.GateGained is not null);
        var withGate = anyGate ? games.Where(g => g.All(d => d.GateGained is not null)).ToList() : new List<IReadOnlyList<DeathResult>>();
        return new DeathTotal(games.Count, all.Count, TimeSpan.FromTicks(all.Sum(d => d.Duration.Ticks)),
            unspent.Count, unspent.Sum(), unspent.Count(u => u >= BigStash),
            withGate.Count, withGate.Sum(g => g.Sum(d => d.GateGained!.Value)));
    }
}
