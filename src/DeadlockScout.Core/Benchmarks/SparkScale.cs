namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// How the pace sparkline measures and draws at a given point in the match. Laning income is steady, so a short window
/// and a ±25% band read well; later a kill, an objective or a death moves a two-minute window by far more than that,
/// and the line would sit on the edge. So the window the line is measured over and the band it is drawn in both widen
/// with the phase: 2 min ±25% in laning, 4 min ±40% in mid game, 6 min ±60% in late game. The headline souls a minute
/// keeps the meter's own window; only the line changes.
/// </summary>
public sealed record SparkScale(TimeSpan Window, double Range)
{
    /// <summary>Share of the half-height the band fills; the rest takes everything past the band, compressed.</summary>
    public const double Band = 0.8;

    public static SparkScale At(TimeSpan gameTime, TimeSpan meterWindow)
    {
        var (minutes, range) = gameTime < Phases.LaningEnd ? (2, 0.25) : gameTime < Phases.MidEnd ? (4, 0.40) : (6, 0.60);
        var window = TimeSpan.FromMinutes(minutes);
        return new SparkScale(window < meterWindow ? meterWindow : window, range);
    }

    /// <summary>
    /// A delta's height in the box, -1 (bottom) to 1 (top). Inside the band it is linear and fills <see cref="Band"/> of
    /// the half-height; past the band it keeps rising towards the edge without reaching it, so a spike still shows its
    /// peak and how fast it fades instead of a flat line on the edge.
    /// </summary>
    public double Position(double delta)
    {
        double x = delta / Range;
        double a = Math.Abs(x);
        // Past the band, e/(1+e): twice the band sits at 90% of the height, three times at 93%, ten times at 98%.
        double y = a <= 1 ? a * Band : Band + (1 - Band) * ((a - 1) / a);
        return Math.Sign(x) * y;
    }

    /// <summary>"±40% · 4m": the band's edge and the window, shown beside the line.</summary>
    public string Label => $"±{Range * 100:0}% · {Window.TotalMinutes:0}m";
}
