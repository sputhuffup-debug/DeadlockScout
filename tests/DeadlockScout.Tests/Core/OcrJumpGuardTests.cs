using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Sources;

namespace DeadlockScout.Tests.Core;

/// <summary>
/// Synthetic matches: your own souls, and the teammates' counters the HUD shows while you are dead. OCR readings are
/// the HUD counter (unspent souls); the broadcast's samples carry net worth and, from the items held, unspent souls.
/// </summary>
public class OcrJumpGuardTests
{
    private static SoulSample Ocr(int seconds, int souls) => new(TimeSpan.FromSeconds(seconds), souls, SampleSource.Ocr);
    /// <summary>A broadcast sample: unspent souls, plus <paramref name="items"/> worth of items held.</summary>
    private static SoulSample Feed(int seconds, int souls, int items = 0) => new(TimeSpan.FromSeconds(seconds), souls + items, SampleSource.LiveFeed, souls);
    /// <summary>A broadcast at 0:00 saying you hold nothing: tests without a broadcast need the items known.</summary>
    private static OcrJumpGuard Known(OcrJumpGuard guard) { guard.OfferFeed(Feed(0, 0)); return guard; }
    private static readonly int[] Shop = { 800, 1_600, 3_200, 6_400 };

    /// <summary>Farming at 20 souls a second from 10,000 at 10:00.</summary>
    private static int Me(int seconds) => 10_000 + (seconds - 600) * 20;

    [Fact]
    public void Cycling_between_teammates_while_dead_never_reaches_the_meter_and_respawn_carries_on()
    {
        var guard = Known(new OcrJumpGuard(prices: () => Shop));
        var meter = new SoulRateMeter();
        void Add(IEnumerable<SoulSample> xs) { foreach (var x in xs) meter.Add(x); }

        for (int t = 600; t <= 660; t++) Add(guard.OfferOcr(Ocr(t, Me(t))));
        int atDeath = Me(660);
        // Dead from 10:61 to 11:40: the camera shows a richer teammate, a poorer one, then a third; your counter is frozen.
        for (int t = 661; t <= 675; t++) Add(guard.OfferOcr(Ocr(t, 13_400 + (t - 661) * 25)));
        for (int t = 676; t <= 685; t++) Add(guard.OfferOcr(Ocr(t, 8_100 + (t - 676) * 15)));
        for (int t = 686; t <= 700; t++) Add(guard.OfferOcr(Ocr(t, 11_900 + (t - 686) * 30)));
        Assert.Equal(40, guard.HeldCount);
        Assert.Equal(atDeath, meter.Latest!.Souls); // the overlay holds your last value meanwhile
        // Respawned: your counter again, a little higher from a team objective.
        for (int t = 701; t <= 720; t++) Add(guard.OfferOcr(Ocr(t, atDeath + 120 + (t - 701) * 20)));

        Assert.Equal(0, guard.HeldCount);
        Assert.Equal(40, guard.Dropped);
        Assert.Equal(atDeath + 120 + 19 * 20, meter.Latest!.Souls);
        Assert.True(meter.Read()!.WindowSpm < 1_500); // a teammate's 13k never entered the window
    }

    [Fact]
    public void A_real_jump_is_kept_once_the_broadcast_shows_it_and_a_teammates_counter_is_dropped()
    {
        var guard = new OcrJumpGuard();
        for (int t = 600; t <= 620; t++) guard.OfferOcr(Ocr(t, Me(t)));
        // You deliver the urn at 10:21: +3,000 at once, then farming carries on from there.
        var urn = Enumerable.Range(621, 30).Select(t => Ocr(t, Me(t) + 3_000)).ToList();
        foreach (var s in urn) Assert.Empty(guard.OfferOcr(s));
        Assert.Equal(30, guard.HeldCount);

        // The broadcast, about 35 s behind, shows your own souls doing the same.
        var kept = new List<SoulSample>();
        for (int t = 590; t <= 640; t++) kept.AddRange(guard.OfferFeed(Feed(t, Me(t) + (t >= 621 ? 3_000 : 0))));
        Assert.Equal(urn.Take(19).Select(x => (x.GameTime, x.Souls)), kept.Select(x => (x.GameTime, x.Souls))); // everything up to 10:39 (the broadcast's 10:40 less a second)
        Assert.Equal(Me(639) + 3_000, guard.Trusted!.Souls);

        // Later you die and watch a teammate 4,000 ahead; the broadcast shows your souls standing still.
        var g2 = new OcrJumpGuard();
        for (int t = 600; t <= 620; t++) g2.OfferOcr(Ocr(t, Me(t)));
        for (int t = 621; t <= 650; t++) g2.OfferOcr(Ocr(t, Me(620) + 4_000 + (t - 621) * 25));
        var keptFromDeath = new List<SoulSample>();
        for (int t = 590; t <= 660; t++) keptFromDeath.AddRange(g2.OfferFeed(Feed(t, Me(Math.Min(t, 620)))));
        Assert.Empty(keptFromDeath);
        Assert.Equal(30, g2.Dropped);
        Assert.Equal(0, g2.HeldCount);
    }

