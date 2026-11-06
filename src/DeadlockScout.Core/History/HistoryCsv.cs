using System.Globalization;
using System.Text;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>One game's row for the CSV: the history row, the hero's name, the pace against the gate when known, the rank text.</summary>
public sealed record CsvGame(MatchHistoryEntry Entry, string Hero, double? VersusGate, string? Gate, string Rank);

/// <summary>
/// The match history as a CSV for a spreadsheet (0.6): your own numbers only, one row per game, numbers in invariant
/// culture so any spreadsheet reads them. Pure, so tested.
/// </summary>
public static class HistoryCsv
{
    public const string Header = "date,match_id,hero,result,kills,deaths,assists,souls,minutes,souls_per_minute,vs_gate,gate,rank";

    public static string Format(IEnumerable<CsvGame> games, TimeZoneInfo zone)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var g in games)
        {
            var e = g.Entry;
            double minutes = e.Duration.TotalMinutes;
            string result = e.IsWin ? "win" : e.IsLoss ? (e.Abandoned ? "left" : "loss") : "not scored";
            sb.AppendJoin(',',
                TimeZoneInfo.ConvertTime(e.StartTime, zone).ToString("yyyy-MM-dd HH:mm", inv),
                e.MatchId.ToString(inv),
                Field(g.Hero),
                result,
                e.Kills.ToString(inv), e.Deaths.ToString(inv), e.Assists.ToString(inv),
                e.NetWorth.ToString(inv),
                minutes.ToString("0.0", inv),
                (minutes > 0 ? e.NetWorth / minutes : 0).ToString("0", inv),
                g.VersusGate is { } v ? v.ToString("0.000", inv) : "",
                Field(g.Gate ?? ""),
                Field(g.Rank));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>A text field, quoted when it holds a comma, a quote or a line break.</summary>
    public static string Field(string s)
        => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
