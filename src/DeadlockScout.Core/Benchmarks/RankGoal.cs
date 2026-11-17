using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// The rank you are pushing towards: a whole rank tier (Oracle, not Oracle 3), whose average souls on your hero is the
/// gate the soul timeline and the overlay show. By default the tier above yours, never below Acolyte: Initiate and
/// Seeker only join the lists when <see cref="GoalSettings.IncludeLowRanks"/> is on.
/// </summary>
public static class RankGoal
{
    public const int Initiate = 1, Acolyte = 3, Eternus = 11;

    // The ladder since the ranked update of July 30, 2026: Acolyte, Sentinel and Mystic took the third to fifth tiers, and
    // Ritualist and Emissary moved up one; Alchemist, Arcanist and Archon are gone. Badges still count tiers 1 to 11.
    private static readonly string[] TierNames =
        { "", "Initiate", "Seeker", "Acolyte", "Sentinel", "Mystic", "Ritualist", "Emissary", "Oracle", "Phantom", "Ascendant", "Eternus" };

    // The retired names, at the tier they named, so a goal saved in settings.json before the rename still reads.
    private static readonly (string Name, int Tier)[] FormerNames = { ("Alchemist", 3), ("Arcanist", 4), ("Archon", 7) };

    /// <summary>"Oracle" for 8; "" outside 1..11.</summary>
    public static string Name(int tier) => tier is >= Initiate and <= Eternus ? TierNames[tier] : "";

    /// <summary>The tier a name stands for, any case; null for "next", blanks and unknown names.</summary>
    public static int? TierByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int i = Array.FindIndex(TierNames, n => n.Length > 0 && n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i > 0) return i;
        foreach (var (former, tier) in FormerNames)
            if (former.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return tier;
        return null;
    }

    public static int Lowest(bool includeLowRanks) => includeLowRanks ? Initiate : Acolyte;

    /// <summary>The tiers to choose from, highest first: Eternus down to Acolyte (or Initiate).</summary>
    public static IReadOnlyList<int> Choices(bool includeLowRanks)
        => Enumerable.Range(Lowest(includeLowRanks), Eternus - Lowest(includeLowRanks) + 1).Reverse().ToList();

    /// <summary>The tier above <paramref name="badge"/>'s (Eternus stays Eternus), held at the lowest choice; that lowest when the badge is unknown.</summary>
    public static int Next(int? badge, bool includeLowRanks)
    {
        int tier = badge is > 0 ? badge.Value / 10 : 0;
        return Math.Clamp(tier + 1, Lowest(includeLowRanks), Eternus);
    }

    /// <summary><see cref="GoalSettings.Rank"/> when it names a tier on the list, else the next one up from <paramref name="badge"/>.</summary>
    public static int Resolve(GoalSettings settings, int? badge)
        => TierByName(settings.Rank) is { } t && t >= Lowest(settings.IncludeLowRanks) ? t : Next(badge, settings.IncludeLowRanks);

    /// <summary>Every badge of the tier: Oracle is 80–89 (Oracle 1 = 81 … Oracle 6 = 86).</summary>
    public static RankBracket Bracket(int tier) => new(tier * 10, tier * 10 + 9);

    /// <summary>The curve's average souls once a minute through <paramref name="duration"/>, for drawing beside a match's own souls.</summary>
    public static IReadOnlyList<NetWorthPoint> Line(PaceCurve curve, TimeSpan duration)
    {
        int minutes = (int)Math.Ceiling(Math.Max(1, duration.TotalMinutes));
        var points = new List<NetWorthPoint>(minutes + 1);
        for (int m = 0; m <= minutes; m++)
        {
            var t = m * 60 > duration.TotalSeconds ? duration : TimeSpan.FromMinutes(m);
            points.Add(new NetWorthPoint(t, (int)Math.Round(PaceMath.NetWorthAt(curve, t))));
        }
        return points;
    }
}