    [Fact]
    public void Without_a_broadcast_held_readings_are_kept_after_the_hold_and_misreads_are_dropped()
    {
        var clock = new StepClock();
        var guard = Known(new OcrJumpGuard(maxHold: TimeSpan.FromSeconds(60), clock: clock));
        IReadOnlyList<SoulSample> Offer(int t, int souls) { clock.Second(); return guard.OfferOcr(Ocr(t, souls)); }
        Offer(600, 10_000);
        // A one-off misread (a digit lost) is held, then dropped when the next reading is back on your line.
        Assert.Empty(Offer(601, 1_002));
        Assert.Single(Offer(602, 10_040));
        Assert.Equal(1, guard.Dropped);
        // Small steps go straight through, a few hundred souls a second included (a kill).
        Assert.Single(Offer(603, 10_520));

        // A jump with no broadcast to check it against: held for 60 s, then kept with everything after it.
        for (int t = 604; t < 664; t++) Assert.Empty(Offer(t, 14_000 + t));
        var kept = Offer(664, 14_664);
        Assert.Equal(61, kept.Count);
        Assert.Equal(14_664, guard.Trusted!.Souls);
    }

    [Fact]
    public void A_misread_clock_does_not_end_the_hold()
    {
        // Dead at 12:52 and watching a teammate 5,000 ahead; one reading's clock is misread ten minutes ahead ("22:52").
        // The hold runs on the PC's clock, so that reading is held like the others instead of letting them all through.
        var clock = new StepClock();
        var guard = Known(new OcrJumpGuard(clock: clock));
        guard.OfferOcr(Ocr(772, 10_000));
        clock.Second();
        Assert.Empty(guard.OfferOcr(Ocr(773, 15_000)));
        clock.Second();
        Assert.Empty(guard.OfferOcr(Ocr(1372, 15_010)));
        Assert.Equal(2, guard.HeldCount);
        // You respawn: back on your line, and the held readings are dropped as someone else's.
        clock.Second();
        Assert.Single(guard.OfferOcr(Ocr(775, 10_060)));
        Assert.Equal(2, guard.Dropped);
    }

    [Fact]
    public void Readings_settled_by_the_broadcast_leave_the_later_ones_their_own_hold()
    {
        var clock = new StepClock();
        var guard = new OcrJumpGuard(maxHold: TimeSpan.FromSeconds(60), clock: clock);
        guard.OfferOcr(Ocr(600, Me(600)));
        guard.OfferFeed(Feed(598, Me(598)));
        // A real jump at 10:01 (an urn), then 40 s later more readings on the new line, all held.
        clock.Second();
        Assert.Empty(guard.OfferOcr(Ocr(601, Me(601) + 3_000)));
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Empty(guard.OfferOcr(Ocr(641, Me(641) + 3_000)));
        // The broadcast catches up to 10:02 only: it settles the first and keeps it (your souls did jump).
        guard.OfferFeed(Feed(600, Me(600)));
        Assert.Single(guard.OfferFeed(Feed(602, Me(602) + 3_000)));
        Assert.Equal(1, guard.HeldCount);
        // 30 s on, the first reading's hold would be over, but the one still held arrived only 30 s ago.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(guard.OfferOcr(Ocr(671, Me(671) + 12_000)));
        Assert.Equal(2, guard.HeldCount);
    }

    // Unspent souls and net worth: farming 20 a second; you hold 6,000 of items at 10:00.
    private static int Unspent(int seconds) => 2_000 + (seconds - 600) * 20;

    [Fact]
    public void Readings_become_net_worth_with_the_items_the_broadcast_says_you_hold()
    {
        var guard = new OcrJumpGuard(prices: () => Shop);
        guard.OfferFeed(Feed(560, Unspent(560), items: 6_000));
        var sent = guard.OfferOcr(Ocr(600, Unspent(600)));
        Assert.Equal(Unspent(600) + 6_000, Assert.Single(sent).Souls);
        Assert.Equal(Unspent(600), sent[0].Unspent);

        // The broadcast catches up to 10:00 and agrees.
        guard.OfferFeed(Feed(600, Unspent(600), items: 6_000));
        Assert.StartsWith("within 0 souls of the broadcast (median of 1 readings", guard.AgreementLine);
    }

