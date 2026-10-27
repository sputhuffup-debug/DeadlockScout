namespace DeadlockScout.Core.Models;

/// <summary>Inclusive badge range. Badge = rank * 10 + tier (Initiate 1 = 11 ... Eternus 6 = 116).</summary>
public sealed record RankBracket(int MinBadge, int MaxBadge)
{
    public static readonly RankBracket All = new(0, 116);

    public static RankBracket Around(int badge, int width)
        => new(Math.Max(0, badge - width), Math.Min(116, badge + width));

    public bool Contains(int badge) => badge >= MinBadge && badge <= MaxBadge;

    public override string ToString() => $"{MinBadge}-{MaxBadge}";
}

/// <summary>Net worth at a whole game minute. Percentiles are null when the provider only has a mean.</summary>
public sealed record PacePoint(int Minute, int? P25, int? P50, int? P75, int Mean);

/// <summary>A per-hero net-worth-over-time curve for a rank bracket, as produced by one benchmark provider.</summary>
public sealed record PaceCurve(
    int HeroId,
    RankBracket Bracket,
    string PatchKey,
    IReadOnlyList<PacePoint> Points,
    int SampleSize,
    string Provider)
{
    public bool HasPercentiles => Points.Count > 0 && Points.All(p => p.P25.HasValue && p.P75.HasValue);
}
