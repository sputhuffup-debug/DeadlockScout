using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class CampTimersTests
{
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);

    // Two small camps 5000 units apart, three creatures each, spawning at 2:00.
    private static NeutralSighting[] SmallCamps(long baseId = 1) => new NeutralSighting[]
    {
        new(baseId, 1000, 1000), new(baseId + 1, 1200, 1100), new(baseId + 2, 1100, 900),
        new(baseId + 10, 6000, 1000), new(baseId + 11, 6150, 1050), new(baseId + 12, 6100, 900),
    };

    [Fact]
    public void Creatures_group_into_camps_by_where_they_spawn_and_the_kind_comes_from_the_first_spawn_time()
    {
        var t = new CampTracker();
        Assert.Empty(t.Observe(T(1, 59), Array.Empty<NeutralSighting>()));
        t.Observe(T(2, 1), SmallCamps());
        t.Observe(T(5, 2), new[] { new NeutralSighting(100, -4000, 3000), new NeutralSighting(101, -4100, 3200) }.Concat(SmallCamps()).ToArray());
        t.Observe(T(8, 1), new[] { new NeutralSighting(200, 0, -7000, Sinners: true) }.Concat(SmallCamps()).ToArray());

        var camps = t.Camps;
        Assert.Equal(new[] { CampKind.Small, CampKind.Small, CampKind.Medium, CampKind.Sinners }, camps.Select(c => c.Kind));
        Assert.Equal(new[] { 3, 3, 0, 1 }, camps.Select(c => c.Alive));   // the medium camp's creatures were not in the last snapshot
        Assert.InRange(camps[0].X, 1099, 1101);                           // the centre is the mean of where its creatures spawned
    }

    [Fact]
    public void A_camp_is_cleared_when_its_last_creature_dies_even_away_from_the_camp_and_its_respawn_is_timed()
    {
        var t = new CampTracker();
        t.Observe(T(2, 0), SmallCamps());
        // Two creatures die; the third chases someone 3000 units away, still alive.
        var chasing = new NeutralSighting(3, 4100, 900);
        Assert.Empty(t.Observe(T(2, 30), new[] { chasing }.Concat(SmallCamps().Skip(3)).ToArray()));
        Assert.Equal(new[] { 1, 3 }, t.Camps.Select(c => c.Alive));        // it still counts for its own camp, not the second one

        var cleared = t.Observe(T(2, 40), SmallCamps().Skip(3).ToArray()).Single();
        Assert.Equal((CampEventType.Cleared, CampKind.Small, T(2, 40)), (cleared.Type, cleared.Kind, cleared.At));
        Assert.Equal(T(2, 40), t.Camps[0].ClearedAt);
        Assert.Empty(t.Observe(T(3, 0), SmallCamps().Skip(3).ToArray()));  // a cleared camp is not cleared again

        // New creatures at the first camp: its respawn, 1:27 after the clear.
        var back = t.Observe(T(4, 7), new[] { new NeutralSighting(50, 1050, 980), new NeutralSighting(51, 1150, 1020) }.Concat(SmallCamps().Skip(3)).ToArray()).Single();
        Assert.Equal((CampEventType.Respawned, CampKind.Small, T(1, 27)), (back.Type, back.Kind, back.Took));
        Assert.Null(t.Camps[0].ClearedAt);
        Assert.Equal(2, t.Camps.Count);                                     // the respawn joined its camp, not a new one
    }

    [Fact]
    public void A_camp_first_seen_long_after_its_first_spawn_is_unknown_and_not_timed()
    {
        var t = new CampTracker();
        t.Observe(T(12, 0), SmallCamps());                                  // the app joined late
        Assert.All(t.Camps, c => Assert.Equal(CampKind.Unknown, c.Kind));
        t.Observe(T(12, 30), Array.Empty<NeutralSighting>());
        Assert.Empty(CampTimers.Rows(T(12, 40), t.Camps, null, new CampTimerSettings(), new TimerSettings()));
    }

    [Fact]
    public void Rows_count_down_to_the_clear_plus_the_interval_for_the_cleared_camps_nearest_you()
    {
        var t = new CampTracker();
        t.Observe(T(2, 0), SmallCamps());
        t.Observe(T(2, 40), Array.Empty<NeutralSighting>());                // both cleared at 2:40, back at 4:05
        var settings = new CampTimerSettings { MaxRows = 1 };

        // Standing by the second camp: only it is shown, with its distance.
        var near = CampTimers.Rows(T(3, 0), t.Camps, (6000f, 1100f), settings, new TimerSettings()).Single();
        Assert.Equal(T(4, 5), near.At);
        Assert.Equal(T(1, 5), near.In);
        Assert.Equal("Small camp · 10 m", near.Names);
        Assert.Equal("cleared 2:40", near.Note);
        Assert.Equal(TimerState.Later, near.State);

        Assert.Equal(TimerState.PushNow, CampTimers.Rows(T(3, 40), t.Camps, (6000f, 1100f), settings, new TimerSettings()).Single().State);
        Assert.Equal(TimerState.Up, CampTimers.Rows(T(4, 10), t.Camps, (6000f, 1100f), settings, new TimerSettings()).Single().State);
        Assert.Empty(CampTimers.Rows(T(4, 26), t.Camps, (6000f, 1100f), settings, new TimerSettings())); // gone 20 s after its return

        // Far from both (5000 units is 127 m, shown as 130): the nearer one.
        Assert.Equal("Small camp · 130 m", CampTimers.Rows(T(3, 0), t.Camps, (1000f, 6000f), settings, new TimerSettings()).Single().Names);
        // Position unknown: soonest first, without a distance.
        Assert.Equal("Small camp", CampTimers.Rows(T(3, 0), t.Camps, null, settings, new TimerSettings()).Single().Names);
        Assert.Empty(CampTimers.Rows(T(3, 0), t.Camps, null, settings with { Enabled = false }, new TimerSettings()));
    }

    [Fact]
    public void Camp_rows_share_the_panel_with_the_first_spawns_soonest_first()
    {
        var first = SpawnTimers.Rows(T(4, 0), new TimerSettings());         // 5:00 medium camps, 8:00 large camps
        var t = new CampTracker();
        t.Observe(T(2, 0), SmallCamps());
        t.Observe(T(3, 0), Array.Empty<NeutralSighting>());                 // back at 4:25
        var camps = CampTimers.Rows(T(4, 0), t.Camps, (1100f, 1000f), new CampTimerSettings { MaxRows = 1 }, new TimerSettings());
        var merged = SpawnTimers.Merge(first, camps, 3);
        Assert.Equal(new[] { "Small camp · 10 m", "Medium camps · Midtown crates", "Large camps · Sinner's Sacrifice" }, merged.Select(r => r.Names));
    }

    [Fact]
    public void Distances_are_rough_metres()
    {
        Assert.Equal(10, CampTimers.Metres(0));
        Assert.Equal(130, CampTimers.Metres(5000));
        Assert.Equal(250, CampTimers.Metres(10000));
    }

    // A pile of three crates at (3000, 3000) and one at (3000, 8000); Midtown's pile of two at (0, 0).
    private static NeutralSighting Crate(long id, float x, float y) => new(id, x, y, Crate: true);
    private static readonly NeutralSighting[] Piles = { Crate(500, 3000, 3000), Crate(501, 3200, 3100), Crate(502, 2900, 3150), Crate(510, 3000, 8000) };
    private static readonly NeutralSighting[] Midtown = { Crate(600, 0, 0), Crate(601, 150, 100) };

    [Fact]
    public void Crates_group_into_spots_of_their_own_and_their_kind_comes_from_their_first_spawn()
    {
        var t = new CampTracker();
        // A breakable prop there from the start, and a small camp's creature next to the first pile at 2:00.
        t.Observe(T(0, 10), new[] { Crate(400, -9000, -9000) });
        t.Observe(T(2, 0), new[] { Crate(400, -9000, -9000), new NeutralSighting(1, 3100, 3000) });
        t.Observe(T(3, 1), new[] { Crate(400, -9000, -9000), new NeutralSighting(1, 3100, 3000) }.Concat(Piles).ToArray());
        t.Observe(T(5, 1), new[] { Crate(400, -9000, -9000), new NeutralSighting(1, 3100, 3000) }.Concat(Piles).Concat(Midtown).ToArray());

        var kinds = t.Camps.Select(c => (c.Kind, c.Alive)).ToList();
        Assert.Equal(new[] { (CampKind.Unknown, 1), (CampKind.Small, 1), (CampKind.Crates, 3), (CampKind.Crates, 1), (CampKind.MidtownCrates, 2) }, kinds);
    }

    [Fact]
    public void A_crate_spot_counts_once_every_crate_in_it_is_broken_and_its_return_is_timed()
    {
        var t = new CampTracker();
        t.Observe(T(3, 0), Piles);
        // Two of the three broken: not cleared yet.
        Assert.Empty(t.Observe(T(3, 20), Piles.Skip(2).ToArray()));
        var cleared = t.Observe(T(3, 40), Piles.Skip(3).ToArray());
        Assert.Equal(CampEventType.Cleared, Assert.Single(cleared).Type);

        var settings = new CampTimerSettings();
        var row = Assert.Single(CampTimers.Rows(T(4, 0), t.Camps, (3000f, 3500f), settings, new TimerSettings()));
        Assert.Equal(T(6, 40), row.At);                       // broken at 3:40, back 3:00 later
        Assert.StartsWith("Crates · ", row.Names);
        Assert.Equal("broken 3:40", row.Note);

        // New crates there: the respawn, with how long it took.
        var back = t.Observe(T(6, 41), new[] { Crate(700, 3050, 3000) }.Concat(Piles.Skip(3)).ToArray());
        var e = Assert.Single(back);
        Assert.Equal((CampEventType.Respawned, CampKind.Crates, T(3, 1)), (e.Type, e.Kind, e.Took!.Value));
    }

    [Fact]
    public void Crate_rows_switch_off_on_their_own()
    {
        var t = new CampTracker();
        t.Observe(T(5, 0), Midtown);
        t.Observe(T(5, 30), Array.Empty<NeutralSighting>());
        Assert.Single(CampTimers.Rows(T(6, 0), t.Camps, null, new CampTimerSettings(), new TimerSettings()));
        Assert.Empty(CampTimers.Rows(T(6, 0), t.Camps, null, new CampTimerSettings { Crates = false }, new TimerSettings()));
        Assert.Equal(TimeSpan.FromMinutes(5), CampTimers.Interval(CampKind.MidtownCrates, new CampTimerSettings()));
    }

    [Fact]
    public void The_tracker_takes_the_crates_first_spawns_from_the_spawn_list()
    {
        var timers = new TimerSettings();
        Assert.Equal((180, 300), CampTracker.CrateFirstSpawns(timers));
        var moved = timers with { Spawns = timers.Spawns.Select(s => s.Name == "Crates" ? s with { FirstSpawnSeconds = 200 } : s).ToList() };
        Assert.Equal((200, 300), CampTracker.CrateFirstSpawns(moved));
    }
}
