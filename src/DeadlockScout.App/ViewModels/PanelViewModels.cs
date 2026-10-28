using System.Collections.ObjectModel;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
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
    private PointCollection _sparkline = new();
    private string _personalLine = "";
    private double? _personalDelta;
    public string Line { get => _line; set => Set(ref _line, value); }

    // The same reading in pieces, for the scoreboard layout: a big delta with the numbers beside it.
    private string _hero = "", _clock = "", _deltaText = "", _spm = "", _target = "", _percentile = "";
    public string Hero { get => _hero; set => Set(ref _hero, value); }
    /// <summary>Game clock of the reading, "14:20".</summary>
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    /// <summary>"+11%", or "–" without a benchmark.</summary>
    public string DeltaText { get => _deltaText; set => Set(ref _deltaText, value); }
    /// <summary>"643 spm (2m)".</summary>
    public string Spm { get => _spm; set => Set(ref _spm, value); }
    /// <summary>"pace 580", or "no benchmark yet".</summary>
    public string Target { get => _target; set => Set(ref _target, value); }
    /// <summary>"73rd pct", or empty.</summary>
    public string Percentile { get => _percentile; set => Set(ref _percentile, value); }

    /// <summary>"as of 12:40" because the broadcast is delayed.</summary>
    public string AsOf { get => _asOf; set => Set(ref _asOf, value); }
    public string Provider { get => _provider; set => Set(ref _provider, value); }
    public int SampleSize { get => _sampleSize; set => Set(ref _sampleSize, value); }
    public string Bracket { get => _bracket; set => Set(ref _bracket, value); }
    public string SourceLabel { get => _sourceLabel; set => Set(ref _sourceLabel, value); }
    public double? Delta { get => _delta; set => Set(ref _delta, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    /// <summary>
    /// The sparkline: pace delta over the last ten minutes in a <see cref="SparkWidth"/> × <see cref="SparkHeight"/> box,
    /// ±<see cref="SparkRange"/> from the middle line. Empty until there are a few points.
    /// </summary>
    public PointCollection Sparkline { get => _sparkline; set { if (Set(ref _sparkline, value)) Raise(nameof(HasSparkline)); } }
    public bool HasSparkline => _sparkline.Count >= 3;
    public const double SparkWidth = 240, SparkHeight = 26, SparkRange = 0.25;
    public static readonly TimeSpan SparkSpan = TimeSpan.FromMinutes(10), SparkStep = TimeSpan.FromSeconds(30);
    /// <summary>"vs your last 12 on Haze: +4%", or empty without a personal curve.</summary>
    public string PersonalLine { get => _personalLine; set => Set(ref _personalLine, value); }
    public double? PersonalDelta { get => _personalDelta; set => Set(ref _personalDelta, value); }
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
    /// <summary>Lane opponents only: your hero's lane win rate against theirs, "you 53% · 2.6k".</summary>
    public string Matchup { get; init; } = "";
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

/// <summary>The session view: today's results and rank, the loss-streak nudge, and the hero pool against the meta.</summary>
public sealed class SessionViewModel : ObservableObject
{
    private string _today = "";
    private string _nudge = "";
    private string _heroPool = "";
    private bool _hasData;
    /// <summary>"Today 4–2 · won 2 in a row · Archon 3 (+1)".</summary>
    public string Today { get => _today; set => Set(ref _today, value); }
    public string Nudge { get => _nudge; set => Set(ref _nudge, value); }
    /// <summary>"Haze 120 · 54% (meta 51%) · Infernus 60 · 48% (meta 52%) ...".</summary>
    public string HeroPool { get => _heroPool; set => Set(ref _heroPool, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
}

/// <summary>The post-match report, in the main window.</summary>
public sealed class PostMatchViewModel : ObservableObject
{
    private string _title = "";
    private string _pace = "";
    private string _lane = "";
    private string _footer = "";
    private bool _won;
    private bool _hasData;
    /// <summary>"Haze · Won · 34:12 · 8/3/11 · 31,240 souls (913/min) · 2nd of 6 on team".</summary>
    public string Title { get => _title; set => Set(ref _title, value); }
    /// <summary>"Pace at 10:00 +6% (64th pct) · 20:00 +3% · 30:00 −2%".</summary>
    public string Pace { get => _pace; set => Set(ref _pace, value); }
    /// <summary>"Lane at 10:00: +1,240 souls against Lady Geist".</summary>
    public string Lane { get => _lane; set => Set(ref _lane, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }
    public bool Won { get => _won; set => Set(ref _won, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
}

/// <summary>The build hint: what players buy at your souls and your next usual ability point.</summary>
public sealed class BuildHintViewModel : ObservableObject
{
    private string _title = "";
    private string _items = "";
    private string _abilities = "";
    private bool _hasData;
    /// <summary>"Bought at 8k–10k souls on Haze".</summary>
    public string Title { get => _title; set => Set(ref _title, value); }
    /// <summary>"Item A 54% · Item B 51% · Item C 49%": most bought first, with the buyers' win rate.</summary>
    public string Items { get => _items; set => Set(ref _items, value); }
    /// <summary>"Next: Sleep Dagger 3 (~13:20) · max Sleep Dagger first".</summary>
    public string Abilities { get => _abilities; set => Set(ref _abilities, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
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
    private string _netWorth = "", _deltaText = "";
    /// <summary>"9,800".</summary>
    public string NetWorthText { get => _netWorth; set => Set(ref _netWorth, value); }
    /// <summary>"+18%", or empty without a curve.</summary>
    public string DeltaText { get => _deltaText; set => Set(ref _deltaText, value); }
    private string _spikeText = "";
    private bool _spikeFlash;
    /// <summary>Their latest power-spike item ("Leech", "Ultimate"), "just got Leech" while it flashes, or empty.</summary>
    public string SpikeText { get => _spikeText; set => Set(ref _spikeText, value); }
    /// <summary>True for <see cref="Core.Builds.ItemSpikes.FlashFor"/> after the purchase shows up.</summary>
    public bool SpikeFlash { get => _spikeFlash; set => Set(ref _spikeFlash, value); }
    private System.Windows.Media.ImageSource? _spikeIcon;
    private string _spikeInitial = "";
    /// <summary>The item's icon once it has loaded; null until then, for the ultimate, and in screenshots.</summary>
    public System.Windows.Media.ImageSource? SpikeIcon { get => _spikeIcon; set => Set(ref _spikeIcon, value); }
    /// <summary>The item name's first letter, drawn in the icon's place while there is no icon.</summary>
    public string SpikeInitial { get => _spikeInitial; set => Set(ref _spikeInitial, value); }
    /// <summary>"ultimate", or the item's shop slot ("weapon", "spirit", "vitality"); picks the chip's colour.</summary>
    public string? SpikeSlot { get; set; }
    private System.Windows.Media.Brush _spikeColor = Converters.SpikeColor(null), _spikeFill = Converters.SpikeFill(null);
    /// <summary>The chip's colour (<see cref="Converters.SpikeColor"/>): its text and outline, or its fill while it flashes.</summary>
    public System.Windows.Media.Brush SpikeColor { get => _spikeColor; set => Set(ref _spikeColor, value); }
    /// <summary>The quiet chip's faint fill in the same colour.</summary>
    public System.Windows.Media.Brush SpikeFill { get => _spikeFill; set => Set(ref _spikeFill, value); }
}

public sealed class EnemyPaceViewModel : ObservableObject
{
    private string _title = "Enemy pace";
    public ObservableCollection<EnemyPaceRowViewModel> Rows { get; } = new();
    /// <summary>"Enemy pace" in your own match; every player's pace while spectating.</summary>
    public string Title { get => _title; set => Set(ref _title, value); }
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
    /// <summary>1,234 → "1.2k", 950 → "950".</summary>
    public static string Count(int n) => n >= 1000 ? (n / 1000.0).ToString("0.#") + "k" : n.ToString();
    public static string Matchup(HeroMatchup m) => $"you {Pct(m.WinRate)} · {Count(m.Matches)}";
    public static string Signed(double v) => (v >= 0 ? "+" : "") + (v * 100).ToString("0") + "%";
    public static string Clock(TimeSpan t) => PaceMath.FormatClock(t);
    public static string Ordinal(int n) => PaceMath.Ordinal(n);
    /// <summary>+1,240 / −830 (a real minus sign).</summary>
    public static string SignedCount(int n) => (n >= 0 ? "+" : "−") + Math.Abs(n).ToString("N0");
}

/// <summary>The two team headings of the scout list: a name and its colour.</summary>
public sealed record TeamHeader(string Name, Brush Brush);

public static class TeamHeaders
{
    public static readonly TeamHeader Amber = new("Amber", Frozen(Color.FromRgb(0xF2, 0xB1, 0x34)));
    public static readonly TeamHeader Sapphire = new("Sapphire", Frozen(Color.FromRgb(0x5F, 0xA8, 0xF5)));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
