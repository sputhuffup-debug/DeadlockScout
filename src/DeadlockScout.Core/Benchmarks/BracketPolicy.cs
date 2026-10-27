using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// Which badge range benchmarks and matchups are read for. One rule, used by the match session and by the
/// between-match prefetch, so both land on the same cache keys.
/// </summary>
public static class BracketPolicy
{
    /// <summary>
    /// <see cref="MeterSettings.BenchmarkBracket"/>: "all"; "min-max" badge numbers; or "auto", your badge ± the bracket
    /// width (all badges when your badge is unknown).
    /// </summary>
    public static RankBracket Resolve(MeterSettings cfg, int? myBadge)
    {
        if (cfg.BenchmarkBracket.Equals("all", StringComparison.OrdinalIgnoreCase)) return RankBracket.All;
        if (!cfg.BenchmarkBracket.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cfg.BenchmarkBracket.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && lo <= hi)
                return new RankBracket(Math.Max(0, lo), Math.Min(116, hi));
        }
        return myBadge is { } b and > 0 ? RankBracket.Around(b, cfg.BracketWidth) : RankBracket.All;
    }
}
