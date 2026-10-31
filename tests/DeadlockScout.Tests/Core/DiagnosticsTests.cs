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
}
