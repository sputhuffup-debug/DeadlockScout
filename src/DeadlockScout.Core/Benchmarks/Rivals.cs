using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// The enemies drawn beside you on the pace sparkline: your lane opponents first, then the richest other enemies, up to
/// <c>count</c>. Pure, so the choice is unit tested.
/// </summary>
public static class Rivals
{
    public static IReadOnlyList<uint> Pick(Roster roster, uint me, IReadOnlyDictionary<uint, int> latestNetWorth, int count = 2)
    {
        if (roster.Find(me) is not { } self || count <= 0) return Array.Empty<uint>();
        var enemies = roster.Players.Where(p => p.Team != self.Team && latestNetWorth.ContainsKey(p.AccountId)).ToList();
        var lane = self.Lane is { } l ? enemies.Where(p => p.Lane == l) : Enumerable.Empty<RosterPlayer>();
        var rest = enemies.Except(lane).OrderByDescending(p => latestNetWorth[p.AccountId]).ThenBy(p => p.AccountId);
        return lane.OrderByDescending(p => latestNetWorth[p.AccountId]).ThenBy(p => p.AccountId)
            .Concat(rest).Select(p => p.AccountId).Take(count).ToList();
    }

    /// <summary>"+1.2k", "−300", "±0": an enemy's souls against yours at the same game time.</summary>
    public static string Gap(int theirs, double mine)
    {
        int d = (int)Math.Round(theirs - mine);
        if (d == 0) return "±0";
        string sign = d > 0 ? "+" : "−";
        int a = Math.Abs(d);
        return sign + (a >= 1000 ? (a / 1000.0).ToString(a >= 10_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "k" : a.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