    [Fact]
    public void A_purchase_drops_the_counter_but_not_the_net_worth()
    {
        var guard = new OcrJumpGuard(prices: () => Shop);
        guard.OfferFeed(Feed(560, Unspent(560), items: 6_000));
        var meter = new List<SoulSample>();
        for (int t = 600; t <= 619; t++) meter.AddRange(guard.OfferOcr(Ocr(t, Unspent(t))));
        // At 10:20 you buy a 1,600 item: the counter falls by its price and farming carries on from there.
        for (int t = 620; t <= 640; t++) meter.AddRange(guard.OfferOcr(Ocr(t, Unspent(t) - 1_600)));

        Assert.Equal(41, meter.Count); // the first reading after the purchase waits a second for the next to confirm it
        Assert.Equal(1, guard.Purchases);
        Assert.Equal(0, guard.Dropped);
        Assert.All(meter, s => Assert.Equal(Unspent((int)s.GameTime.TotalSeconds) + 6_000, s.Souls)); // net worth never fell
        Assert.Equal(Unspent(640) - 1_600, meter[^1].Unspent);

        // 35 s later the broadcast shows the item: 7,600 held from 10:20. Net worth goes on the same.
        guard.OfferFeed(Feed(625, Unspent(625) - 1_600, items: 7_600));
        Assert.Equal(Unspent(641) + 6_000, Assert.Single(guard.OfferOcr(Ocr(641, Unspent(641) - 1_600))).Souls);
        Assert.StartsWith("within 0 souls", guard.AgreementLine);
    }

    [Fact]
    public void A_drop_that_is_no_price_or_is_read_once_is_not_a_purchase()
    {
        var guard = Known(new OcrJumpGuard(prices: () => Shop));
        guard.OfferOcr(Ocr(600, 4_000));
        // A digit lost once ("1,600" read "0" would match a price, but only once).
        Assert.Empty(guard.OfferOcr(Ocr(601, 2_400)));
        Assert.Single(guard.OfferOcr(Ocr(602, 4_040)));
        // A poorer teammate's counter, 2,950 lower: no price or pair of prices is that, so it is held however long it lasts.
        for (int t = 603; t <= 610; t++) Assert.Empty(guard.OfferOcr(Ocr(t, 1_090)));
        Assert.Single(guard.OfferOcr(Ocr(611, 4_100))); // respawned: the held ones are dropped
        Assert.Equal(0, guard.Purchases);
        Assert.Equal(9, guard.Dropped);
    }

    [Fact]
    public void A_teammates_counter_that_looks_like_a_purchase_is_undone_when_yours_comes_back()
    {
        var guard = Known(new OcrJumpGuard(prices: () => Shop));
        guard.OfferOcr(Ocr(600, 4_000));
        // Dead: the camera shows a teammate exactly 3,200 poorer, which reads like buying a 3,200 item.
        var sent = new List<SoulSample>();
        for (int t = 601; t <= 612; t++) sent.AddRange(guard.OfferOcr(Ocr(t, 800)));
        Assert.Equal(1, guard.Purchases);
        Assert.All(sent, s => Assert.Equal(4_000, s.Souls)); // a purchase keeps net worth where it was
        // Your own counter again, still 4,000 (dead, so nothing earned): the "purchase" is undone.
        Assert.Equal(4_000, Assert.Single(guard.OfferOcr(Ocr(613, 4_000))).Souls);
        Assert.Equal(0, guard.Purchases);

        // A real purchase followed by a kill that earns more than it cost is not undone.
        var g2 = Known(new OcrJumpGuard(prices: () => Shop));
        g2.OfferOcr(Ocr(600, 1_000));
        g2.OfferOcr(Ocr(601, 200));
        g2.OfferOcr(Ocr(602, 210));
        Assert.Equal(1, g2.Purchases);
        g2.OfferOcr(Ocr(603, 1_300)); // +1,090 at once: held, a jump, but not back to the old line
        Assert.Equal(1, g2.Purchases);
    }

    [Fact]
    public void Held_readings_that_match_the_broadcasts_unspent_souls_are_kept_even_after_a_wrong_trusted_one()
    {
        // The first reading of the match was a misread (1,000 for 4,000); the next ones jump, and are yours.
        var guard = new OcrJumpGuard(prices: () => Shop);
        guard.OfferFeed(Feed(590, 3_800, items: 2_400));
        guard.OfferOcr(Ocr(600, 1_000));
        for (int t = 601; t <= 610; t++) Assert.Empty(guard.OfferOcr(Ocr(t, 4_000 + (t - 600) * 20)));
        // The broadcast moved by 200 where the readings moved by 3,000, but its unspent souls are the readings' own.
        var kept = new List<SoulSample>();
        for (int t = 595; t <= 612; t++) kept.AddRange(guard.OfferFeed(Feed(t, 4_000 + (t - 600) * 20, items: 2_400)));
        Assert.Equal(10, kept.Count);
        Assert.Equal(4_200 + 2_400, kept[^1].Souls);
    }

