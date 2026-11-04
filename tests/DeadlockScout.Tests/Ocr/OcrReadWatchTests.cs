using DeadlockScout.Ocr;

namespace DeadlockScout.Tests.Ocr;

public class OcrReadWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_box_unread_for_the_whole_wait_is_named_once_a_match()
    {
        var watch = new OcrReadWatch();
        string? warning = null;
        for (int s = 0; s <= 60 && warning is null; s++) warning = watch.Record(soulsRead: false, clockRead: true, T0.AddSeconds(s));
        Assert.Contains("your souls box", warning);
        Assert.DoesNotContain("clock", warning!.Split('.')[0]);
        for (int s = 61; s <= 200; s++) Assert.Null(watch.Record(false, false, T0.AddSeconds(s))); // once a match
        watch.Reset();
        string? next = null;
        for (int s = 0; s <= 60 && next is null; s++) next = watch.Record(false, false, T0.AddMinutes(40).AddSeconds(s));
        Assert.Contains("your souls box and the match clock box", next);
    }

    [Fact]
    public void Reading_now_and_then_or_a_short_gap_is_no_warning()
    {
        var watch = new OcrReadWatch();
        for (int s = 0; s < 300; s++)
            Assert.Null(watch.Record(soulsRead: s % 20 == 0, clockRead: true, T0.AddSeconds(s))); // a read every 20 s
        for (int s = 300; s < 340; s++) Assert.Null(watch.Record(true, false, T0.AddSeconds(s))); // 40 s: the shop, say
        Assert.Null(watch.Record(true, true, T0.AddSeconds(340)));
    }
}
