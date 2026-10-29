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
/// Respawns start when a camp is cleared or a crate broken, which the app cannot see yet, so only first spawns are timed;
/// each row's note says how soon it comes back.
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
}
