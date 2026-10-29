using System.Collections.ObjectModel;
using System.Windows.Media;
using DeadlockScout.App.Services;
using DeadlockScout.Core.History;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeadlockScout.App.ViewModels;

/// <summary>A choice in the trends' range or hero box: what it shows and the value it stands for.</summary>
public sealed record TrendOption(string Text, int Value)
{
    public override string ToString() => Text;
}

/// <summary>
/// The trends section of the match history: over the newest 20, 50 or 100 games (optionally on one hero), the rolling
/// win rate and souls per minute, the older half against the newer, and a row per hero. Recomputed from the history
/// rows whenever the range or hero changes; nothing stored.
/// </summary>
public sealed class TrendsViewModel : ObservableObject
{
    private static readonly Brush Up = Frozen(Color.FromRgb(0x7F, 0xE0, 0x8A));
    private static readonly Brush Down = Frozen(Color.FromRgb(0xF0, 0x7A, 0x6E));
    private static readonly Brush Flat = Frozen(Color.FromRgb(0x8A, 0x94, 0xA3));

    private IReadOnlyList<Core.Models.MatchHistoryEntry> _history = Array.Empty<Core.Models.MatchHistoryEntry>();
    private AssetCatalog? _assets;
    private ItemIcons? _icons;
    private DateTimeOffset _now;
    private TrendOption? _range, _hero;
    private bool _loading, _hasData;
    private string _title = "Trends", _winRateChange = "", _spmChange = "", _kdaChange = "";
    private Brush _winRateBrush = Flat, _spmBrush = Flat, _kdaBrush = Flat;
    private IReadOnlyList<TrendPoint> _points = Array.Empty<TrendPoint>();
    private IReadOnlyList<string> _labels = Array.Empty<string>();

    public ObservableCollection<TrendOption> Ranges { get; } = new()
    {
        new("Last 20 games", 20), new("Last 50 games", 50), new("Last 100 games", MatchHistoryService.TrendGames),
    };
    /// <summary>"All heroes" (0), then every hero in the history, most played first.</summary>
    public ObservableCollection<TrendOption> HeroChoices { get; } = new();
    public ObservableCollection<HeroTrendRowViewModel> Heroes { get; } = new();

    public TrendOption? Range { get => _range; set { if (Set(ref _range, value)) Recompute(); } }
    public TrendOption? Hero { get => _hero; set { if (Set(ref _hero, value)) Recompute(); } }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    /// <summary>"Trends · last 50 games on Haze".</summary>
    public string Title { get => _title; set => Set(ref _title, value); }
    public IReadOnlyList<TrendPoint> Points { get => _points; set => Set(ref _points, value); }
    /// <summary>One tooltip per point: "Haze · won · 612 souls/min · 8/3/11 · 3 days ago".</summary>
    public IReadOnlyList<string> Labels { get => _labels; set => Set(ref _labels, value); }
    /// <summary>"Win rate 45% → 58%" (older half → newer half).</summary>
    public string WinRateChange { get => _winRateChange; set => Set(ref _winRateChange, value); }
    public string SpmChange { get => _spmChange; set => Set(ref _spmChange, value); }
    public string KdaChange { get => _kdaChange; set => Set(ref _kdaChange, value); }
    public Brush WinRateBrush { get => _winRateBrush; set => Set(ref _winRateBrush, value); }
    public Brush SpmBrush { get => _spmBrush; set => Set(ref _spmBrush, value); }
    public Brush KdaBrush { get => _kdaBrush; set => Set(ref _kdaBrush, value); }

    public void Load(IReadOnlyList<Core.Models.MatchHistoryEntry> newestFirst, AssetCatalog assets, ItemIcons? icons, DateTimeOffset now)
    {
        _loading = true;
        _history = newestFirst; _assets = assets; _icons = icons; _now = now;
        int keepHero = _hero?.Value ?? 0;
        HeroChoices.Clear();
        HeroChoices.Add(new TrendOption("All heroes", 0));
        foreach (var h in HistoryTrends.Heroes(newestFirst))
            HeroChoices.Add(new TrendOption($"{assets.HeroName(h.HeroId)} ({h.Games})", h.HeroId));
        _range ??= Ranges[1];
        _hero = HeroChoices.FirstOrDefault(c => c.Value == keepHero) ?? HeroChoices[0];
        Raise(nameof(Range)); Raise(nameof(Hero));
        _loading = false;
        Recompute();
    }

