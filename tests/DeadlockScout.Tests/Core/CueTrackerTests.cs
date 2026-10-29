using DeadlockScout.Core.Cues;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class CueTrackerTests
{
    private static readonly AudioSettings On = new() { Enabled = true };
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);
    private static CueInput At(TimeSpan t, TimeSpan[]? push = null, string[]? soon = null, double? goal = null)
        => new(t, push ?? Array.Empty<TimeSpan>(), soon ?? Array.Empty<string>(), goal);

    [Fact]
    public void Each_spawn_and_objective_cues_once_when_it_enters_its_lead()
    {
        var cues = new CueTracker();
        Assert.Equal(new[] { Cue.PushNow }, cues.Update(At(T(1, 31), push: new[] { T(2) }), On));
        Assert.Empty(cues.Update(At(T(1, 32), push: new[] { T(2) }), On));                 // still inside the lead: once only
        Assert.Equal(new[] { Cue.Objective }, cues.Update(At(T(17, 51), soon: new[] { "boss@18:20" }), On));
        Assert.Empty(cues.Update(At(T(17, 52), soon: new[] { "boss@18:20" }), On));
        Assert.Equal(new[] { Cue.PushNow, Cue.Objective },
            cues.Update(At(T(4, 31), push: new[] { T(5) }, soon: new[] { "urn@5:00" }), On));
    }

    [Fact]
    public void Pace_cues_after_a_minute_under_the_gate_then_cools_down()
    {
        var cues = new CueTracker();
        var heard = new List<TimeSpan>();
        for (int s = 0; s <= 6 * 60; s += 1)
        {
            double goal = s < 300 ? -0.08 : 0.02;                                         // behind until 5:00, then level
            if (cues.Update(At(TimeSpan.FromSeconds(s), goal: goal), On).Contains(Cue.PaceBehind)) heard.Add(TimeSpan.FromSeconds(s));
        }
        Assert.Equal(new[] { T(1), T(4) }, heard);                                        // a minute in, then after the 3-minute cooldown

        var shallow = new CueTracker();
        for (int s = 0; s <= 120; s++) Assert.Empty(shallow.Update(At(TimeSpan.FromSeconds(s), goal: -0.03), On)); // under 5% behind
    }

    [Fact]
    public void Switched_off_cues_stay_quiet_and_do_not_fire_late_when_switched_on()
    {
        var cues = new CueTracker();
        Assert.Empty(cues.Update(At(T(1, 31), push: new[] { T(2) }), new AudioSettings()));  // master off by default
        Assert.Empty(cues.Update(At(T(1, 32), push: new[] { T(2) }), On));                   // already heard
        Assert.Empty(cues.Update(At(T(2, 31), push: new[] { T(3) }), On with { PushNow = false }));
    }
}
