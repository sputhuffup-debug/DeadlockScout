using DeadlockScout.Steam.Logging;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.Tests.Steam;

public class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dls-log-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 31, 7, 50, 12, 345, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void A_line_has_the_time_level_short_category_and_message_and_the_exception_indented()
    {
        var line = FileLoggerProvider.Format(_now, LogLevel.Warning, "DeadlockScout.App.Services.MatchSession", "Feed stopped",
            new InvalidOperationException("boom"));
        var lines = line.Split('\n');
        Assert.Equal("2026-10-31 07:50:12.345 WARN  MatchSession: Feed stopped", lines[0]);
        Assert.Equal("    System.InvalidOperationException: boom", lines[1]);
    }

    [Fact]
    public void Lines_go_to_one_file_a_day_and_only_from_the_minimum_level()
    {
        using (var provider = new FileLoggerProvider(_folder, LogLevel.Information, () => _now))
        {
            var log = provider.CreateLogger("DeadlockScout.Api.DeadlockApiClient");
            log.LogDebug("not written");
            log.LogInformation("first {N}", 1);
            _now = _now.AddDays(1);
            log.LogInformation("next day");
        }
        var day1 = File.ReadAllLines(Path.Combine(_folder, "scout-2026-10-31.log"));
        Assert.Equal(new[] { "2026-10-31 07:50:12.345 INFO  DeadlockApiClient: first 1" }, day1);
        Assert.Single(File.ReadAllLines(Path.Combine(_folder, "scout-2026-11-01.log")));
    }

    [Fact]
    public void Only_the_newest_week_of_files_is_kept()
    {
        Directory.CreateDirectory(_folder);
        for (int d = 1; d <= 9; d++) File.WriteAllText(Path.Combine(_folder, $"scout-2026-10-{d:00}.log"), "old");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a log");
        using (var provider = new FileLoggerProvider(_folder, now: () => _now))
            provider.CreateLogger("X").LogInformation("today");
        var logs = Directory.GetFiles(_folder, "scout-*.log").Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal(FileLoggerProvider.KeepFiles, logs.Count);
        Assert.Equal("scout-2026-10-31.log", logs[^1]);
        Assert.Equal("scout-2026-10-04.log", logs[0]);
        Assert.True(File.Exists(Path.Combine(_folder, "notes.txt")));
    }

    [Fact]
    public void A_full_day_stops_with_one_line_saying_so()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "scout-2026-10-31.log");
        File.WriteAllBytes(path, new byte[FileLoggerProvider.MaxBytes]);
        using (var provider = new FileLoggerProvider(_folder, now: () => _now))
        {
            var log = provider.CreateLogger("X");
            log.LogInformation("one");
            log.LogInformation("two");
        }
        var tail = File.ReadAllText(path)[(int)FileLoggerProvider.MaxBytes..];
        Assert.Contains("nothing more today", tail);
        Assert.DoesNotContain("one", tail);
        Assert.DoesNotContain("two", tail);
    }
}
