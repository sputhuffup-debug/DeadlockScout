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
}
