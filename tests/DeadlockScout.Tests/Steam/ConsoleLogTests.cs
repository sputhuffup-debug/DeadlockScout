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
        var s = ConsoleLogTailer.Parse("10/27 17:14:35 Lobby 2998877 for Match 41223344 created", At);
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
    public void Server_ticket_line_starts_the_match()
    {
        var s = ConsoleLogTailer.Parse("10/27 17:14:35 [SteamNetSockets] Received Steam datagram ticket for server steamid:90000000000000002 vport -1. match_id=41223344", At);
        Assert.Equal(41223344, Assert.IsType<MatchSignal.Started>(s).MatchId);
    }

    [Fact]
    public void Hero_spawn_line_names_the_hero()
    {
        var s = ConsoleLogTailer.Parse("10/27 16:54:21 Attempting to spawn hero hero_frank (66). GameState is 7", At);
        Assert.Equal("hero_frank", Assert.IsType<MatchSignal.HeroLocked>(s).HeroName);
    }

    [Theory]
    [InlineData("10/27 16:55:14 [Client] Disconnected from server: NETWORK_DISCONNECT_DISCONNECT_BY_USER")]
    [InlineData("10/27 16:55:14 [Client] Disconnecting from server: NETWORK_DISCONNECT_DISCONNECT_BY_USER")]
    [InlineData("10/27 16:55:14 Loading map \"dl_hideout\"")]
    [InlineData("10/27 17:55:04 OnGameStateChanged: PostGame (8)")]
    [InlineData("10/27 17:55:14 OnGameStateChanged: End (11)")]
    public void Leaving_or_finishing_ends_the_match(string line)
        => Assert.Equal(0, Assert.IsType<MatchSignal.Ended>(ConsoleLogTailer.Parse(line, At)).MatchId);

    [Theory]
    [InlineData("10/27 17:14:36 [Client] Disconnecting from server: NETWORK_DISCONNECT_LOOPDEACTIVATE")]
    [InlineData("10/27 17:14:36 [Server] SV:  Server shutting down: NETWORK_DISCONNECT_LOOPDEACTIVATE (55)")]
    [InlineData("10/27 16:55:14 [Server] Disconnect client 'Player' from server: NETWORK_DISCONNECT_SHUTDOWN (Server shutdown)")]
    [InlineData("10/27 16:54:18 [SteamNetSockets] [#1 UDP steamid:90000000000000001@127.0.0.1:27015 'server'] closed by app (2055) NETWORK_DISCONNECT_LOOPDEACTIVATE")]
    [InlineData("10/27 17:15:50 OnGameStateChanged: GameInProgress (7)")]
    [InlineData("10/27 16:53:32 ChangeGameState: GameInProgress (7)")]
    [InlineData("10/27 16:53:32 [Client] Map: \"dl_hideout\"")]
    public void Loop_switches_and_server_lines_are_not_signals(string line)
        => Assert.Null(ConsoleLogTailer.Parse(line, At));

    private static (List<MatchContext> starts, List<long> ends, List<string> heroes, CompositeMatchDetector det) Replay(IEnumerable<string> lines)
    {
        var clock = new TestClock(At);
        var det = new CompositeMatchDetector(Array.Empty<IMatchSignalSource>(), clock, () => 123456);
        var starts = new List<MatchContext>(); var ends = new List<long>(); var heroes = new List<string>();
        det.MatchStarted += starts.Add; det.MatchEnded += ends.Add; det.HeroLocked += heroes.Add;
        MatchSignal.Started? pending = null;
        foreach (var line in lines)
        {
            var s = ConsoleLogTailer.Parse(line, clock.UtcNow);
            if (s is not null) det.Handle(s, ref pending);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        return (starts, ends, heroes, det);
    }

    [Fact]
    public void Replaying_a_real_match_gives_one_start_and_one_end()
    {
        // Hideout, a bot match left early, matchmaking, then a matchmade match played to the end screen and back to
        // the hideout. The loop-switch disconnect right after the lobby line must not end it.
        var lines = File.ReadLines(Fixtures.Path("console", "real-match.log")).ToList();
        var (starts, ends, heroes, det) = Replay(lines);
        var start = Assert.Single(starts);
        Assert.Equal(41223344, start.MatchId);
        Assert.Equal(new long[] { 41223344 }, ends);
        Assert.Null(det.Current);
        Assert.Equal(new[] { "hero_frank" }, heroes); // the bot match logs the spawn; the matchmade match does not

        // Live until the end screen opens.
        var endScreen = lines.FindIndex(l => l.Contains("PostGame (8)"));
        var (_, endsBefore, _, live) = Replay(lines.Take(endScreen));
        Assert.Empty(endsBefore);
        Assert.Equal(41223344, live.Current?.MatchId);
        Assert.Equal(2998877000000001, live.Current?.LobbyId);
    }

    [Fact]
    public void Lobby_destroyed_line_ends_that_match()
    {
        var s = ConsoleLogTailer.Parse("10/27 17:55:05 Lobby 2998877000000001 for Match 41223344 destroyed", At);
        Assert.Equal(41223344, Assert.IsType<MatchSignal.Ended>(s).MatchId);
    }

    [Fact]
    public void Leaving_a_match_early_ends_it_once()
    {
        var lines = File.ReadLines(Fixtures.Path("console", "real-match.log"))
            .TakeWhile(l => !l.Contains("PostGame (8)"))
            .Concat(new[]
            {
                // The leave sequence as the bot match logged it.
                "10/27 17:39:41 [Client] Disconnected from server: NETWORK_DISCONNECT_DISCONNECT_BY_USER",
                "10/27 17:39:41 [Client] Disconnecting from server: NETWORK_DISCONNECT_DISCONNECT_BY_USER",
                "10/27 17:39:41 Loading map \"dl_hideout\"",
                "10/27 17:39:41 [Client] Disconnecting from server: NETWORK_DISCONNECT_LOOPDEACTIVATE",
            });
        var (starts, ends, _, det) = Replay(lines);
        Assert.Single(starts);
        Assert.Equal(new long[] { 41223344 }, ends);
        Assert.Null(det.Current);
    }

    // ---- startup scan: an app started mid-match picks the match up from what console.log already says ----

    private static readonly DateTime FixtureEnd = new(2026, 10, 27, 17, 55, 26); // last line of real-match.log

    private static List<string> FixtureUntil(string marker)
        => File.ReadLines(Fixtures.Path("console", "real-match.log")).TakeWhile(l => !l.Contains(marker)).ToList();

    [Fact]
    public void Scan_resumes_a_match_still_in_progress()
    {
        var lines = FixtureUntil("PostGame (8)"); // ends at 17:54:54, mid-match
        var resume = ConsoleLogTailer.ResumeSignals(lines, At, new DateTime(2026, 10, 27, 17, 55, 30));
        Assert.Contains(resume, s => s is MatchSignal.LobbyCreated { MatchId: 41223344, LobbyId: 2998877000000001 });
        Assert.All(resume, s => Assert.True(s is MatchSignal.LobbyCreated or MatchSignal.Started { MatchId: 41223344 }));

        var clock = new TestClock(At);
        var det = new CompositeMatchDetector(Array.Empty<IMatchSignalSource>(), clock, () => 123456);
        var starts = new List<MatchContext>();
        det.MatchStarted += starts.Add;
        MatchSignal.Started? pending = null;
        foreach (var s in resume) det.Handle(s, ref pending);
        Assert.Equal(41223344, Assert.Single(starts).MatchId);
    }

    [Fact]
    public void Scan_does_not_resume_a_finished_match_a_bot_match_or_a_stale_log()
    {
        var now = FixtureEnd.AddSeconds(30);
        // Played to the end screen and back to the hideout.
        Assert.Empty(ConsoleLogTailer.ResumeSignals(File.ReadLines(Fixtures.Path("console", "real-match.log")), At, now));
        // Only the hideout and a bot match so far: no match id, nothing to resume.
        Assert.Empty(ConsoleLogTailer.ResumeSignals(FixtureUntil("Received Steam datagram ticket"), At, now));
        // Mid-match, but the log has been quiet for ten minutes: the game closed or crashed.
        Assert.Empty(ConsoleLogTailer.ResumeSignals(FixtureUntil("PostGame (8)"), At, new DateTime(2026, 10, 27, 18, 5, 0)));
    }

    [Fact]
    public void Line_times_take_the_year_that_keeps_them_in_the_past()
    {
        Assert.Equal(new DateTime(2026, 10, 27, 18, 32, 28), ConsoleLogPatterns.LineTimeOf("10/27 18:32:28 Lobby 1 for Match 2 created", new DateTime(2026, 10, 27, 18, 33, 0)));
        Assert.Equal(new DateTime(2025, 12, 31, 23, 59, 0), ConsoleLogPatterns.LineTimeOf("12/31 23:59:00 x", new DateTime(2026, 1, 1, 0, 1, 0)));
        Assert.Equal(new DateTime(2026, 10, 27, 16, 53, 19), ConsoleLogPatterns.LineTimeOf("\uFEFF10/27 16:53:19 [RenderSystem] first line", new DateTime(2026, 10, 27, 17, 0, 0)));
        Assert.Null(ConsoleLogPatterns.LineTimeOf("[Client] no time", new DateTime(2026, 10, 27, 17, 0, 0)));
        Assert.Null(ConsoleLogPatterns.LineTimeOf("10/27 25:61:00 not a time", new DateTime(2026, 10, 27, 17, 0, 0))); // never throws
        Assert.Null(ConsoleLogPatterns.LineTimeOf("13/40 10:00:00 not a date", new DateTime(2026, 10, 27, 17, 0, 0)));
    }

    [Fact]
    public async Task Tailer_started_mid_match_resumes_it_then_follows_the_end()
    {
        var dir = Directory.CreateTempSubdirectory("dls-scan");
        var path = Path.Combine(dir.FullName, "console.log");
        var now = DateTimeOffset.Now;
        string T(int secondsAgo) => now.LocalDateTime.AddSeconds(-secondsAgo).ToString("MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(path,
            $"{T(300)} Loading map \"dl_hideout\"\n" +
            $"{T(120)} [SteamNetSockets] Received Steam datagram ticket for server steamid:90000000000000002 vport -1. match_id=41223344\n" +
            $"{T(120)} Lobby 2998877000000001 for Match 41223344 created\n" +
            $"{T(119)} [Client] Disconnecting from server: NETWORK_DISCONNECT_LOOPDEACTIVATE\n" +
            $"{T(60)} OnGameStateChanged: GameInProgress (7)\n" +
            $"{T(5)} Bullet Hit Mismatch: client hit entity#0/worldent, server hit #2759/npc_trooper\n");
        var tailer = new ConsoleLogTailer(() => path, new TestClock(now.ToUniversalTime()), poll: TimeSpan.FromMilliseconds(20));
        var got = new List<MatchSignal>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = Task.Run(async () =>
        {
            await foreach (var s in tailer.ReadAsync(cts.Token)) { got.Add(s); if (s is MatchSignal.Ended) break; }
        });
        await Task.Delay(200);
        Assert.Contains(got, s => s is MatchSignal.LobbyCreated { MatchId: 41223344 });
        await File.AppendAllTextAsync(path, $"{T(0)} OnGameStateChanged: PostGame (8)\n");
        await reader;
        Assert.IsType<MatchSignal.Ended>(got[^1]);
        Assert.Single(got, s => s is MatchSignal.LobbyCreated); // the scanned lines are not read again by the tailer
        dir.Delete(true);
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
