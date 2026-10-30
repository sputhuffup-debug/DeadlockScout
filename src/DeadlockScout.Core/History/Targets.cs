using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.History;

public enum TargetKind { LaningSpm, LaningDeaths, Phase }

/// <summary>One target in one game: hit, missed, or null when the game lacks what it needs; <see cref="Value"/> is what was measured.</summary>
public sealed record TargetCheck(TargetKind Kind, bool? Hit, string Value);

/// <summary>A target over many games: how often it was hit, and the results newest first (null for unknown).</summary>
public sealed record TargetRate(TargetKind Kind, int Hits, int Misses, IReadOnlyList<bool?> Results)
{
    public int Games => Hits + Misses;
    public double? Rate => Games > 0 ? (double)Hits / Games : null;
}

/// <summary>
/// Checks a finished game against your <see cref="TargetSettings"/>: souls a minute by 10:00 from the soul timeline,
/// deaths before 10:00 from the death times, and the phase to fix against the rank gate's phases. Derived whenever it is
/// shown, never stored. Pure, so it is unit tested.
/// </summary>
public static class Targets
{
    public static readonly TimeSpan LaningEnd = Phases.LaningEnd;

    /// <summary>The set targets in a fixed order, each checked against the game.</summary>
    public static IReadOnlyList<TargetCheck> Check(TargetSettings t, IReadOnlyList<NetWorthPoint> souls, TimeSpan duration,
                                                   IReadOnlyList<DeathRecord>? deaths, IReadOnlyList<PhaseResult> phases)
    {
        var checks = new List<TargetCheck>();
        bool reachedTen = duration >= LaningEnd;
        if (t.LaningSpm > 0)
        {
            // The timeline has to reach 10:00; before the first sample there is nothing to read.
            if (reachedTen && souls.Count >= 2 && souls[^1].GameTime >= LaningEnd && HistoryMath.SoulsAt(souls, LaningEnd) is { } at10)
            {
                double spm = at10 / LaningEnd.TotalMinutes;
                checks.Add(new TargetCheck(TargetKind.LaningSpm, spm >= t.LaningSpm, $"{spm:0}/min"));
            }
            else checks.Add(new TargetCheck(TargetKind.LaningSpm, null, "–"));
        }
        if (t.MaxLaningDeaths is { } max)
        {
            if (reachedTen && deaths is not null)
            {
                int n = deaths.Count(d => d.At < LaningEnd);
                checks.Add(new TargetCheck(TargetKind.LaningDeaths, n <= max, $"{n} {(n == 1 ? "death" : "deaths")}"));
            }
            else checks.Add(new TargetCheck(TargetKind.LaningDeaths, null, "–"));
        }
        if (t.FixPhase is { } phase)
        {
            var p = phases.FirstOrDefault(r => r.Phase == phase);
            checks.Add(p?.Delta is { } d
                ? new TargetCheck(TargetKind.Phase, d >= 0, $"{(d >= 0 ? "+" : "−")}{Math.Abs(d) * 100:0}%")
                : new TargetCheck(TargetKind.Phase, null, "–"));
        }
        return checks;
    }

    /// <summary>Each target's hit rate over <paramref name="games"/> (newest first), in the order <see cref="Check"/> gives them.</summary>
    public static IReadOnlyList<TargetRate> Rates(IReadOnlyList<IReadOnlyList<TargetCheck>> games)
        => games.SelectMany(g => g.Select(c => c.Kind)).Distinct().OrderBy(k => k)
            .Select(kind =>
            {
                var results = games.Select(g => g.FirstOrDefault(c => c.Kind == kind)?.Hit).ToList();
                return new TargetRate(kind, results.Count(r => r == true), results.Count(r => r == false), results);
            }).ToList();

    /// <summary>"Laning pace 500/min", "At most 1 laning death", "Mid game at the rank gate".</summary>
    public static string Describe(TargetKind kind, TargetSettings t) => kind switch
    {
        TargetKind.LaningSpm => $"Laning pace {t.LaningSpm}/min",
        TargetKind.LaningDeaths => t.MaxLaningDeaths == 0 ? "No deaths in laning" : $"At most {t.MaxLaningDeaths} laning {(t.MaxLaningDeaths == 1 ? "death" : "deaths")}",
        _ => $"{(t.FixPhase is { } p ? Phases.Name(p) : "A phase")} at the rank gate",
    };

    /// <summary>The chip label for a target: "10:00 PACE", "LANING DEATHS", "MID GAME".</summary>
    public static string Short(TargetKind kind, TargetSettings t) => kind switch
    {
        TargetKind.LaningSpm => "10:00 PACE",
        TargetKind.LaningDeaths => "LANING DEATHS",
        _ => (t.FixPhase is { } p ? Phases.Name(p) : "PHASE").ToUpperInvariant(),
    };
}
