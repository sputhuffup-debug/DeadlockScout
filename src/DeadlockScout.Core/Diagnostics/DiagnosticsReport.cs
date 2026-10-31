using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadlockScout.Core.Diagnostics;

/// <summary>What only the running app knows at the moment the report is made: the clock and souls sources, OCR's health, settings.</summary>
public sealed record LiveDiagnostics(
    string AppVersion,
    string LiveSourceSetting,
    bool OcrEnabled,
    string? ClockSource,
    string? SoulsSource,
    double? OcrReadRate,
    int OcrDropped,
    bool InMatch);

/// <summary>
/// The "copy diagnostics" text: plain lines a player can paste into an issue. Account ids, player names, the Steam name
/// and the API key are replaced by <see cref="Scrub"/> before it leaves the app. Pure, so it is unit tested.
/// </summary>
public static class DiagnosticsReport
{
    public const string Redacted = "<redacted>";

    public static string Format(DiagnosticsSnapshot s, LiveDiagnostics live, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DeadlockScout {live.AppVersion} diagnostics, {now.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Settings: live feed {live.LiveSourceSetting}, OCR {(live.OcrEnabled ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine(live.InMatch ? "Match (live)" : "Last match");
        if (s.Match is not { } m)
        {
            sb.AppendLine("  none since the app started");
        }
        else
        {
            sb.AppendLine($"  {(m.Spectating ? "spectated " : "")}match {m.MatchId}, started {m.StartedAt.ToUniversalTime():HH:mm:ss} UTC" +
                          (m.EndedAt is { } end ? $", ended after {Duration(end - m.StartedAt)}" : ""));
            sb.AppendLine($"  feed: {m.Feed ?? "none yet"}");
            sb.AppendLine($"  roster: {(m.RosterAfter is { } r ? $"after {Duration(r)}" : "not received")}");
            sb.AppendLine($"  broadcast delay: {(m.Delay is { } d ? $"{d.TotalSeconds:0.0} s ({m.DelayHow})" : "not measured")}");
            if (m.ClockOffset is { } o)
                sb.AppendLine($"  game-log clock vs HUD clock: {o.TotalSeconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} s");
            sb.AppendLine($"  performance: {(m.Performance is { } perf ? perf.Line : "not measured yet")}");
            foreach (var n in m.Notes) sb.AppendLine($"  note: {n}");
        }
        sb.AppendLine($"  clock now: {live.ClockSource ?? "–"}; souls from: {live.SoulsSource ?? "–"}" +
                      (live.InMatch && live.OcrReadRate is { } rate ? $"; OCR reads {rate:P0}" : "") + // nothing to read between matches
                      (live.OcrDropped > 0 ? $"; {live.OcrDropped} OCR readings dropped as a teammate's" : ""));
        sb.AppendLine();
        sb.AppendLine($"API since {s.Since.ToUniversalTime():HH:mm} UTC (attempts, errors, 429s, 404s, avg time; our rate-limit waits)");
        if (s.Api.Count == 0) sb.AppendLine("  no calls yet");
        foreach (var f in s.Api)
            sb.AppendLine($"  {f.Family}: {f.Attempts}, {f.Errors} errors, {f.TooMany} × 429, {f.NotFound} × 404, avg {f.AverageTime.TotalMilliseconds:0} ms" +
                          (f.Waits > 0 ? $"; waited {f.Waits}× for {Duration(f.Waited)}, longest {Duration(f.LongestWait)}" : ""));
        if (s.Errors.Count > 0)
        {
            sb.AppendLine("Latest errors");
            foreach (var e in s.Errors)
                sb.AppendLine($"  {e.At.ToUniversalTime():HH:mm:ss} {e.Family} {e.Path} {(e.Status is { } st ? st.ToString(CultureInfo.InvariantCulture) : "")}{(e.Error is { } err ? " " + err : "")}".TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replaces every secret in <paramref name="text"/>: numbers (account ids, the SteamID64) only as whole numbers, so a
    /// match id or a time that happens to contain one survives; names and keys wherever they appear, ignoring case.
    /// </summary>
    public static string Scrub(string text, IEnumerable<string?> secrets)
    {
        foreach (var secret in secrets.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct().OrderByDescending(x => x.Length))
        {
            if (secret.All(char.IsDigit))
            {
                if (secret.Length >= 3) text = Regex.Replace(text, $@"(?<!\d){secret}(?!\d)", Redacted);
            }
            else if (secret.Length >= 2)
            {
                text = Regex.Replace(text, Regex.Escape(secret), Redacted, RegexOptions.IgnoreCase);
            }
        }
        return text;
    }

    /// <summary>
    /// Every path under the user's profile folder written as <c>%USERPROFILE%</c>. The folder is named when the Windows
    /// account is made and can differ from the user name (a display name, a Microsoft account), so scrubbing the user
    /// name alone left it in the settings path.
    /// </summary>
    public static string HideProfile(string text, string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return text;
        var path = profilePath.TrimEnd('\\', '/');
        if (path.Length < 3) return text; // a bare drive or root: nothing personal to hide
        return Regex.Replace(text, Regex.Escape(path), "%USERPROFILE%", RegexOptions.IgnoreCase);
    }

    public static string Duration(TimeSpan t) => t.TotalSeconds < 60
        ? $"{t.TotalSeconds:0.#} s"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
