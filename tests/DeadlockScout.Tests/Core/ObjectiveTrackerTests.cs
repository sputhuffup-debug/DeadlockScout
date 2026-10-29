using DeadlockScout.Core.Models;
using DeadlockScout.Core.Objectives;

namespace DeadlockScout.Tests.Core;

public class ObjectiveTrackerTests
{
    private static ObjectiveSample At(int minutes, int seconds, bool? boss, int amber = 0, int sapphire = 0, bool? urn = null)
        => new(new TimeSpan(0, minutes, seconds), boss, amber, sapphire, urn);

    [Fact]
    public void Nothing_before_the_first_sample()
        => Assert.Null(new ObjectiveTracker().Snapshot());

    [Fact]
    public void Kill_records_the_time_the_team_from_the_rejuvenator_and_the_estimated_return()
    {
        var t = new ObjectiveTracker(TimeSpan.FromMinutes(7));
        t.Observe(At(20, 0, true));
        t.Observe(At(23, 10, false));
        Assert.Null(t.Snapshot()!.MidBoss!.TakenBy); // the drop is picked up a moment later
        t.Observe(At(23, 14, false, amber: 1));

        var boss = t.Snapshot()!.MidBoss!;
        Assert.False(boss.Alive);
        Assert.Equal(new TimeSpan(0, 23, 10), boss.DownSince);
        Assert.Equal(Teams.Amber, boss.TakenBy);
        Assert.Equal(new TimeSpan(0, 30, 10), boss.BackAt);
        Assert.Equal(1, t.Snapshot()!.AmberRejuvs);
    }

    [Fact]
    public void An_observed_respawn_replaces_the_default_for_the_next_estimate()
    {
        var t = new ObjectiveTracker(TimeSpan.FromMinutes(7));
        t.Observe(At(20, 0, true));
        t.Observe(At(21, 0, false));
        Assert.Equal(TimeSpan.FromMinutes(6), t.Observe(At(27, 0, true)));
        Assert.True(t.Snapshot()!.MidBoss!.Alive);

        t.Observe(At(30, 0, false));
        Assert.Equal(new TimeSpan(0, 36, 0), t.Snapshot()!.MidBoss!.BackAt);
    }

    [Fact]
    public void Joining_while_the_boss_is_down_shows_down_without_a_time_and_counts_are_not_a_kill()
    {
        var t = new ObjectiveTracker();
        t.Observe(At(25, 0, false, sapphire: 2));
        var boss = t.Snapshot()!.MidBoss!;
        Assert.False(boss.Alive);
        Assert.Null(boss.DownSince);
        Assert.Null(boss.BackAt);
        Assert.Null(boss.TakenBy);
        Assert.Equal(2, t.Snapshot()!.SapphireRejuvs);
    }

    [Fact]
    public void Urn_changes_are_timed_but_its_first_state_is_not()
    {
        var t = new ObjectiveTracker();
        t.Observe(At(9, 0, true, urn: false));
        Assert.Equal(new UrnView(false, null), t.Snapshot()!.Urn);
        t.Observe(At(10, 0, true, urn: true));
        Assert.Equal(new UrnView(true, new TimeSpan(0, 10, 0)), t.Snapshot()!.Urn);
        t.Observe(At(10, 40, true, urn: false));
        Assert.Equal(new UrnView(false, new TimeSpan(0, 10, 40)), t.Snapshot()!.Urn);
    }

    [Fact]
    public void A_source_without_objectives_leaves_them_unknown()
    {
        var t = new ObjectiveTracker();
        t.Observe(At(5, 0, null));
        var view = t.Snapshot()!;
        Assert.Null(view.MidBoss);
        Assert.Null(view.Urn);
    }
}

public class ObjectiveFormatTests
{
    private static readonly TimeSpan T2310 = new(0, 23, 10), T3010 = new(0, 30, 10);

    [Fact]
    public void Down_boss_reads_relative_to_your_team()
    {
        var v = new ObjectiveView(new MidBossView(false, T2310, Teams.Sapphire, T3010), new UrnView(true, new TimeSpan(0, 20, 0)), 0, 1);
        var (boss, rejuvs, urn) = ObjectiveFormat.Lines(v, Teams.Amber);
        Assert.Equal("Mid boss down 23:10 · back ~30:10 · Rejuv to enemy", boss);
        Assert.Equal("Rejuvenators: you 0 · enemy 1", rejuvs);
        Assert.Equal("Urn on the map since 20:00", urn);
    }

    [Fact]
    public void Spectating_names_the_teams_and_empty_parts_stay_empty()
    {
        var v = new ObjectiveView(new MidBossView(true, null, null, null), new UrnView(false, null), 2, 1);
        var (boss, rejuvs, urn) = ObjectiveFormat.Lines(v, 0);
        Assert.Equal("Mid boss up", boss);
        Assert.Equal("Rejuvenators: Amber 2 · Sapphire 1", rejuvs);
        Assert.Equal("", urn);
        Assert.Equal("", ObjectiveFormat.Lines(v with { AmberRejuvs = 0, SapphireRejuvs = 0 }, 0).Rejuvs);
    }
}

public class ObjectiveCountdownTests
{
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);
    private static readonly TimeSpan Lead = TimeSpan.FromSeconds(30);

    private static ObjectiveView Boss(bool alive, TimeSpan? back, bool urnOnMap = false)
        => new(new MidBossView(alive, alive ? null : T(11, 20), null, back), new UrnView(urnOnMap, null), 0, 0);

    [Fact]
    public void Mid_boss_counts_down_to_its_estimated_return_and_turns_soon_inside_the_lead()
    {
        Assert.Equal(new Countdown("Mid boss back in 1:40", false), ObjectiveCountdowns.MidBoss(Boss(false, T(18, 20)), T(16, 40), Lead));
        Assert.True(ObjectiveCountdowns.MidBoss(Boss(false, T(18, 20)), T(17, 55), Lead)!.Soon);
        Assert.Equal("Mid boss due now (estimate)", ObjectiveCountdowns.MidBoss(Boss(false, T(18, 20)), T(18, 30), Lead)!.Text);
        Assert.Null(ObjectiveCountdowns.MidBoss(Boss(true, null), T(18, 30), Lead));
        Assert.Null(ObjectiveCountdowns.MidBoss(null, T(18, 30), Lead));
    }

    [Fact]
    public void Next_urn_follows_the_schedule_while_none_is_on_the_map()
    {
        Assert.Equal(new Countdown("Next urn 10:00 · in 5:25", false), ObjectiveCountdowns.NextUrn(null, T(4, 35), T(10), T(5), Lead));
        Assert.Equal(new Countdown("Next urn 15:00 · in 0:20", true), ObjectiveCountdowns.NextUrn(Boss(true, null), T(14, 40), T(10), T(5), Lead));
        Assert.Null(ObjectiveCountdowns.NextUrn(Boss(true, null, urnOnMap: true), T(14, 40), T(10), T(5), Lead));
        Assert.Null(ObjectiveCountdowns.NextUrn(null, T(4, 35), TimeSpan.Zero, T(5), Lead));
        Assert.Null(ObjectiveCountdowns.NextUrn(null, T(12), T(10), TimeSpan.Zero, Lead)); // one urn only, already past
    }
}
