using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Laning;

/// <summary>Where a timer row stands: still ahead, inside the lead (push your wave now), or just spawned.</summary>
public enum TimerState { Later, PushNow, Up }

/// <summary>One row of the timers panel: everything that spawns at <see cref="At"/>, and how far away it is.</summary>
public sealed record TimerRow(TimeSpan At, string Names, string Note, TimeSpan In, TimerState State);

/// <summary>
/// The jungle's first spawns by the game clock (small camps, crates, medium camps, large camps, Sinner's Sacrifice), as
/// rows for the laning timers: the next few, each turning to "push now" <see cref="TimerSettings.LeadSeconds"/> before it
/// spawns, so you shove your wave and are free when the camp or crate appears. Spawns at the same time share a row.
/// Camp respawns come from <see cref="CampTimers"/> (the broadcast shows each clear) and share the panel through
/// <see cref="Merge"/>; crates are not tracked, so their notes still say how soon they come back.
/// </summary>
public static class SpawnTimers
{
    /// <summary>The next rows at <paramref name="now"/>: up to <see cref="TimerSettings.MaxRows"/>, a spawn kept for <see cref="TimerSettings.ShowUpSeconds"/> after it happens.</summary>
    public static IReadOnlyList<TimerRow> Rows(TimeSpan now, TimerSettings settings)
    {
        var lead = TimeSpan.FromSeconds(Math.Max(0, settings.LeadSeconds));
        var showUp = TimeSpan.FromSeconds(Math.Max(0, settings.ShowUpSeconds));
        return settings.Spawns
            .Where(s => s.FirstSpawnSeconds > 0 && !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.FirstSpawnSeconds)
            .Select(g => (At: TimeSpan.FromSeconds(g.Key), Rules: g.ToList()))
            .Where(g => now < g.At + showUp)
            .OrderBy(g => g.At)
            .Take(Math.Max(1, settings.MaxRows))
            .Select(g =>
            {
                var left = g.At - now;
                var state = left <= TimeSpan.Zero ? TimerState.Up : left <= lead ? TimerState.PushNow : TimerState.Later;
                return new TimerRow(g.At, string.Join(" · ", g.Rules.Select(r => r.Name)),
                    string.Join(" · ", g.Rules.Select(r => r.Note).Where(n => !string.IsNullOrWhiteSpace(n))), left, state);
            })
            .ToList();
    }

    /// <summary>The first spawns and the camp respawns in one panel: soonest first, at most <paramref name="maxRows"/>.</summary>
    public static IReadOnlyList<TimerRow> Merge(IReadOnlyList<TimerRow> firstSpawns, IReadOnlyList<TimerRow> campRows, int maxRows)
        => firstSpawns.Concat(campRows).OrderBy(r => r.At).Take(Math.Max(1, maxRows)).ToList();
}
