using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Laning;

public enum CampKind { Unknown, Small, Medium, Large, Sinners, Crates, MidtownCrates }

/// <summary>
/// One alive neutral in a broadcast snapshot: a stable id (the entity handle), where it stands, and whether it is a
/// Sinner's Sacrifice machine or a breakable crate rather than a jungle creature.
/// </summary>
public readonly record struct NeutralSighting(long Id, float X, float Y, bool Sinners = false, bool Crate = false);

/// <summary>A camp as the tracker knows it: where it is, what kind, how many of its creatures are alive, and when it was last cleared.</summary>
public sealed record CampView(int Id, CampKind Kind, float X, float Y, int Alive, TimeSpan? ClearedAt);

public enum CampEventType { Cleared, Respawned }

/// <summary>A clear, or a respawn with how long it took (<see cref="Took"/>), for the log that checks the intervals.</summary>
public sealed record CampEvent(CampEventType Type, CampKind Kind, float X, float Y, TimeSpan At, TimeSpan? Took = null);

/// <summary>
/// The neutral camps seen in the broadcast, from one snapshot of alive neutrals a second (Direct feed). A creature
/// belongs to the camp it spawned at: the first camp whose centre is within <see cref="CampTimerSettings.CampRadius"/>
/// of where it was first seen, else a new camp there. Membership is by creature, so a creature chasing a player away
/// from its camp still counts for it. A camp whose creatures are all gone is cleared; new creatures at a cleared camp are
/// its respawn, logged with the time it took. A camp's kind comes from when it first spawned (small at the small camps'
/// first spawn, and so on; Sinner's Sacrifice by its own entity), so a camp first seen at any other time, which happens
/// when the app joins a match late, stays <see cref="CampKind.Unknown"/> and is not timed.
/// <para>
/// Breakable crates are tracked the same way, apart from the creatures: crates standing within
/// <see cref="CampTimerSettings.CrateRadius"/> of each other form one crate spot, which is cleared once every crate in it
/// is broken; spots first seen at the crates' first spawn (3:00) are <see cref="CampKind.Crates"/>, at Midtown's (5:00)
/// <see cref="CampKind.MidtownCrates"/>. The few breakable props there before 3:00 stay unknown.
/// </para>
/// </summary>
public sealed class CampTracker
{
    private sealed class Camp
    {
        public int Id;
        public CampKind Kind;
        public double SumX, SumY;
        public int Points;
        public readonly HashSet<long> Members = new();
        public TimeSpan? ClearedAt;
        public bool IsCrates;
        public float X => (float)(SumX / Points);
        public float Y => (float)(SumY / Points);
    }

    private readonly object _gate = new();   // the match pump observes, the overlay reads once a second
    private readonly List<Camp> _camps = new();
    private readonly Dictionary<long, Camp> _creatureCamp = new();
    private readonly double _radiusSquared, _crateRadiusSquared;
    private readonly (CampKind Kind, int Seconds)[] _firstSpawns, _crateFirstSpawns;

    /// <param name="firstSpawns">Each kind's first spawn by the game clock; defaults to the verified 2:00, 5:00 and 8:00 (note 38).</param>
    /// <param name="crateFirstSpawns">The crates' and Midtown crates' first spawns; defaults to the verified 3:00 and 5:00 (note 38).</param>
    public CampTracker(int campRadius = 900, (int Small, int Medium, int Large)? firstSpawns = null,
                       int crateRadius = 600, (int Crates, int Midtown)? crateFirstSpawns = null)
    {
        _radiusSquared = (double)campRadius * campRadius;
        _crateRadiusSquared = (double)crateRadius * crateRadius;
        var f = firstSpawns ?? (120, 300, 480);
        _firstSpawns = new[] { (CampKind.Small, f.Small), (CampKind.Medium, f.Medium), (CampKind.Large, f.Large) };
        var c = crateFirstSpawns ?? (180, 300);
        _crateFirstSpawns = new[] { (CampKind.Crates, c.Crates), (CampKind.MidtownCrates, c.Midtown) };
    }

    /// <summary>A tracker set up from the settings: radii and every kind's first spawn.</summary>
    public static CampTracker For(CampTimerSettings camps, TimerSettings timers)
        => new(camps.CampRadius, FirstSpawns(timers), camps.CrateRadius, CrateFirstSpawns(timers));

