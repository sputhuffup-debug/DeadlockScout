using DeadlockScout.Core.Diagnostics;

namespace DeadlockScout.Tests.Core;

public class CheckResultsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);

    private const string Setup = "Version: 0.3.3\nSteam: found\nGlyph reader: clock: ready · 98% of reads; souls: learning, knows 7 of 10 digits\n"
                                 + "Memory now: 118 MB (private 41 MB) · main window released\nProcess hardening: on: DLL search, image loads, extension points, access list\n"
                                 + "Match ingest: 1 match sent\nCatch-up at start: at 17:08: 1 match finished before the app started, 0 already in deadlock-api, 1 asked for from Steam, 1 fetched";

    [Fact]
    public void After_a_match_every_line_the_sheet_asks_for_is_named_by_its_check()
    {
        var log = new DiagnosticsLog(T0);
        log.MatchStarted(42, false, T0);
        log.Feed("Direct broadcast");
        log.BroadcastDelay(TimeSpan.FromSeconds(34.1), "against the game log's start line");
        log.MatchEnded(T0.AddMinutes(30));
        var live = new LiveDiagnostics("0.3.3", "Direct", true, null, null, 0.97, 12, false,
            "within 60 souls of the broadcast (median of 1,412 readings, 9 in 10 within 240); 11 purchases seen", ClockCorrected: 4, ClockRejected: 1);

        var text = CheckResults.Format("0.3.3", Setup, log.Snapshot(), live, T0.AddHours(1));
        Assert.StartsWith("DeadlockScout 0.3.3 check results, 2026-11-04 19:00 UTC", text);
        Assert.Contains("Check 2 · memory now: 118 MB (private 41 MB) · main window released", text);
        Assert.Contains("Check 6 · match ingest: 1 match sent", text);
        Assert.Contains("Check 6 · catch-up at start: at 17:08: 1 match finished before the app started", text);
        Assert.Contains("Check 10 · glyph reader: clock: ready · 98% of reads; souls: learning, knows 7 of 10 digits", text);
        Assert.Contains("Check 10 · OCR net worth: within 60 souls", text);
        Assert.Contains("Check 10 · OCR readings: 12 dropped; clock: 4 put right, 1 dropped", text);
        Assert.Contains("Check 19 · process hardening: on: DLL search", text);
        Assert.Contains("Gate · last match: feed Direct broadcast, broadcast delay 34.1 s", text);
        Assert.Contains("Gate · performance:", text);
        Assert.DoesNotContain("Steam: found", text); // only what the sheet asks for
    }

    [Fact]
    public void Without_a_match_or_a_line_it_says_so()
    {
        var text = CheckResults.Format("0.3.3", "Version: 0.3.3", new DiagnosticsLog(T0).Snapshot(),
            new LiveDiagnostics("0.3.3", "Direct", false, null, null, null, 0, false), T0);
        Assert.Contains("Check 10 · glyph reader: not available", text);
        Assert.Contains("OCR net worth: no match compared with the broadcast yet", text);
        Assert.Contains("Gate · last match: none since the app started", text);
    }

    [Fact]
    public void The_content_log_and_respawn_lines_seen_this_run_are_pasted_too()
    {
        var live = new LiveDiagnostics("0.5.0", "Direct", false, null, null, null, 0, false);
        var content = new RecentLines(2);
        content.Add("[2026-11-05 20:01:02] AppID 1422450 state changed : Fully Installed,App Running,");
        content.Add("  [2026-11-05 20:02:00] AppID 1422450 auto update disabled  ");
        content.Add("[2026-11-05 20:03:00] AppID 1422450 auto update enabled");           // the oldest is dropped
        var respawns = new RecentLines(5);
        respawns.Add("Small camp back 1:27 (setting 1:25) at 9:12");

        var text = CheckResults.Format("0.5.0", "Version: 0.5.0", new DiagnosticsLog(T0).Snapshot(), live, T0, content.Snapshot(), respawns.Snapshot());
        Assert.Contains("Check 3 · Steam content log (Deadlock): [2026-11-05 20:02:00] AppID 1422450 auto update disabled | [2026-11-05 20:03:00] AppID 1422450 auto update enabled", text);
        Assert.Contains("Check 7 · respawns seen: Small camp back 1:27 (setting 1:25) at 9:12", text);
        Assert.DoesNotContain("findstr", text);

        var none = CheckResults.Format("0.5.0", "Version: 0.5.0", new DiagnosticsLog(T0).Snapshot(), live, T0);
        Assert.Contains("Check 3 · Steam content log (Deadlock): none since the app started", none);
        Assert.Contains("Check 7 · respawns seen: none since the app started", none);
    }

    [Fact]
    public void The_catch_up_line_says_what_the_start_up_scan_found()
    {
        var at = new DateTimeOffset(2026, 11, 7, 4, 0, 0, TimeSpan.Zero);
        string Clock() => at.ToLocalTime().ToString("HH:mm");
        Assert.StartsWith("not run yet", DeadlockScout.Core.PostMatch.CatchUpRun.Summary(null));
        Assert.Equal($"at {Clock()}: console.log not found", DeadlockScout.Core.PostMatch.CatchUpRun.Summary(new(at, false, 0, 0, 0, 0)));
        Assert.Equal($"at {Clock()}: no finished match in console.log", DeadlockScout.Core.PostMatch.CatchUpRun.Summary(new(at, true, 0, 0, 0, 0)));
        Assert.Equal($"at {Clock()}: 2 matches finished before the app started, 2 already in deadlock-api",
            DeadlockScout.Core.PostMatch.CatchUpRun.Summary(new(at, true, 2, 2, 0, 0)));
        Assert.Equal($"at {Clock()}: 1 match finished before the app started, 0 already in deadlock-api, 1 asked for from Steam, 0 fetched",
            DeadlockScout.Core.PostMatch.CatchUpRun.Summary(new(at, true, 1, 0, 1, 0)));
    }
}