    private void Recompute()
    {
        if (_loading || _assets is not { } assets || _range is not { } range) return;
        int? hero = _hero is { Value: > 0 } h ? h.Value : null;
        var games = HistoryTrends.Range(_history, range.Value, hero);
        Title = $"Trends · last {games.Count} {(games.Count == 1 ? "game" : "games")}" + (hero is { } id ? $" on {assets.HeroName(id)}" : "");
        Points = HistoryTrends.Series(games);
        Labels = Points.Select(p => $"{assets.HeroName(p.Game.HeroId)} · {(p.Won is true ? "won" : p.Won is false ? "lost" : "not scored")} · " +
                                    $"{p.SoulsPerMinute:0} souls/min · {p.Game.Kills}/{p.Game.Deaths}/{p.Game.Assists} · " +
                                    GameCardViewModel.Ago(_now - (p.Game.StartTime + p.Game.Duration))).ToList();

        if (HistoryTrends.Compare(games) is { } c)
        {
            WinRateChange = c.WinRateThen is { } wt && c.WinRateNow is { } wn ? $"Win rate {Format.Pct(wt)} → {Format.Pct(wn)}" : "";
            WinRateBrush = Direction(c.WinRateThen, c.WinRateNow, 0.03);
            SpmChange = $"Souls/min {c.SpmThen:0} → {c.SpmNow:0}";
            SpmBrush = Direction(c.SpmThen, c.SpmNow, 15);
            KdaChange = $"KDA {c.KdaThen:0.00} → {c.KdaNow:0.00}";
            KdaBrush = Direction(c.KdaThen, c.KdaNow, 0.15);
        }
        else { WinRateChange = SpmChange = KdaChange = ""; }

        Heroes.Clear();
        foreach (var r in HistoryTrends.Heroes(games)) Heroes.Add(new HeroTrendRowViewModel(r, assets.HeroName(r.HeroId), _now));
        if (_icons is { } icons) RefreshIcons(icons); // portraits already fetched show at once after a filter change
        HasData = games.Count > 0;
    }

    /// <summary>Picks up hero portraits that have loaded. False once nothing is waiting.</summary>
    public bool RefreshIcons(ItemIcons icons)
    {
        bool waiting = false;
        foreach (var h in Heroes) waiting |= h.Refresh(icons);
        return waiting;
    }

    /// <summary>Green when the newer half is better by more than <paramref name="margin"/>, red when worse, grey otherwise.</summary>
    private static Brush Direction(double? then, double? now, double margin)
        => then is not { } t || now is not { } n ? Flat : n - t > margin ? Up : t - n > margin ? Down : Flat;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>One hero in the trends table.</summary>
public sealed class HeroTrendRowViewModel : IconViewModel
{
    public HeroTrendRowViewModel(HeroTrendRow r, string name, DateTimeOffset now)
    {
        HeroId = r.HeroId;
        Name = name;
        Games = r.Games.ToString();
        Record = $"{r.Wins}–{r.Losses}";
        WinRate = r.WinRate is { } w ? Format.Pct(w) : "–";
        Kda = r.Kda.ToString("0.00");
        Spm = $"{r.AvgSoulsPerMinute:0}";
        LastPlayed = GameCardViewModel.Ago(now - r.LastPlayed);
    }

    public int HeroId { get; }
    public string Name { get; }
    public string Games { get; }
    public string Record { get; }
    public string WinRate { get; }
    public string Kda { get; }
    public string Spm { get; }
    public string LastPlayed { get; }
    public override string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool Refresh(ItemIcons icons) { Icon ??= icons.GetHero(HeroId); return Icon is null && icons.Enabled; }
}