    /// <summary>The crates' and Midtown crates' first spawns from the timers' spawn list (by name), else the defaults.</summary>
    public static (int Crates, int Midtown) CrateFirstSpawns(TimerSettings timers)
    {
        int Find(string name, int fallback) =>
            timers.Spawns.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { FirstSpawnSeconds: > 0 } rule
                ? rule.FirstSpawnSeconds : fallback;
        return (Find("Crates", 180), Find("Midtown crates", 300));
    }

    /// <summary>The first-spawn times of the small, medium and large camps from the timers' spawn list (by name), else the defaults.</summary>
    public static (int Small, int Medium, int Large) FirstSpawns(TimerSettings timers)
    {
        int Find(string name, int fallback) =>
            timers.Spawns.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { FirstSpawnSeconds: > 0 } rule
                ? rule.FirstSpawnSeconds : fallback;
        return (Find("Small camps", 120), Find("Medium camps", 300), Find("Large camps", 480));
    }

    /// <summary>How long after its first spawn a camp first seen still counts as that kind; past it, the kind is unknown.</summary>
    public static readonly TimeSpan KindWindow = TimeSpan.FromSeconds(90);

    public IReadOnlyList<CampView> Camps
    {
        get { lock (_gate) return _camps.Select(c => new CampView(c.Id, c.Kind, c.X, c.Y, c.Members.Count, c.ClearedAt)).ToList(); }
    }

    /// <summary>Takes one snapshot of the alive neutrals at <paramref name="gameTime"/>; returns the clears and respawns it saw.</summary>
    public IReadOnlyList<CampEvent> Observe(TimeSpan gameTime, IReadOnlyCollection<NeutralSighting> alive)
    {
        lock (_gate) return ObserveLocked(gameTime, alive);
    }

    private List<CampEvent> ObserveLocked(TimeSpan gameTime, IReadOnlyCollection<NeutralSighting> alive)
    {
        var events = new List<CampEvent>();
        var aliveIds = new HashSet<long>(alive.Select(a => a.Id));

        // Creatures that are gone: dead, or removed from the world.
        foreach (var camp in _camps)
        {
            if (camp.Members.Count == 0) continue;
            camp.Members.RemoveWhere(id =>
            {
                if (aliveIds.Contains(id)) return false;
                _creatureCamp.Remove(id);
                return true;
            });
            if (camp.Members.Count == 0)
            {
                camp.ClearedAt = gameTime;
                events.Add(new CampEvent(CampEventType.Cleared, camp.Kind, camp.X, camp.Y, gameTime));
            }
        }

        // Creatures seen for the first time: they belong to the camp where they spawned.
        foreach (var n in alive)
        {
            if (_creatureCamp.ContainsKey(n.Id)) continue;
            var camp = Nearest(n.X, n.Y, n.Sinners, n.Crate);
            if (camp is null)
            {
                camp = new Camp
                {
                    Id = _camps.Count + 1, IsCrates = n.Crate,
                    Kind = n.Sinners ? CampKind.Sinners : KindAt(gameTime, n.Crate ? _crateFirstSpawns : _firstSpawns),
                };
                _camps.Add(camp);
            }
            else if (camp.Members.Count == 0 && camp.ClearedAt is { } cleared)
            {
                events.Add(new CampEvent(CampEventType.Respawned, camp.Kind, camp.X, camp.Y, gameTime, gameTime - cleared));
                camp.ClearedAt = null;
            }
            camp.Members.Add(n.Id);
            camp.SumX += n.X; camp.SumY += n.Y; camp.Points++;
            _creatureCamp[n.Id] = camp;
        }
        return events;
    }

    private Camp? Nearest(float x, float y, bool sinners, bool crate)
    {
        Camp? best = null;
        double bestD = double.MaxValue;
        double radius = crate ? _crateRadiusSquared : _radiusSquared;
        foreach (var c in _camps)
        {
            if ((c.Kind == CampKind.Sinners) != sinners || c.IsCrates != crate) continue;
            double dx = c.X - x, dy = c.Y - y, d = dx * dx + dy * dy;
            if (d <= radius && d < bestD) { best = c; bestD = d; }
        }
        return best;
    }

