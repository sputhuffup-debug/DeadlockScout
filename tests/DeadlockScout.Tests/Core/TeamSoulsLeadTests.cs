using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class TeamSoulsLeadTests
{
    // Two a side: 1 and 2 on Amber, 3 and 4 on Sapphire.
    private static TeamSoulsLead Lead() => new(new (uint, int)[] { (1, Teams.Amber), (2, Teams.Amber), (3, Teams.Sapphire), (4, Teams.Sapphire) });

    private static void At(TeamSoulsLead lead, int seconds, int a1, int a2, int s1, int s2)
    {
        var t = TimeSpan.FromSeconds(seconds);
        lead.Observe(new NetWorthTick(t, 1, a1, 0, 0, 0));
        lead.Observe(new NetWorthTick(t, 2, a2, 0, 0, 0));
        lead.Observe(new NetWorthTick(t, 3, s1, 0, 0, 0));
        lead.Observe(new NetWorthTick(t, 4, s2, 0, 0, 0));
    }

    [Fact]
    public void Nothing_shows_until_every_player_has_been_seen()
    {
        var lead = Lead();
        lead.Observe(new NetWorthTick(TimeSpan.FromSeconds(60), 1, 900, 0, 0, 0));
        lead.Observe(new NetWorthTick(TimeSpan.FromSeconds(60), 3, 800, 0, 0, 0));
        Assert.Null(lead.View(Teams.Amber));
        lead.Observe(new NetWorthTick(TimeSpan.FromSeconds(60), 99, 5000, 0, 0, 0)); // not in the roster
        Assert.Null(lead.View(Teams.Amber));
    }

    [Fact]
    public void The_gap_is_from_your_side_and_has_no_trend_before_three_minutes()
    {
        var lead = Lead();
        At(lead, 600, 6000, 5000, 4000, 2800);
        var amber = lead.View(Teams.Amber)!;
        Assert.Equal((11000, 6800, 4200, (int?)null), (amber.Amber, amber.Sapphire, amber.Gap, amber.Change));
        Assert.Equal("Your team +4.2k", amber.Text);
        Assert.Equal("Enemy +4.2k", lead.View(Teams.Sapphire)!.Text);
        Assert.Equal("Amber +4.2k", lead.View(0)!.Text);              // spectating
        Assert.Equal(TimeSpan.FromSeconds(600), amber.GameTime);
    }

    [Fact]
    public void Over_three_minutes_a_lead_grows_closes_or_holds()
    {
        var growing = Lead();
        At(growing, 600, 6000, 5000, 5000, 5000);   // Amber +1.0k
        At(growing, 780, 9000, 8000, 6000, 6000);   // Amber +5.0k three minutes later
        Assert.Equal("Your team +5.0k, growing", growing.View(Teams.Amber)!.Text);
        Assert.Equal("Enemy +5.0k, growing", growing.View(Teams.Sapphire)!.Text);
        Assert.Equal(4000, growing.View(Teams.Amber)!.Change);

        var closing = Lead();
        At(closing, 600, 9000, 8000, 6000, 6000);   // Amber +5.0k
        At(closing, 780, 10000, 9000, 9000, 9000);  // Amber +1.0k: a fight Sapphire won
        Assert.Equal("Your team +1.0k, closing", closing.View(Teams.Amber)!.Text);
        Assert.Equal("Enemy +1.0k, closing", closing.View(Teams.Sapphire)!.Text);

        var steady = Lead();
        At(steady, 600, 6000, 5000, 5000, 5000);
        At(steady, 780, 9000, 8000, 7900, 8000);    // +1.0k then +1.1k
        Assert.Equal("Your team +1.1k, steady", steady.View(Teams.Amber)!.Text);
    }

    [Fact]
    public void The_trend_compares_with_about_three_minutes_ago_not_the_start()
    {
        var lead = Lead();
        At(lead, 0, 0, 0, 0, 0);
        for (int s = 5; s <= 900; s += 5)
        {
            // Sapphire takes a 6k lead by 10:00, then Amber claws back 3k in the last five minutes.
            int sap = s <= 600 ? s * 10 : 6000 - (s - 600) * 10;
            At(lead, s, 0, 0, Math.Max(0, sap), 0);
        }
        var view = lead.View(Teams.Amber)!;
        Assert.Equal(-3000, view.Gap);
        Assert.Equal(1800, view.Change);              // 12:00 → 15:00: from -4.8k to -3.0k
        Assert.Equal("Enemy +3.0k, closing", view.Text);
    }

    [Fact]
    public void Souls_are_shortened_to_thousands()
    {
        Assert.Equal("950", TeamSoulsLead.Short(950));
        Assert.Equal("4.2k", TeamSoulsLead.Short(4213));
        Assert.Equal("12.0k", TeamSoulsLead.Short(12000));
    }
}
