using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Detection;
using DeadlockScout.Core.Models;
using DeadlockScout.Steam.Logs;

namespace DeadlockScout.Tests.Steam;

public class ConsoleLogTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lobby_line_yields_ids()
    {
        var s = ConsoleLogTailer.Parse("10/26 20:00:46 [Client] Lobby 2998877 for Match 41223344 created", At);
        var lc = Assert.IsType<MatchSignal.LobbyCreated>(s);
        Assert.Equal(2998877, lc.LobbyId);
        Assert.Equal(41223344, lc.MatchId);
        Assert.Equal(ConsoleLogTailer.SourceName, lc.Source);
    }

    [Fact]
    public void Noise_lines_yield_nothing()
    {
        Assert.Null(ConsoleLogTailer.Parse("", At));
        Assert.Null(ConsoleLogTailer.Parse("[Net] CL: ping 31 ms", At));
        Assert.Null(ConsoleLogTailer.Parse("Lobby created without ids", At));
    }

    [Fact]
    public void Hero_account_and_end_lines_parse()
    {
        Assert.Equal("hero_haze", Assert.IsType<MatchSignal.HeroLocked>(ConsoleLogTailer.Parse("[Client] CCitadelPlayerController selected hero hero_haze", At)).HeroName);
        Assert.Equal(123456u, Assert.IsType<MatchSignal.AccountSeen>(ConsoleLogTailer.Parse("[Client] Connected to Steam, SteamID [U:1:123456]", At)).AccountId);
        Assert.Equal(41223344, Assert.IsType<MatchSignal.Ended>(ConsoleLogTailer.Parse("[Client] Match 41223344 ended", At)).MatchId);
        Assert.Equal(0, Assert.IsType<MatchSignal.Ended>(ConsoleLogTailer.Parse("[Client] Disconnect from server", At)).MatchId);
    }

    [Fact]
    public void Replaying_the_fixture_through_the_detector_gives_one_start_and_one_end()
    {
        var clock = new TestClock(At);
        var det = new CompositeMatchDetector(Array.Empty<IMatchSignalSource>(), clock, () => 123456);
        var starts = new List<MatchContext>(); var ends = new List<long>(); var heroes = new List<string>();
        det.MatchStarted += starts.Add; det.MatchEnded += ends.Add; det.HeroLocked += heroes.Add;
        MatchSignal.Started? pending = null;
        foreach (var line in File.ReadLines(Fixtures.Path("console", "match-start-end.log")))
        {
            var s = ConsoleLogTailer.Parse(line, clock.UtcNow);
            if (s is not null) det.Handle(s, ref pending);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Single(starts);
        Assert.Equal(41223344, starts[0].MatchId);
        Assert.Equal(new long[] { 41223344 }, ends);
        Assert.Equal("hero_haze", heroes[0]);
    }

    [Fact]
    public async Task Tailer_follows_appends_and_truncation()
    {
        var dir = Directory.CreateTempSubdirectory("dls-tail");
        var path = Path.Combine(dir.FullName, "console.log");
        await File.WriteAllTextAsync(path, "old line\n");
        var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(20));
        var got = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = Task.Run(async () =>
        {
            await foreach (var l in tailer.ReadLinesAsync(cts.Token)) { got.Add(l); if (got.Count == 3) break; }
        });
        await Task.Delay(100);
        await File.AppendAllTextAsync(path, "Lobby 1 for Match 2 created\r\npartial");
        await Task.Delay(100);
        await File.AppendAllTextAsync(path, " line\n");
        await Task.Delay(100);
        await File.WriteAllTextAsync(path, "after truncate\n"); // shorter than before -> restart from top
        await reader;
        Assert.Equal(new[] { "Lobby 1 for Match 2 created", "partial line", "after truncate" }, got);
        dir.Delete(true);
    }

    [Fact]
    public async Task Tailer_waits_for_a_file_that_does_not_exist_yet()
    {
        var dir = Directory.CreateTempSubdirectory("dls-tail2");
        var path = Path.Combine(dir.FullName, "console.log");
        var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(20), fromStart: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = Task.Run(async () => { await foreach (var l in tailer.ReadLinesAsync(cts.Token)) return l; return null; });
        await Task.Delay(100);
        await File.WriteAllTextAsync(path, "hello\n");
        Assert.Equal("hello", await first);
        dir.Delete(true);
    }
}