    private static CampKind KindAt(TimeSpan gameTime, (CampKind Kind, int Seconds)[] firstSpawns)
    {
        foreach (var (kind, seconds) in firstSpawns)
        {
            var first = TimeSpan.FromSeconds(seconds);
            if (gameTime >= first - TimeSpan.FromSeconds(15) && gameTime <= first + KindWindow) return kind;
        }
        return CampKind.Unknown;
    }
}

/// <summary>
/// Timer rows for camp respawns: each cleared camp of a known kind counts down to its clear plus its kind's interval,
/// turning to "push now" inside the panel's lead like the first spawns. Only the <see cref="CampTimerSettings.MaxRows"/>
/// cleared camps nearest you are shown (soonest first when your position is unknown), each with its distance.
/// </summary>
public static class CampTimers
{
    /// <summary>Map units to metres (Source units are inches); for the rough distance shown on a row.</summary>
    public const double MetresPerUnit = 0.0254;

    public static TimeSpan? Interval(CampKind kind, CampTimerSettings s) => kind switch
    {
        CampKind.Crates or CampKind.MidtownCrates when !s.Crates => null,
        CampKind.Crates => Seconds(s.CrateRespawnSeconds),
        CampKind.MidtownCrates => Seconds(s.MidtownCrateRespawnSeconds),
        CampKind.Small => Seconds(s.SmallRespawnSeconds),
        CampKind.Medium => Seconds(s.MediumRespawnSeconds),
        CampKind.Large => Seconds(s.LargeRespawnSeconds),
        CampKind.Sinners => Seconds(s.SinnersRespawnSeconds),
        _ => null,
    };

    private static TimeSpan? Seconds(int s) => s > 0 ? TimeSpan.FromSeconds(s) : null;

    public static string Name(CampKind kind) => kind switch
    {
        CampKind.Small => "Small camp",
        CampKind.Medium => "Medium camp",
        CampKind.Large => "Large camp",
        CampKind.Sinners => "Sinner's Sacrifice",
        CampKind.Crates => "Crates",
        CampKind.MidtownCrates => "Midtown crates",
        _ => "Camp",
    };

    public static IReadOnlyList<TimerRow> Rows(TimeSpan now, IReadOnlyList<CampView> camps, (float X, float Y)? me,
                                               CampTimerSettings settings, TimerSettings timers)
    {
        if (!settings.Enabled || settings.MaxRows <= 0) return Array.Empty<TimerRow>();
        var lead = TimeSpan.FromSeconds(Math.Max(0, timers.LeadSeconds));
        var showUp = TimeSpan.FromSeconds(Math.Max(0, timers.ShowUpSeconds));
        var due = camps
            .Where(c => c.ClearedAt is not null && c.Alive == 0)
            .Select(c => (Camp: c, Interval: Interval(c.Kind, settings)))
            .Where(x => x.Interval is not null)
            .Select(x => (x.Camp, At: x.Camp.ClearedAt!.Value + x.Interval!.Value,
                          Distance: me is { } p ? Math.Sqrt(Math.Pow(x.Camp.X - p.X, 2) + Math.Pow(x.Camp.Y - p.Y, 2)) : (double?)null))
            .Where(x => now < x.At + showUp);
        var chosen = (me is null ? due.OrderBy(x => x.At) : due.OrderBy(x => x.Distance)).Take(settings.MaxRows);
        return chosen
            .OrderBy(x => x.At)
            .Select(x =>
            {
                var left = x.At - now;
                var state = left <= TimeSpan.Zero ? TimerState.Up : left <= lead ? TimerState.PushNow : TimerState.Later;
                string where = x.Distance is { } d ? $" · {Metres(d)} m" : "";
                string what = x.Camp.Kind is CampKind.Crates or CampKind.MidtownCrates ? "broken" : "cleared";
                return new TimerRow(x.At, Name(x.Camp.Kind) + where, $"{what} {Clock(x.Camp.ClearedAt!.Value)}", left, state);
            })
            .ToList();
    }

    /// <summary>A distance in map units as metres, rounded to 10 (at least 10).</summary>
    public static int Metres(double units) => Math.Max(10, (int)Math.Round(units * MetresPerUnit / 10) * 10);

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
