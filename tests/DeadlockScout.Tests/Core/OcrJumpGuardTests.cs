using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Sources;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic matches: your own souls, and the teammates' counters the HUD shows while you are dead.</summary>
public class OcrJumpGuardTests
{
    private static SoulSample Ocr(int seconds, int souls) => new(TimeSpan.FromSeconds(seconds), souls, SampleSource.Ocr);
    private static SoulSample Feed(int seconds, int souls) => new(TimeSpan.FromSeconds(seconds), souls, SampleSource.LiveFeed);

    /// <summary>Farming at 20 souls a second from 10,000 at 10:00.</summary>
    private static int Me(int seconds) => 10_000 + (seconds - 600) * 20;

    [Fact]
    public void Cycling_between_teammates_while_dead_never_reaches_the_meter_and_respawn_carries_on()
    {
        var guard = new OcrJumpGuard();
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
        Assert.Equal(urn.Take(19), kept); // everything up to 10:39 (the broadcast's 10:40 less a second)
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
        var guard = new OcrJumpGuard(maxHold: TimeSpan.FromSeconds(60));
        guard.OfferOcr(Ocr(600, 10_000));
        // A one-off misread (a digit lost) is held, then dropped when the next reading is back on your line.
        Assert.Empty(guard.OfferOcr(Ocr(601, 1_002)));
        Assert.Single(guard.OfferOcr(Ocr(602, 10_040)));
        Assert.Equal(1, guard.Dropped);
        // Small steps go straight through, a few hundred souls a second included (a kill).
        Assert.Single(guard.OfferOcr(Ocr(603, 10_520)));

        // A jump with no broadcast to check it against: held for 60 s, then kept with everything after it.
        for (int t = 604; t < 664; t++) Assert.Empty(guard.OfferOcr(Ocr(t, 14_000 + t)));
        var kept = guard.OfferOcr(Ocr(664, 14_664));
        Assert.Equal(61, kept.Count);
        Assert.Equal(14_664, guard.Trusted!.Souls);
    }

    [Fact]
    public async Task The_composite_source_sends_only_your_own_readings_on()
    {
        // OCR: you farm, die at 10:20 and watch a teammate 5,000 ahead for 20 s, then respawn. The broadcast runs alongside.
        var ocr = new ListSource(SampleSource.Ocr, Enumerable.Range(600, 61).Select(t =>
            Ocr(t, t is > 620 and <= 640 ? 15_000 + t : Me(Math.Min(t, 620) + Math.Max(0, t - 640)))).ToList());
        var feed = new ListSource(SampleSource.LiveFeed, new List<SoulSample>());
        var composite = new CompositeSoulSource(feed, ocr, () => SoulSourceMode.Ocr, () => true);

        var got = new List<SoulSample>();
        await foreach (var s in composite.ReadAsync(new MatchContext(1, 0, 7, DateTimeOffset.UtcNow), CancellationToken.None)) got.Add(s);

        Assert.Equal(41, got.Count);
        Assert.All(got, s => Assert.True(s.Souls < 12_000));
        Assert.DoesNotContain(got, s => s.GameTime > TimeSpan.FromSeconds(620) && s.GameTime <= TimeSpan.FromSeconds(640));
    }

    private sealed class ListSource(SampleSource kind, List<SoulSample> samples) : ISoulSampleSource
    {
        public SampleSource Kind => kind;
        public TimeSpan ExpectedLatency => TimeSpan.Zero;
        public bool IsHealthy => true;
        public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var s in samples) { await Task.Yield(); yield return s; }
        }
    }
}
