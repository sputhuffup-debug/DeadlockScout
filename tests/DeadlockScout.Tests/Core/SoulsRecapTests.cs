using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class SoulsRecapTests
{
    private static SoulSample Ocr(int seconds, int unspent, int? haunted = null) =>
        new(TimeSpan.FromSeconds(seconds), 20_000, SampleSource.Ocr, unspent, haunted);

    [Fact]
    public void Time_on_unspent_souls_over_the_threshold_adds_up_with_the_longest_stretch()
    {
        var recap = new SoulsRecap(3_000);
        // 10:00–10:59 under, 11:00–12:59 over (2:00), 13:00–13:59 under, 14:00–14:29 over (0:30).
        for (int t = 600; t < 900; t++)
            recap.Observe(Ocr(t, t is >= 660 and < 780 || t >= 840 && t < 870 ? 3_500 : 1_000));
        Assert.Equal("3k+ unspent for 2:30, longest 2:00 from 11:00", recap.Line());
    }

    [Fact]
    public void A_gap_counts_at_most_ten_seconds()
    {
        var recap = new SoulsRecap(3_000);
        recap.Observe(Ocr(600, 4_000));
        recap.Observe(Ocr(900, 4_000)); // five minutes without a reading
        Assert.Equal("3k+ unspent for 0:10", recap.Line());
    }

    [Fact]
    public void A_death_that_takes_the_haunted_souls_is_a_loss_and_securing_them_is_not()
    {
        var recap = new SoulsRecap(0);
        recap.Observe(Ocr(600, 2_000, haunted: 1_051));
        var loss = recap.Observe(Ocr(601, 949));          // died: the haunted line gone, unspent down by about as much
        Assert.Equal(new HauntedLoss(TimeSpan.FromSeconds(601), 1_051), loss);
        Assert.Equal("Died at 10:01: lost 1,051 haunted souls", recap.LossLine(TimeSpan.FromSeconds(620)));
        Assert.Null(recap.LossLine(TimeSpan.FromSeconds(640)));   // shown for 30 s

        recap.Observe(Ocr(700, 1_500, haunted: 600));
        Assert.Null(recap.Observe(Ocr(701, 1_500)));      // secured: they joined the count, unspent unchanged
        recap.Observe(Ocr(800, 3_000, haunted: 1_300));
        Assert.NotNull(recap.Observe(Ocr(801, 1_700)));
        Assert.Equal("lost 2.4k haunted souls in 2 deaths", recap.Line());
    }

    [Fact]
    public void Nothing_to_say_without_time_over_or_losses()
    {
        var recap = new SoulsRecap(3_000);
        for (int t = 0; t < 60; t++) recap.Observe(Ocr(t, 500));
        Assert.Null(recap.Line());
        Assert.Null(recap.Observe(new SoulSample(TimeSpan.FromSeconds(61), 9_000, SampleSource.LiveFeed))); // no unspent known
        Assert.Null(recap.Observe(Ocr(30, 9_000)));       // out of order
    }
}
