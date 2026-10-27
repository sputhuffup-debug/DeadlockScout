using System.Collections.ObjectModel;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.App.ViewModels;

public sealed class StatusViewModel : ObservableObject
{
    private string _phase = "Waiting for a match";
    private long _matchId;
    private string _heroName = "";
    private string _detection = "";
    private string _warning = "";
    public string Phase { get => _phase; set => Set(ref _phase, value); }
    public long MatchId { get => _matchId; set => Set(ref _matchId, value); }
    public string HeroName { get => _heroName; set => Set(ref _heroName, value); }
    /// <summary>Paths found, condebug state, feed URL: shown in the main window.</summary>
    public string Detection { get => _detection; set => Set(ref _detection, value); }
    public string Warning { get => _warning; set => Set(ref _warning, value); }
}

/// <summary>The pace strip: <c>Haze · 14:20 · 612 spm (2m) · pace 580 · +5% · 58th pct</c>.</summary>
public sealed class PaceStripViewModel : ObservableObject
{
    private string _line = "";
    private string _asOf = "";
    private string _provider = "";
    private int _sampleSize;
    private string _bracket = "";
    private string _sourceLabel = "broadcast";
    private double? _delta;
    private bool _hasData;
    public string Line { get => _line; set => Set(ref _line, value); }
    /// <summary>"as of 12:40" because the broadcast is delayed.</summary>
    public string AsOf { get => _asOf; set => Set(ref _asOf, value); }
    public string Provider { get => _provider; set => Set(ref _provider, value); }
    public int SampleSize { get => _sampleSize; set => Set(ref _sampleSize, value); }
    public string Bracket { get => _bracket; set => Set(ref _bracket, value); }
    public string SourceLabel { get => _sourceLabel; set => Set(ref _sourceLabel, value); }
    public double? Delta { get => _delta; set => Set(ref _delta, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public string Tooltip => $"Benchmark: {Provider} ({SampleSize} matches, badges {Bracket}) · source: {SourceLabel}";
}

public sealed class ScoutRowViewModel
{
    public required uint AccountId { get; init; }
    public required string Name { get; init; }
    public required string Hero { get; init; }
    public required int Team { get; init; }
    public required bool IsMe { get; init; }
    public required string GamesOnHero { get; init; }
    public required string WinRate { get; init; }
    public required string Rank { get; init; }
    public required string Met { get; init; }
    public required string Flag { get; init; }
    public string Lane { get; init; } = "";
}

/// <summary>The scout list, two columns by team, and the lane card (your lane opponents).</summary>
public sealed class ScoutListViewModel : ObservableObject
{
    public ObservableCollection<ScoutRowViewModel> Amber { get; } = new();
    public ObservableCollection<ScoutRowViewModel> Sapphire { get; } = new();
    public ObservableCollection<ScoutRowViewModel> LaneOpponents { get; } = new();
    private string _laneTitle = "Lane";
    private string _rankSpread = "";
    public string LaneTitle { get => _laneTitle; set => Set(ref _laneTitle, value); }
    public string RankSpread { get => _rankSpread; set => Set(ref _rankSpread, value); }
}

public sealed class EnemyPaceRowViewModel : ObservableObject
{
    private string _line = "";
    private double _lead;
    public required uint AccountId { get; init; }
    public required string Name { get; init; }
    public required string Hero { get; init; }
    public string Line { get => _line; set => Set(ref _line, value); }
    /// <summary>Net worth relative to the hero's curve, as a fraction; sorted descending.</summary>
    public double Lead { get => _lead; set => Set(ref _lead, value); }
}

public sealed class EnemyPaceViewModel : ObservableObject
{
    public ObservableCollection<EnemyPaceRowViewModel> Rows { get; } = new();
}

/// <summary>Helpers shared by overlay and main window.</summary>
public static class Format
{
    public static string Rank(int? badge)
    {
        if (badge is null or <= 0) return "–";
        int tier = badge.Value / 10, sub = badge.Value % 10;
        string name = tier switch
        {
            1 => "Initiate", 2 => "Seeker", 3 => "Alchemist", 4 => "Arcanist", 5 => "Ritualist", 6 => "Emissary",
            7 => "Archon", 8 => "Oracle", 9 => "Phantom", 10 => "Ascendant", 11 => "Eternus", _ => $"Badge {badge}",
        };
        return sub > 0 ? $"{name} {sub}" : name;
    }

    public static string Pct(double v) => (v * 100).ToString("0") + "%";
    public static string Signed(double v) => (v >= 0 ? "+" : "") + (v * 100).ToString("0") + "%";
    public static string Clock(TimeSpan t) => PaceMath.FormatClock(t);
}
