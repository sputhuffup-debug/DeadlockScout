using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Cues;

/// <summary>The app's own sounds (never the game's): each is its own short tone pattern, switchable in settings.</summary>
public enum Cue
{
    /// <summary>A laning timer reached "push now": shove your wave.</summary>
    PushNow,
    /// <summary>The mid boss's return or the next urn is inside the lead.</summary>
    Objective,
    /// <summary>Your pace has been under the rank gate for a while.</summary>
    PaceBehind,
}

/// <summary>
/// One second of the overlay, as the cues see it: the game time, the spawns inside their "push now" lead, the objectives
/// inside theirs (one key per event, such as "boss@18:20"), and your pace against the rank gate.
/// </summary>
public sealed record CueInput(TimeSpan GameTime, IReadOnlyList<TimeSpan> PushNowSpawns, IReadOnlyList<string> SoonObjectives, double? GoalDelta);

/// <summary>
/// Decides which cues sound, once a second. Every timer and objective cues once, when it first enters its lead; pace
/// cues after <see cref="AudioSettings.PaceBehindSeconds"/> of game time under the gate by more than
/// <see cref="AudioSettings.PaceBehindPercent"/>, then not again for <see cref="AudioSettings.PaceCooldownSeconds"/>.
/// A cue switched off is still marked as heard, so switching it on mid-match never plays a stale one. New per match.
/// </summary>
public sealed class CueTracker
{
    private readonly HashSet<string> _heard = new();
    private TimeSpan? _behindSince, _lastPace;

    public IReadOnlyList<Cue> Update(CueInput input, AudioSettings settings)
    {
        var cues = new List<Cue>();
        bool on = settings.Enabled;

        bool newSpawn = false;
        foreach (var at in input.PushNowSpawns) newSpawn |= _heard.Add($"spawn@{at}");
        if (newSpawn && on && settings.PushNow) cues.Add(Cue.PushNow);

        bool newObjective = false;
        foreach (var key in input.SoonObjectives) newObjective |= _heard.Add($"objective@{key}");
        if (newObjective && on && settings.Objectives) cues.Add(Cue.Objective);

        if (input.GoalDelta is { } d && d <= -settings.PaceBehindPercent / 100.0)
        {
            _behindSince ??= input.GameTime;
            bool longEnough = input.GameTime - _behindSince.Value >= TimeSpan.FromSeconds(settings.PaceBehindSeconds);
            bool cooled = _lastPace is not { } last || input.GameTime - last >= TimeSpan.FromSeconds(settings.PaceCooldownSeconds);
            if (longEnough && cooled)
            {
                _lastPace = input.GameTime;
                if (on && settings.PaceBehind) cues.Add(Cue.PaceBehind);
            }
        }
        else _behindSince = null;

        return cues;
    }
}
