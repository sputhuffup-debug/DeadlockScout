using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>Pure helpers that turn raw API numbers into monotone <see cref="PaceCurve"/>s.</summary>
public static class CurveBuilder
{
    /// <summary>Duration buckets the analytics benchmark queries, in minutes: 10–15, 15–20 … 45–50.</summary>
    public static readonly IReadOnlyList<(int FromMin, int ToMin)> DurationBuckets =
        Enumerable.Range(0, 8).Select(i => (10 + 5 * i, 15 + 5 * i)).ToList();

    /// <summary>
    /// Builds a mean-only curve from (bucket midpoint minute, mean final net worth, matches) points: sorted, monotone
    /// non-decreasing, anchored at 0 souls at minute 0. Buckets with no matches are skipped.
    /// </summary>
    public static PaceCurve FromBucketMeans(int heroId, RankBracket bracket, string patchKey, string provider,
                                            IEnumerable<(int Minute, double MeanNetWorth, int Matches)> buckets)
    {
        var pts = new List<PacePoint> { new(0, null, null, null, 0) };
        int sample = 0;
        double last = 0;
        foreach (var b in buckets.Where(b => b.Matches > 0).OrderBy(b => b.Minute))
        {
            double v = Math.Max(last, b.MeanNetWorth); // never let the curve dip
            pts.Add(new PacePoint(b.Minute, null, null, null, (int)Math.Round(v)));
            last = v;
            sample += b.Matches;
        }
        return new PaceCurve(heroId, bracket, patchKey, pts, sample, provider);
    }

    /// <summary>
    /// Builds a percentile curve from per-match net-worth series: for each whole minute with at least
    /// <paramref name="minMatches"/> matches still running, P25/P50/P75/mean across those matches.
    /// </summary>
    public static PaceCurve FromSeries(int heroId, RankBracket bracket, string patchKey, string provider,
                                       IReadOnlyList<IReadOnlyList<NetWorthPoint>> series, int minMatches = 5)
    {
        var pts = new List<PacePoint> { new(0, 0, 0, 0, 0) };
        if (series.Count == 0) return new PaceCurve(heroId, bracket, patchKey, pts, 0, provider);

        int maxMinute = (int)Math.Floor(series.Max(s => s.Count == 0 ? 0 : s[^1].GameTime.TotalMinutes));
        var values = new List<double>();
        for (int m = 1; m <= maxMinute; m++)
        {
            values.Clear();
            var t = TimeSpan.FromMinutes(m);
            foreach (var s in series)
            {
                if (s.Count == 0 || s[^1].GameTime < t) continue; // match already over
                values.Add(InterpolateSeries(s, t));
            }
            if (values.Count < minMatches) break;
            values.Sort();
            pts.Add(new PacePoint(m,
                (int)Math.Round(Quantile(values, 0.25)),
                (int)Math.Round(Quantile(values, 0.50)),
                (int)Math.Round(Quantile(values, 0.75)),
                (int)Math.Round(values.Average())));
        }
        return new PaceCurve(heroId, bracket, patchKey, pts, series.Count, provider);
    }

    public static double InterpolateSeries(IReadOnlyList<NetWorthPoint> s, TimeSpan t)
    {
        if (s.Count == 0) return 0;
        if (t <= s[0].GameTime) return s[0].GameTime <= TimeSpan.Zero ? s[0].NetWorth : s[0].NetWorth * (t.Ticks / (double)s[0].GameTime.Ticks);
        if (t >= s[^1].GameTime) return s[^1].NetWorth;
        for (int i = 1; i < s.Count; i++)
        {
            if (s[i].GameTime >= t)
            {
                var a = s[i - 1]; var b = s[i];
                if (b.GameTime == a.GameTime) return b.NetWorth;
                double f = (t - a.GameTime).Ticks / (double)(b.GameTime - a.GameTime).Ticks;
                return a.NetWorth + (b.NetWorth - a.NetWorth) * f;
            }
        }
        return s[^1].NetWorth;
    }

    /// <summary>Linear-interpolated quantile of a sorted list.</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