    [Fact]
    public void Started_mid_match_the_readings_wait_for_the_broadcasts_items()
    {
        var guard = new OcrJumpGuard(prices: () => Shop);
        Assert.Empty(guard.OfferOcr(Ocr(900, 3_000)));
        Assert.Empty(guard.OfferOcr(Ocr(901, 3_020)));
        guard.OfferFeed(Feed(866, 2_300, items: 9_600));
        Assert.Equal(3_040 + 9_600, Assert.Single(guard.OfferOcr(Ocr(902, 3_040))).Souls);
        // From the start of a match nothing is held yet, so no broadcast is needed.
        var early = new OcrJumpGuard();
        Assert.Equal(300, Assert.Single(early.OfferOcr(Ocr(20, 300))).Souls);
        // A broadcast without the items (no prices loaded) changes nothing.
        Assert.Empty(guard.OfferFeed(new SoulSample(TimeSpan.FromSeconds(870), 12_000, SampleSource.LiveFeed)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Readings_with_haunted_souls_tell_whether_the_broadcast_counts_them(bool broadcastCounts)
    {
        // You carry 1,200 haunted souls from 10:00: the HUD's count plus the haunted line is what OCR reads.
        var guard = Known(new OcrJumpGuard(prices: () => Shop));
        const int haunted = 1_200;
        for (int t = 600; t <= 630; t++)
            guard.OfferOcr(new SoulSample(TimeSpan.FromSeconds(t), Unspent(t) + haunted, SampleSource.Ocr, Haunted: haunted));
        Assert.Null(guard.HauntedVerdict);
        // The broadcast catches up, with or without them in its net worth.
        for (int t = 600; t <= 630; t++)
            guard.OfferFeed(Feed(t, Unspent(t) + (broadcastCounts ? haunted : 0)));
        Assert.Equal(broadcastCounts, guard.HauntedVerdict);
        Assert.Contains(broadcastCounts ? "so the broadcast counts them" : "so the broadcast leaves them out", guard.AgreementLine);
        Assert.Contains("no readings without haunted souls compared yet", guard.AgreementLine);
    }

    [Fact]
    public void Purchase_sizes_are_prices_pairs_and_upgrades()
    {
        Assert.Equal(new[] { 800, 1_600, 2_400, 3_200 }, OcrJumpGuard.PurchaseSizes(new[] { 800, 1_600, 800, 0 }));
        Assert.Empty(OcrJumpGuard.PurchaseSizes(null));
    }

    /// <summary>A PC clock the test moves by hand.</summary>
    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Second() => _now += TimeSpan.FromSeconds(1);
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task The_composite_source_sends_only_your_own_readings_on()
    {
        // OCR: you farm, die at 10:20 and watch a teammate 5,000 ahead for 20 s, then respawn. The broadcast runs alongside.
        var ocr = new ListSource(SampleSource.Ocr, Enumerable.Range(600, 61).Select(t =>
            Ocr(t, t is > 620 and <= 640 ? 15_000 + t : Me(Math.Min(t, 620) + Math.Max(0, t - 640)))).ToList());
        var feed = new ListSource(SampleSource.LiveFeed, new List<SoulSample> { Feed(0, 0) });
        ocr.Wait = TimeSpan.FromMilliseconds(100); // the broadcast's "no items" first
        var composite = new CompositeSoulSource(feed, ocr, () => SoulSourceMode.Ocr, () => true);

        var got = new List<SoulSample>();
        await foreach (var s in composite.ReadAsync(new MatchContext(1, 0, 7, DateTimeOffset.UtcNow), CancellationToken.None)) got.Add(s);

        Assert.Equal(41, got.Count);
        Assert.All(got, s => Assert.True(s.Souls < 12_000));
        Assert.DoesNotContain(got, s => s.GameTime > TimeSpan.FromSeconds(620) && s.GameTime <= TimeSpan.FromSeconds(640));
    }

    private sealed class ListSource(SampleSource kind, List<SoulSample> samples) : ISoulSampleSource
    {
        public TimeSpan Wait { get; set; }
        public SampleSource Kind => kind;
        public TimeSpan ExpectedLatency => TimeSpan.Zero;
        public bool IsHealthy => true;
        public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Wait > TimeSpan.Zero) await Task.Delay(Wait, ct);
            foreach (var s in samples) { await Task.Yield(); yield return s; }
        }
    }
}
