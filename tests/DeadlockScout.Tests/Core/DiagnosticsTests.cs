using System.Net;
using DeadlockScout.Api.Http;
using DeadlockScout.Core.Diagnostics;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic ids and names only.</summary>
public class DiagnosticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Api_attempts_are_counted_per_family_and_only_real_failures_are_listed()
    {
        var log = new DiagnosticsLog(T0);
        log.ApiAttempt("Metadata", "/v1/matches/5/metadata", 200, null, TimeSpan.FromMilliseconds(100), T0);
        log.ApiAttempt("Metadata", "/v1/matches/5/metadata", 404, null, TimeSpan.FromMilliseconds(300), T0);
        log.ApiAttempt("Default", "/v1/players/4242/mate-stats?same_party=true", 429, null, TimeSpan.Zero, T0);
        log.ApiAttempt("Default", "/v1/players/4242/mate-stats", null, "timed out", TimeSpan.FromSeconds(30), T0.AddSeconds(1));
        log.RateLimitWait("Default", TimeSpan.FromSeconds(2));
        log.RateLimitWait("Default", TimeSpan.FromSeconds(5));

        var s = log.Snapshot();
        var meta = s.Api.Single(f => f.Family == "Metadata");
        Assert.Equal((2, 0, 0, 1), (meta.Attempts, meta.Errors, meta.TooMany, meta.NotFound));
        Assert.Equal(TimeSpan.FromMilliseconds(200), meta.AverageTime);
        var def = s.Api.Single(f => f.Family == "Default");
        Assert.Equal((2, 1, 1), (def.Attempts, def.Errors, def.TooMany));
        Assert.Equal((2, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(5)), (def.Waits, def.Waited, def.LongestWait));
        // Newest first; the 404 is not listed; player ids and queries never reach the list.
        Assert.Equal(new[] { "timed out", null }, s.Errors.Select(e => e.Error));
        Assert.All(s.Errors, e => Assert.Equal("/v1/players/<account>/mate-stats", e.Path));
    }

    [Fact]
    public void The_error_list_is_capped_and_a_late_match_id_keeps_what_was_learned()
    {
        var log = new DiagnosticsLog(T0);
        for (int i = 0; i < 30; i++) log.ApiAttempt("Analytics", "/v1/analytics/hero-stats", 500, null, TimeSpan.Zero, T0.AddSeconds(i));
        Assert.Equal(DiagnosticsLog.MaxErrors, log.Snapshot().Errors.Count);

        log.MatchStarted(77, false, T0);
        log.Feed("Direct broadcast");
        log.RosterReceived(T0.AddSeconds(64));
        log.MatchStarted(77, false, T0.AddSeconds(70)); // the detector firing again for the same match
        log.Feed("Hosted live query");
        var m = log.Snapshot().Match!;
        Assert.Equal(TimeSpan.FromSeconds(64), m.RosterAfter);
        Assert.Equal("Hosted live query", m.Feed);
        Assert.Contains("Feed switched from Direct broadcast to Hosted live query", m.Notes);

        log.MatchEnded(T0.AddMinutes(36));
        log.MatchStarted(78, true, T0.AddMinutes(40));
        Assert.Equal((78L, true, (string?)null), (log.Snapshot().Match!.MatchId, log.Snapshot().Match!.Spectating, log.Snapshot().Match!.Feed));
    }

    [Fact]
    public void The_report_names_the_match_feed_and_api_and_scrubs_ids_and_names()
    {
        var log = new DiagnosticsLog(T0);
        log.MatchStarted(123456789, false, T0);
        log.Feed("Direct broadcast");
        log.BroadcastDelay(TimeSpan.FromSeconds(34.1), "against the game log's start line");
        log.MatchEnded(T0.AddMinutes(35).AddSeconds(51));
        log.ApiAttempt("LiveUrl", "/v1/matches/123456789/live/url", 200, null, TimeSpan.FromMilliseconds(900), T0);
        var text = DiagnosticsReport.Format(log.Snapshot(),
            new LiveDiagnostics("1.0.0", "Direct", true, "HUD clock (OCR)", "OCR", 0.97, 3, false), T0.AddHours(1));
        Assert.Contains("match 123456789", text);
        Assert.Contains("ended after 35:51", text);
        Assert.Contains("feed: Direct broadcast", text);
        Assert.Contains("broadcast delay: 34.1 s", text);
        Assert.Contains("LiveUrl: 1, 0 errors", text);
        Assert.Contains("3 OCR readings dropped", text);

        var setup = text + "Account: 4242\nSteamID 76561197960269970\nSettings: C:\\Users\\SomeOne\\AppData\\x.json\nPlayer: Vexa vexa 14242";
        var scrubbed = DiagnosticsReport.Scrub(setup, new[] { "4242", "76561197960269970", "someone", "Vexa", null, " " });
        Assert.DoesNotContain("4242\n", scrubbed);
        Assert.DoesNotContain("76561197960269970", scrubbed);
        Assert.DoesNotContain("SomeOne", scrubbed);
        Assert.DoesNotContain("Vexa", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14242", scrubbed); // a longer number that only contains an id survives
        Assert.Contains("match 123456789", scrubbed);
    }

    [Fact]
    public async Task The_handler_records_each_attempt_with_its_family_and_status()
    {
        var log = new DiagnosticsLog(T0);
        using var client = new HttpClient(new DiagnosticsHandler(log) { InnerHandler = new Fixed(HttpStatusCode.ServiceUnavailable) });
        using var r = await client.GetAsync("https://api.deadlock-api.com/v1/matches/9/metadata?include_info=true");
        var f = log.Snapshot().Api.Single();
        Assert.Equal(("Metadata", 1, 1), (f.Family, f.Attempts, f.Errors));
        Assert.Equal(("/v1/matches/9/metadata", 503), (log.Snapshot().Errors[0].Path, log.Snapshot().Errors[0].Status));
    }

    private sealed class Fixed(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(code));
    }

    [Fact]
    public void The_profile_folder_is_hidden_even_when_it_is_not_the_user_name()
    {
        // The profile folder keeps the name the account had when it was made; the user name can be something else.
        const string profile = @"C:\Users\Some Body's Name";
        var text = "Settings: C:\\Users\\some body's name\\AppData\\Local\\DeadlockScout\\settings.json\nSteam: c:\\games\\steam";
        var hidden = DiagnosticsReport.HideProfile(text, profile);
        Assert.Contains(@"Settings: %USERPROFILE%\AppData\Local\DeadlockScout\settings.json", hidden);
        Assert.DoesNotContain("body", hidden, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"c:\games\steam", hidden); // paths outside the profile stay
        Assert.Equal(text, DiagnosticsReport.HideProfile(text, null));
        Assert.Equal(text, DiagnosticsReport.HideProfile(text, @"C:\"));
        // The folder's own name is scrubbed elsewhere too (a settings path pointed outside the profile).
        Assert.DoesNotContain("Some Body's Name", DiagnosticsReport.Scrub(@"D:\Some Body's Name\x.json", new[] { "Some Body's Name" }));
    }

    [Fact]
    public void The_ocr_read_rate_shows_only_during_a_match()
    {
        var log = new DiagnosticsLog(T0);
        string Report(bool inMatch) => DiagnosticsReport.Format(log.Snapshot(),
            new LiveDiagnostics("1.0.0", "Direct", true, null, null, 0.0, 0, inMatch), T0);
        Assert.DoesNotContain("OCR reads", Report(false));
        Assert.Contains("OCR reads 0", Report(true)); // "0%" or "0 %", by the culture
    }

    private static ProcessSample Sample(int seconds, double cpuSeconds, int workingMb, int privateMb = 100, int managedMb = 0)
        => new(T0.AddSeconds(seconds), TimeSpan.FromSeconds(cpuSeconds), workingMb * 1024L * 1024, privateMb * 1024L * 1024, managedMb * 1024L * 1024);

    [Fact]
    public void Cpu_is_shared_over_every_core_and_the_average_weighs_by_time()
    {
        // 8 cores. 0–10 s: 0.8 s of CPU (1%); 10–20 s: 4 s (5%); 20–60 s: 0.8 s (0.25%).
        var meter = new PerformanceMeter(8);
        Assert.Null(meter.Summary());                           // nothing yet
        meter.Add(Sample(0, 10, 150));
        Assert.Null(meter.Summary());                           // one sample: no CPU reading
        meter.Add(Sample(10, 10.8, 160));
        meter.Add(Sample(20, 14.8, 190, 140));
        meter.Add(Sample(15, 99, 900));                         // out of order: ignored
        meter.Add(Sample(60, 15.6, 170));
        var s = meter.Summary()!;
        Assert.Equal(4, s.Samples);
        Assert.Equal(TimeSpan.FromMinutes(1), s.Measured);
        Assert.Equal(5.6 / 60 / 8 * 100, s.AverageCpuPercent, 6); // total CPU over total wall time, not the mean of the three
        Assert.Equal(5, s.PeakCpuPercent, 6);
        Assert.Equal(5, s.P95CpuPercent, 6);
        Assert.Equal(190L * 1024 * 1024, s.PeakWorkingSetBytes);
        Assert.Equal(140L * 1024 * 1024, s.PeakPrivateBytes);
        Assert.Equal(150L * 1024 * 1024, s.StartWorkingSetBytes);              // the first reading, not the smallest
    }

    [Fact]
    public void The_budget_line_says_within_over_or_too_short()
    {
        long mb = 1024 * 1024;
        var ok = new PerformanceSummary(200, TimeSpan.FromMinutes(30), 0.84, 1.6, 4.1, 148 * mb, 121 * mb);
        Assert.Equal("CPU 0.8% average (p95 1.6%, peak 4.1%) · memory 148 MB peak (private 121 MB) · within budget (2%, 200 MB)", ok.Line);
        // With the .NET heap and the start known, they show inside the memory brackets.
        Assert.Contains("memory 148 MB peak (private 121 MB, .NET heap 52 MB; 130 MB at the start)",
            (ok with { PeakManagedBytes = 52 * mb, StartWorkingSetBytes = 130 * mb }).Line);
        Assert.EndsWith("over budget: CPU and memory (2%, 200 MB)", (ok with { AverageCpuPercent = 2.4, PeakWorkingSetBytes = 230 * mb }).Line);
        Assert.EndsWith("over budget: memory (2%, 200 MB)", (ok with { PeakWorkingSetBytes = 200 * mb }).Line);
        Assert.Contains("too short to judge (3 min measured)", (ok with { Measured = TimeSpan.FromMinutes(3) }).Line);
    }

    [Fact]
    public void Performance_counts_only_while_a_match_is_live_and_starts_over_with_the_next()
    {
        var log = new DiagnosticsLog(T0, cores: 4);
        log.Performance(Sample(0, 1, 120));                     // no match yet: dropped
        log.MatchStarted(7, false, T0.AddSeconds(5));
        log.Performance(Sample(10, 1, 130));
        log.Performance(Sample(20, 1.4, 135));                  // 0.4 s over 10 s on 4 cores: 1%
        log.MatchEnded(T0.AddSeconds(25));
        log.Performance(Sample(30, 9, 400));                    // after the end: dropped
        var perf = log.Snapshot().Match!.Performance!;
        Assert.Equal(1, perf.AverageCpuPercent, 6);
        Assert.Equal(135L * 1024 * 1024, perf.PeakWorkingSetBytes);
        Assert.Contains("performance: CPU 1.0% average", DiagnosticsReport.Format(log.Snapshot(),
            new LiveDiagnostics("1.0.0", "Direct", false, null, null, null, 0, false), T0.AddMinutes(1)));

        log.MatchStarted(8, false, T0.AddMinutes(1));
        Assert.Null(log.Snapshot().Match!.Performance);         // a new match starts empty
        Assert.Contains("performance: not measured yet", DiagnosticsReport.Format(log.Snapshot(),
            new LiveDiagnostics("1.0.0", "Direct", false, null, null, null, 0, true), T0.AddMinutes(2)));
    }
}
