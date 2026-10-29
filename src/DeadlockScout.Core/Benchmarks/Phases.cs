using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Benchmarks;

public enum GamePhase { Laning, Mid, Late }

/// <summary>
/// One phase of one game against a rank's curve: the souls you gained in it and the souls the curve gained over the same
/// minutes. Derived whenever it is shown, never stored.
/// </summary>
public sealed record PhaseResult(GamePhase Phase, TimeSpan From, TimeSpan To, int Gained, double Expected)
{
    public double Minutes => (To - From).TotalMinutes;
    /// <summary>(gained / expected) - 1: +0.08 is 8% more souls than the rank in this phase. Null when the curve gained nothing.</summary>
    public double? Delta => Expected > 0 ? Gained / Expected - 1 : null;
    public double Spm => Minutes > 0 ? Gained / Minutes : 0;
    public double ExpectedSpm => Minutes > 0 ? Expected / Minutes : 0;
}

/// <summary>A phase over many games, pooled: total souls against total expected, so long games weigh by their minutes.</summary>
public sealed record PhaseTotal(GamePhase Phase, int Games, double Gained, double Expected, double Minutes)
{
    public double? Delta => Expected > 0 ? Gained / Expected - 1 : null;
    public double Spm => Minutes > 0 ? Gained / Minutes : 0;
    public double ExpectedSpm => Minutes > 0 ? Expected / Minutes : 0;
    /// <summary>Souls a game you are short of the rank in this phase (negative: ahead of it).</summary>
    public double ShortfallPerGame => Games > 0 ? (Expected - Gained) / Games : 0;
}

/// <summary>
/// Splits a game into laning (0–10 min), mid game (10–20) and late game (20 to the end) and sets your souls in each against
/// a rank's curve, so a run of games shows which phase costs you the most.
/// </summary>
public static class Phases
{
    public static readonly TimeSpan LaningEnd = TimeSpan.FromMinutes(10), MidEnd = TimeSpan.FromMinutes(20);
    /// <summary>A phase shorter than this (a game that ended at 21:00) says too little to count.</summary>
    public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(2);
    /// <summary>Games a phase needs before the summary names it the weakest.</summary>
    public const int MinGamesForWeakest = 3;

    public static string Name(GamePhase phase) => phase switch
    {
        GamePhase.Laning => "Laning",
        GamePhase.Mid => "Mid game",
        _ => "Late game",
    };

    /// <summary>"0–10", "10–20", "20+".</summary>
    public static string Span(GamePhase phase) => phase switch
    {
        GamePhase.Laning => "0–10 min",
        GamePhase.Mid => "10–20 min",
        _ => "20+ min",
    };

    /// <summary>
    /// The phases <paramref name="souls"/> covers, each against <paramref name="curve"/>. A phase ends at the game's end or the
    /// last sample, and never after the curve's last point: past it the curve is flat and would flatter you.
    /// </summary>
    public static IReadOnlyList<PhaseResult> Of(IReadOnlyList<NetWorthPoint> souls, TimeSpan duration, PaceCurve curve)
    {
        var results = new List<PhaseResult>();
        if (souls.Count < 2 || curve.Points.Count < 2) return results;
        var end = Min(Min(duration, souls[^1].GameTime), TimeSpan.FromMinutes(curve.Points[^1].Minute));
        foreach (var (phase, from, to) in new[] { (GamePhase.Laning, TimeSpan.Zero, LaningEnd), (GamePhase.Mid, LaningEnd, MidEnd), (GamePhase.Late, MidEnd, TimeSpan.MaxValue) })
        {
            var until = Min(to, end);
            if (until - from < MinLength) continue;
            int gained = (int)Math.Round(CurveBuilder.InterpolateSeries(souls, until) - CurveBuilder.InterpolateSeries(souls, from));
            double expected = PaceMath.NetWorthAt(curve, until) - PaceMath.NetWorthAt(curve, from);
            results.Add(new PhaseResult(phase, from, until, gained, expected));
        }
        return results;
    }

    /// <summary>Each phase pooled over <paramref name="games"/>, in phase order; phases no game reached are left out.</summary>
    public static IReadOnlyList<PhaseTotal> Total(IEnumerable<IReadOnlyList<PhaseResult>> games)
        => games.SelectMany(g => g)
            .GroupBy(r => r.Phase)
            .OrderBy(g => g.Key)
            .Select(g => new PhaseTotal(g.Key, g.Count(), g.Sum(r => (double)r.Gained), g.Sum(r => r.Expected), g.Sum(r => r.Minutes)))
            .ToList();

    /// <summary>The phase furthest behind the rank (lowest delta) among those with enough games; null when none is behind.</summary>
    public static PhaseTotal? Weakest(IReadOnlyList<PhaseTotal> totals)
        => totals.Where(t => t.Games >= MinGamesForWeakest && t.Delta is < 0).OrderBy(t => t.Delta).FirstOrDefault();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
