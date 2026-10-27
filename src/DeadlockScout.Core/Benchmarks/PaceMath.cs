using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>One line of the pace strip: your window rate against the curve's window rate at the same game time.</summary>
public sealed record PaceEvaluation(
    TimeSpan GameTime,
    double ActualSpm,
    double ExpectedSpm,
    /// <summary>(actual / expected) - 1, so +0.05 means 5% ahead. Null when expected is 0.</summary>
    double? Delta,
    int ActualNetWorth,
    double ExpectedNetWorth,
    /// <summary>0..100 when the curve has percentiles, else null.</summary>
    double? Percentile,
    string Provider,
    int SampleSize);

/// <summary>Reads a <see cref="PaceCurve"/> at arbitrary game times. Pure functions.</summary>
public static class PaceMath
{
    /// <summary>Mean net worth at <paramref name="t"/>, linearly interpolated between whole minutes; flat beyond the last point.</summary>
    public static double NetWorthAt(PaceCurve curve, TimeSpan t) => Interpolate(curve, t, p => p.Mean);

    /// <summary>Expected souls per minute over the same window the meter uses, from the curve itself (like for like).</summary>
    public static double ExpectedSpm(PaceCurve curve, TimeSpan t, TimeSpan window)
    {
        if (t <= TimeSpan.Zero) return 0;
        var from = t - window;
        if (from < TimeSpan.Zero) from = TimeSpan.Zero;
        var used = t - from;
        if (used <= TimeSpan.Zero) return 0;
        return (NetWorthAt(curve, t) - NetWorthAt(curve, from)) / used.TotalMinutes;
    }

    /// <summary>
    /// Where <paramref name="netWorth"/> sits in the curve's distribution at <paramref name="t"/>, 0..100.
    /// Piecewise linear through P25/P50/P75; the tails are stretched by the inter-quartile distance so the value
    /// keeps moving but never leaves [1, 99]. Null when the curve has no percentiles.
    /// </summary>
    public static double? Percentile(PaceCurve curve, TimeSpan t, double netWorth)
    {
        if (!curve.HasPercentiles || curve.Points.Count == 0) return null;
        double p25 = Interpolate(curve, t, p => p.P25!.Value);
        double p50 = Interpolate(curve, t, p => p.P50 ?? p.Mean);
        double p75 = Interpolate(curve, t, p => p.P75!.Value);
        if (p75 <= p25) return netWorth >= p50 ? 50 : 50; // degenerate curve: no spread yet

        double iqr = p75 - p25;
        if (netWorth <= p25)
        {
            // one IQR below P25 reads as ~1st percentile
            double d = (p25 - netWorth) / iqr;
            return Math.Max(1, 25 - 24 * Math.Min(1, d));
        }
        if (netWorth <= p50) return 25 + 25 * (netWorth - p25) / Math.Max(1e-9, p50 - p25);
        if (netWorth <= p75) return 50 + 25 * (netWorth - p50) / Math.Max(1e-9, p75 - p50);
        double up = (netWorth - p75) / iqr;
        return Math.Min(99, 75 + 24 * Math.Min(1, up));
    }

    /// <summary>Builds the pace-strip evaluation for one reading.</summary>
    public static PaceEvaluation Evaluate(PaceCurve curve, MeterReading reading)
    {
        double expectedSpm = ExpectedSpm(curve, reading.GameTime, reading.WindowUsed);
        double expectedNw = NetWorthAt(curve, reading.GameTime);
        double? delta = expectedSpm > 0 ? reading.WindowSpm / expectedSpm - 1 : null;
        return new PaceEvaluation(
            reading.GameTime,
            reading.WindowSpm,
            expectedSpm,
            delta,
            reading.Souls,
            expectedNw,
            Percentile(curve, reading.GameTime, reading.Souls),
            curve.Provider,
            curve.SampleSize);
    }

    /// <summary>Formats the overlay line: <c>Haze · 14:20 · 612 spm (2m) · pace 580 · +5% · 58th pct</c>.</summary>
    public static string FormatLine(string heroName, PaceEvaluation e, TimeSpan window)
    {
        string w = window.TotalMinutes >= 1 ? $"{window.TotalMinutes:0}m" : $"{window.TotalSeconds:0}s";
        var sb = new System.Text.StringBuilder();
        sb.Append(heroName).Append(" · ").Append(FormatClock(e.GameTime));
        sb.Append(" · ").Append(e.ActualSpm.ToString("0")).Append(" spm (").Append(w).Append(')');
        sb.Append(" · pace ").Append(e.ExpectedSpm.ToString("0"));
        if (e.Delta is { } d) sb.Append(" · ").Append(d >= 0 ? "+" : "").Append((d * 100).ToString("0")).Append('%');
        if (e.Percentile is { } p) sb.Append(" · ").Append(Ordinal((int)Math.Round(p))).Append(" pct");
        return sb.ToString();
    }

    public static string FormatClock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static string Ordinal(int n)
    {
        int r100 = n % 100, r10 = n % 10;
        string suffix = r100 is >= 11 and <= 13 ? "th" : r10 switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n + suffix;
    }

    private static double Interpolate(PaceCurve curve, TimeSpan t, Func<PacePoint, double> value)
    {
        var pts = curve.Points;
        if (pts.Count == 0) return 0;
        double minute = t.TotalMinutes;
        if (minute <= 0) return 0;
        if (minute <= pts[0].Minute)
            return pts[0].Minute <= 0 ? value(pts[0]) : value(pts[0]) * (minute / pts[0].Minute);
        if (minute >= pts[^1].Minute) return value(pts[^1]);
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].Minute >= minute)
            {
                var a = pts[i - 1]; var b = pts[i];
                double f = (minute - a.Minute) / (b.Minute - a.Minute);
                return value(a) + (value(b) - value(a)) * f;
            }
        }
        return value(pts[^1]);
    }
}
