using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Objectives;

/// <summary>
/// One reading of the map objectives from the broadcast, at a game time. Null means the source does not say (the hosted
/// feeds carry no objectives), never "no".
/// </summary>
public sealed record ObjectiveSample(TimeSpan GameTime, bool? MidBossAlive, int AmberRejuvs, int SapphireRejuvs, bool? UrnOnMap);

/// <summary>The mid boss: up, or down since a game time (with the team that took its Rejuvenator, once known) and when it should be back.</summary>
public sealed record MidBossView(bool Alive, TimeSpan? DownSince, int? TakenBy, TimeSpan? BackAt);

/// <summary>The urn: on the map or not, and since when (null when it was already so when the feed started).</summary>
public sealed record UrnView(bool OnMap, TimeSpan? Since);

/// <summary>What the objectives panel shows. Times are game-clock times, so the broadcast's delay does not shift them.</summary>
public sealed record ObjectiveView(MidBossView? MidBoss, UrnView? Urn, int AmberRejuvs, int SapphireRejuvs);

/// <summary>
/// Turns objective samples into the panel's view. The broadcast has no respawn timers for the mid boss or the urn, only
/// whether each is there; the tracker records when that changed. The mid boss's return is estimated from its last
/// observed respawn delay in this match, or <see cref="DefaultMidBossRespawn"/> before one has been seen. The team that
/// killed it is the one whose Rejuvenator count goes up while it is down. Thread-safe: the feed writes, the overlay reads.
/// </summary>
public sealed class ObjectiveTracker
{
    private readonly object _gate = new();
    private readonly TimeSpan _defaultRespawn;
    private bool? _midBossAlive;
    private TimeSpan? _downSince;
    private int? _takenBy;
    private TimeSpan? _observedRespawn;
    private bool? _urnOnMap;
    private TimeSpan? _urnSince;
    private int _amber, _sapphire;
    private bool _seen;

    /// <summary>
    /// UNVERIFIED: seven minutes, from the game's patch notes as remembered, not from a captured match. The app logs each
    /// respawn delay it observes (see <see cref="Observe"/>'s return), so a real match confirms or corrects it.
    /// </summary>
    public static readonly TimeSpan DefaultMidBossRespawn = TimeSpan.FromMinutes(7);

    public ObjectiveTracker(TimeSpan? midBossRespawn = null) => _defaultRespawn = midBossRespawn ?? DefaultMidBossRespawn;

    /// <summary>Feeds one sample. Returns the respawn delay when this sample shows the mid boss back after a death this tracker saw, else null.</summary>
    public TimeSpan? Observe(ObjectiveSample s)
    {
        lock (_gate)
        {
            TimeSpan? respawned = null;
            if (s.MidBossAlive is { } alive)
            {
                if (_midBossAlive is { } was && was != alive)
                {
                    if (!alive) { _downSince = s.GameTime; _takenBy = null; }
                    else
                    {
                        if (_downSince is { } d && s.GameTime > d) respawned = _observedRespawn = s.GameTime - d;
                        _downSince = null; _takenBy = null;
                    }
                }
                _midBossAlive = alive;
            }
            // A Rejuvenator picked up while the boss is down names the team that took it. Counts seen at the first sample
            // are history, not a kill.
            if (_seen && _midBossAlive == false)
            {
                if (s.AmberRejuvs > _amber) _takenBy = Teams.Amber;
                else if (s.SapphireRejuvs > _sapphire) _takenBy = Teams.Sapphire;
            }
            _amber = s.AmberRejuvs; _sapphire = s.SapphireRejuvs;

            if (s.UrnOnMap is { } onMap)
            {
                if (_urnOnMap is { } wasOn && wasOn != onMap) _urnSince = s.GameTime;
                _urnOnMap = onMap;
            }
            _seen = true;
            return respawned;
        }
    }

    public ObjectiveView? Snapshot()
    {
        lock (_gate)
        {
            if (!_seen) return null;
            var boss = _midBossAlive is { } alive
                ? new MidBossView(alive, alive ? null : _downSince, alive ? null : _takenBy, !alive && _downSince is { } d ? d + (_observedRespawn ?? _defaultRespawn) : null)
                : null;
            var urn = _urnOnMap is { } on ? new UrnView(on, _urnSince) : null;
            return new ObjectiveView(boss, urn, _amber, _sapphire);
        }
    }
}

