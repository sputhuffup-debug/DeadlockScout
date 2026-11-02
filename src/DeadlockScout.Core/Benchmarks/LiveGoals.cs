using System.Globalization;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// Your own goals from the match history (<see cref="TargetSettings"/>), checked during the match instead of only after
/// it: the laning pace target ("on track for 420 spm by 10:00", or the pace the rest of laning needs), the laning
/// deaths allowed ("one laning death left"), and while the phase you chose to fix is on, how it stands against the rank
/// gate. Each part goes once its phase is over. Null when there is nothing to say. Pure, so tested.
/// </summary>
public static class LiveGoals
{
    public static readonly TimeSpan LaningEnd = TimeSpan.FromMinutes(10);

    /// <param name="souls">Your souls now (net worth).</param>
    /// <param name="deaths">Your deaths so far; null when the feed has not said.</param>
    /// <param name="fixPhase">The live result of the phase you chose to fix, while it is the current phase.</param>
    public static string? Line(TargetSettings targets, TimeSpan now, int souls, int? deaths, PhaseResult? fixPhase)
    {
        var parts = new List<string>();
        if (now < LaningEnd)
        {
            if (targets.LaningSpm > 0 && now >= TimeSpan.FromMinutes(1))
            {
                int goal = targets.LaningSpm * 10;
                double left = (LaningEnd - now).TotalMinutes;
                double need = (goal - souls) / left;
                double pace = souls / now.TotalMinutes;
                parts.Add(souls >= goal ? $"laning goal of {targets.LaningSpm} spm met"
                    : need <= pace ? $"on track for {targets.LaningSpm} spm by 10:00"
                    : $"need {need.ToString("0", CultureInfo.InvariantCulture)} spm to 10:00 for {targets.LaningSpm}");
            }
            if (targets.MaxLaningDeaths is { } max && deaths is { } d)
            {
                int spare = max - d;
                parts.Add(spare switch
                {
                    > 1 => $"{spare} laning deaths left",
                    1 => "one laning death left",
                    0 => "no laning deaths left",
                    _ => $"{d} laning deaths, {max} was the goal",
                });
            }
        }
        if (targets.FixPhase is { } phase && fixPhase is { Phase: var p, Delta: { } delta } && p == phase)
        {
            string signed = (delta >= 0 ? "+" : "−") + Math.Abs(delta).ToString("0%", CultureInfo.InvariantCulture);
            parts.Add($"{Phases.Name(phase).ToLowerInvariant()} to fix: {signed} on the gate");
        }
        return parts.Count == 0 ? null : "Goals: " + string.Join(" · ", parts);
    }
}
