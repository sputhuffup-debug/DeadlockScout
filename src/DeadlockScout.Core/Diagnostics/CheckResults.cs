using System.Globalization;
using System.Text;

namespace DeadlockScout.Core.Diagnostics;

/// <summary>
/// Diagnostics → "Copy check results": the lines the check sheet (docs/verification/checks-0.2.md) asks to be sent
/// after a match, in one block, each named by its check, so they are pasted at once instead of copied one by one. Only
/// what the app knows itself; the sheet's other answers (what the calibration tool showed, what the match looked like)
/// stay the player's. The caller scrubs the text like Copy diagnostics. Pure, so tested.
/// </summary>
public static class CheckResults
{
    /// <param name="setup">The Diagnostics page's setup lines ("Glyph reader: …", one per line).</param>
    /// <param name="contentLog">Steam's content log lines naming Deadlock seen this run (check 3).</param>
    /// <param name="respawns">The camp and crate respawns seen this run (check 7).</param>
    public static string Format(string appVersion, string setup, DiagnosticsSnapshot snapshot, LiveDiagnostics live, DateTimeOffset now,
                                IReadOnlyList<string>? contentLog = null, IReadOnlyList<string>? respawns = null)
    {
        var lines = setup.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        string? Setup(string label) =>
            lines.FirstOrDefault(l => l.StartsWith(label + ":", StringComparison.Ordinal)) is { } line ? line[(label.Length + 1)..].Trim() : null;

        var sb = new StringBuilder();
        sb.AppendLine($"DeadlockScout {appVersion} check results, {now.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
        void Line(string check, string what, string? value) => sb.AppendLine($"{check} · {what}: {value ?? "not available"}");

        Line("Check 2", "memory now", Setup("Memory now"));
        Line("Check 6", "match ingest", Setup("Match ingest"));
        Line("Check 6", "catch-up at start", Setup("Catch-up at start"));
        Line("Check 10", "glyph reader", Setup("Glyph reader"));
        Line("Check 10", "OCR net worth", live.OcrNetWorth ?? "no match compared with the broadcast yet");
        Line("Check 10", "OCR readings", $"{live.OcrDropped.ToString(CultureInfo.InvariantCulture)} dropped; clock: "
                                         + $"{live.ClockCorrected.ToString(CultureInfo.InvariantCulture)} put right, "
                                         + $"{live.ClockRejected.ToString(CultureInfo.InvariantCulture)} dropped");
        Line("Check 19", "process hardening", Setup("Process hardening"));
        Line("Check 3", "Steam content log (Deadlock)", contentLog is { Count: > 0 } c
            ? string.Join(" | ", c)
            : "none since the app started (switch Deadlock's automatic updates off and on in Steam, then copy again)");
        Line("Check 7", "respawns seen", respawns is { Count: > 0 } r ? string.Join("; ", r) : "none since the app started");
        if (snapshot.Match is { } m)
        {
            Line("Gate", "last match", $"{(m.Spectating ? "spectated, " : "")}feed {m.Feed ?? "none"}, broadcast delay "
                                       + (m.Delay is { } d ? $"{d.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s" : "not measured"));
            Line("Gate", "performance", m.Performance?.Line ?? "not measured yet");
        }
        else Line("Gate", "last match", "none since the app started");
        sb.AppendLine("Not in here: the calibration tool's lines (checks 5, 9) and what you saw in the match.");
        return sb.ToString();
    }
}
