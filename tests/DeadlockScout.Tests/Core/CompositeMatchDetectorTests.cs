using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Detection;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class CompositeMatchDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);

    private static (CompositeMatchDetector det, List<MatchContext> starts, List<long> ends, List<string> heroes, TestClock clock) Make()
    {
        var clock = new TestClock(T0);
        var det = new CompositeMatchDetector(Array.Empty<IMatchSignalSource>(), clock, () => 123456, TimeSpan.FromSeconds(20));
        var starts = new List<MatchContext>(); var ends = new List<long>(); var heroes = new List<string>();
        det.MatchStarted += starts.Add; det.MatchEnded += ends.Add; det.HeroLocked += heroes.Add;
        return (det, starts, ends, heroes, clock);
    }

    [Fact]
    public void Lobby_line_starts_once_even_when_logged_twice()
    {
        var (det, starts, ends, _, clock) = Make();
        MatchSignal.Started? pending = null;
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 2998877, 41223344), ref pending);
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 2998877, 41223344), ref pending);
        Assert.Single(starts);
        Assert.Equal(41223344, starts[0].MatchId);
        Assert.Equal(2998877, starts[0].LobbyId);
        Assert.Equal(123456u, starts[0].MyAccountId);
        det.Handle(new MatchSignal.Ended(clock.UtcNow, "console.log", 41223344), ref pending);
        det.Handle(new MatchSignal.Ended(clock.UtcNow, "content_log", 0), ref pending);
        Assert.Equal(new long[] { 41223344 }, ends);
        Assert.Null(det.Current);
    }

    [Fact]
    public void Content_log_start_is_ignored_when_console_spoke_recently()
    {
        var (det, starts, _, _, clock) = Make();
        MatchSignal.Started? pending = null;
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 1, 2), ref pending);
        det.Handle(new MatchSignal.Started(clock.UtcNow, "content_log", 0), ref pending);
        Assert.Null(pending);
        Assert.Single(starts);
    }

    [Fact]
    public void Content_log_start_becomes_pending_then_upgrades_with_the_console_id()
    {
        var (det, starts, _, _, clock) = Make();
        MatchSignal.Started? pending = null;
        clock.Advance(TimeSpan.FromHours(1));
        det.Handle(new MatchSignal.Started(clock.UtcNow, "content_log", 0), ref pending);
        Assert.NotNull(pending);
        Assert.Empty(starts);
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 7, 99), ref pending);
        Assert.Null(pending);
        Assert.Single(starts);
        Assert.Equal(99, starts[0].MatchId);
    }

    [Fact]
    public void A_new_lobby_without_an_end_line_ends_the_previous_match()
    {
        var (det, starts, ends, _, clock) = Make();
        MatchSignal.Started? pending = null;
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 1, 100), ref pending);
        det.Handle(new MatchSignal.LobbyCreated(clock.UtcNow, "console.log", 2, 200), ref pending);
        Assert.Equal(new long[] { 100 }, ends);
        Assert.Equal(new long[] { 100, 200 }, starts.Select(s => s.MatchId));
    }

    [Fact]
    public void Hero_lock_passes_through()
    {
        var (det, _, _, heroes, clock) = Make();
        MatchSignal.Started? pending = null;
        det.Handle(new MatchSignal.HeroLocked(clock.UtcNow, "console.log", "hero_haze"), ref pending);
        Assert.Equal(new[] { "hero_haze" }, heroes);
    }

    [Fact]
    public async Task Fallback_fires_without_an_id_after_the_grace_period()
    {
        var clock = new TestClock(T0.AddHours(2));
        var source = new ScriptedSource(new MatchSignal.Started(clock.UtcNow, "content_log", 0));
        var det = new CompositeMatchDetector(new[] { source }, clock, () => 1, fallbackGrace: TimeSpan.FromMilliseconds(100));
        var started = new TaskCompletionSource<MatchContext>();
        det.MatchStarted += c => started.TrySetResult(c);
        await det.StartAsync(CancellationToken.None);
        var ctx = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ctx.HasMatchId);
        await det.StopAsync();
    }

    private sealed class ScriptedSource(params MatchSignal[] signals) : IMatchSignalSource
    {
        public string Name => "scripted";
        public async IAsyncEnumerable<MatchSignal> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var s in signals) yield return s;
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