/// <summary>The objectives panel's lines. Teams read as "you"/"enemy" when you play, by colour when you watch. Pure.</summary>
public static class ObjectiveFormat
{
    public static (string MidBoss, string Rejuvs, string Urn) Lines(ObjectiveView v, int myTeam)
    {
        string Who(int team) => myTeam is Teams.Amber or Teams.Sapphire ? (team == myTeam ? "you" : "enemy") : Teams.Name(team);
        string Clock(TimeSpan t) => Benchmarks.PaceMath.FormatClock(t);

        var boss = v.MidBoss switch
        {
            null => "",
            { Alive: true } => "Mid boss up",
            { DownSince: { } since } b => $"Mid boss down {Clock(since)}" + (b.BackAt is { } back ? $" · back ~{Clock(back)}" : "")
                                          + (b.TakenBy is { } team ? $" · Rejuv to {Who(team)}" : ""),
            _ => "Mid boss down",
        };
        var rejuvs = v.AmberRejuvs + v.SapphireRejuvs == 0 ? ""
            : myTeam is Teams.Amber or Teams.Sapphire
                ? $"Rejuvenators: you {(myTeam == Teams.Amber ? v.AmberRejuvs : v.SapphireRejuvs)} · enemy {(myTeam == Teams.Amber ? v.SapphireRejuvs : v.AmberRejuvs)}"
                : $"Rejuvenators: Amber {v.AmberRejuvs} · Sapphire {v.SapphireRejuvs}";
        var urn = v.Urn switch
        {
            { OnMap: true, Since: { } t } => $"Urn on the map since {Clock(t)}",
            { OnMap: true } => "Urn on the map",
            { OnMap: false, Since: { } t } => $"Urn picked up {Clock(t)}",
            _ => "",
        };
        return (boss, rejuvs, urn);
    }
}

/// <summary>A countdown line for the objectives panel: its text, whether it is inside the lead (shown gold), and the game time it counts to.</summary>
public sealed record Countdown(string Text, bool Soon, TimeSpan At);

/// <summary>
/// The objectives as countdowns on the live game clock (see <c>Laning.LiveClock</c>): the mid boss's estimated return and
/// the next urn. The panel's other lines are game-clock times, which the broadcast's delay does not shift but which make
/// you do the subtraction; these do it for you, and turn gold <paramref name="lead"/> before.
/// </summary>
public static class ObjectiveCountdowns
{
    /// <summary>"Mid boss back in 1:40", "due now" once its estimated return has passed; null while it is up or with no estimate.</summary>
    public static Countdown? MidBoss(ObjectiveView? view, TimeSpan now, TimeSpan lead)
    {
        if (view?.MidBoss is not { Alive: false, BackAt: { } back }) return null;
        var left = back - now;
        if (left <= TimeSpan.Zero) return new Countdown("Mid boss due now (estimate)", true, back);
        return new Countdown($"Mid boss back in {Benchmarks.PaceMath.FormatClock(left)}", left <= lead, back);
    }

    /// <summary>
    /// The next urn by the schedule (<paramref name="first"/>, then every <paramref name="interval"/>), while no urn is on
    /// the map; null when one is, or the schedule is switched off (a zero first spawn).
    /// </summary>
    public static Countdown? NextUrn(ObjectiveView? view, TimeSpan now, TimeSpan first, TimeSpan interval, TimeSpan lead)
    {
        if (first <= TimeSpan.Zero || view?.Urn is { OnMap: true }) return null;
        var next = first;
        if (now > first && interval > TimeSpan.Zero)
            next = first + TimeSpan.FromTicks(interval.Ticks * (long)Math.Ceiling((now - first).Ticks / (double)interval.Ticks));
        else if (now > first) return null;
        var left = next - now;
        return new Countdown($"Next urn {Benchmarks.PaceMath.FormatClock(next)} · in {Benchmarks.PaceMath.FormatClock(left)}", left <= lead, next);
    }
}
