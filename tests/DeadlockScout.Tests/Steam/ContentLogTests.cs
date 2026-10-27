using DeadlockScout.Core.Abstractions;
using DeadlockScout.Steam.Logs;

namespace DeadlockScout.Tests.Steam;

public class ContentLogTests
{
    [Fact]
    public void Updates_toggled_and_app_quit_bracket_a_match()
    {
        var at = DateTimeOffset.UtcNow;
        bool running = false;
        var signals = File.ReadLines(Fixtures.Path("steam", "content_log.txt"))
            .Select(l => SteamContentLogWatcher.Parse(l, at, ref running)).Where(s => s is not null).ToList();
        Assert.Collection(signals,
            s => Assert.IsType<MatchSignal.Started>(s),
            s => Assert.IsType<MatchSignal.Ended>(s),
            s => Assert.IsType<MatchSignal.Ended>(s));
        Assert.All(signals, s => Assert.Equal(SteamContentLogWatcher.SourceName, s!.Source));
    }

    [Fact]
    public void Other_apps_are_ignored()
    {
        bool running = false;
        Assert.Null(SteamContentLogWatcher.Parse("[2026-10-26 20:00:50] AppID 730 scheduler: updates disabled", DateTimeOffset.UtcNow, ref running));
    }
}
