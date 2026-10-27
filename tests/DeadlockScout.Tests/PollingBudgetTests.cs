using DeadlockScout.Api.Live;
using DeadlockScout.Core.Models;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using DeadlockScout.Steam.Logs;

namespace DeadlockScout.Tests;

/// <summary>Nothing in the app polls faster than once a second; the overlay and OCR are real time at 1 s.</summary>
public class PollingBudgetTests
{
    [Fact]
    public void Default_poll_intervals_are_at_least_one_second()
    {
        Assert.True(OcrSoulSource.DefaultInterval >= TimeSpan.FromSeconds(1));
        Assert.True(LogTailer.DefaultPoll >= TimeSpan.FromSeconds(1));
        Assert.True(DirectBroadcastStream.FragmentRetryInterval >= TimeSpan.FromSeconds(3)); // the relay publishes every 3 s
    }

    private sealed class Capture(bool available) : IRegionCapture
    {
        public int Calls;
        public bool IsAvailable => available;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct) { Calls++; return Task.FromResult<CapturedFrame?>(null); }
    }

    private sealed class Engine : IOcrEngine
    {
        public string Name => "fake";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Regions : IHudRegionMap
    {
        public HudRegion? Souls => new(0.4, 0.9, 0.1, 0.04);
        public HudRegion? Timer => new(0.48, 0, 0.04, 0.02);
        public IReadOnlyList<HudRegion?> EnemySouls => Array.Empty<HudRegion?>();
    }

    [Fact]
    public async Task Ocr_captures_nothing_while_the_game_is_not_on_screen()
    {
        var capture = new Capture(available: false); // the app's capture reports unavailable while the game is not in front
        var src = new OcrSoulSource(capture, new Regions(), new Engine(), DeadlockScout.Core.Abstractions.SystemClock.Instance, interval: TimeSpan.FromMilliseconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        try { await foreach (var _ in src.ReadAsync(new MatchContext(1, 1, 1, DateTimeOffset.UtcNow), cts.Token)) { } } catch (OperationCanceledException) { }
        Assert.Equal(0, capture.Calls);
        Assert.Equal(0, src.SuccessRate); // and those passes did not count as failures either
    }

    /// <summary>Capture that becomes available after a few passes, like alt-tabbing back into the game.</summary>
    private sealed class LateCapture : IRegionCapture
    {
        public int Checks, Calls;
        public bool IsAvailable => ++Checks > 3;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<CapturedFrame?>(new CapturedFrame(1, 1, 4, new byte[4], DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task Ocr_starts_reading_when_the_game_comes_to_the_front_mid_match()
    {
        var capture = new LateCapture();
        var src = new OcrSoulSource(capture, new Regions(), new Engine(), DeadlockScout.Core.Abstractions.SystemClock.Instance, interval: TimeSpan.FromMilliseconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try { await foreach (var _ in src.ReadAsync(new MatchContext(1, 1, 1, DateTimeOffset.UtcNow), cts.Token)) { } } catch (OperationCanceledException) { }
        Assert.True(capture.Calls > 0); // it kept checking instead of giving up at the start
    }
}
