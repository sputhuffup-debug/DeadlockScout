using System.Collections.ObjectModel;
using System.Windows.Media;
using DeadlockScout.App.Controls;
using DeadlockScout.App.Services;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeadlockScout.App.ViewModels;

/// <summary>
/// The match history window: a summary row over your recent games and one card per game, each with a soul timeline
/// and its item purchases. Built from a <see cref="MatchHistoryPage"/>; everything shown is derived here, nothing stored.
/// </summary>
public sealed class MatchHistoryViewModel : ObservableObject
{
    private static readonly Brush WinBrush = Frozen(Color.FromRgb(0x7F, 0xE0, 0x8A));
    private static readonly Brush LossBrush = Frozen(Color.FromRgb(0xF0, 0x7A, 0x6E));
    private static readonly Brush NeutralBrush = Frozen(Color.FromRgb(0x5A, 0x63, 0x70));

    private string _status = "Loading your match history…";
    private bool _hasData;
    private string _rank = "–", _rankLine = "", _winRate = "–", _record = "", _kda = "–", _kdaLine = "", _gamesTitle = "Recent games";
    private PointCollection _rankPoints = new();

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    /// <summary>"Archon 3".</summary>
    public string Rank { get => _rank; set => Set(ref _rank, value); }
    /// <summary>"peak Archon 5 · +2 in 30 days".</summary>
    public string RankLine { get => _rankLine; set => Set(ref _rankLine, value); }
    /// <summary>The rank over the last 30 days, scaled to the 160×36 box beside it.</summary>
    public PointCollection RankPoints { get => _rankPoints; set => Set(ref _rankPoints, value); }
    public string WinRate { get => _winRate; set => Set(ref _winRate, value); }
    /// <summary>"12W 8L · 20 games".</summary>
    public string Record { get => _record; set => Set(ref _record, value); }
    public string Kda { get => _kda; set => Set(ref _kda, value); }
    /// <summary>"10.9 / 5.0 / 6.9 per game".</summary>
    public string KdaLine { get => _kdaLine; set => Set(ref _kdaLine, value); }
    public string GamesTitle { get => _gamesTitle; set => Set(ref _gamesTitle, value); }
    public ObservableCollection<HeroUsageViewModel> MainHeroes { get; } = new();
    /// <summary>The last 15 results, newest first, as coloured bars.</summary>
    public ObservableCollection<Brush> LastResults { get; } = new();
    public ObservableCollection<GameCardViewModel> Games { get; } = new();

    public void Load(MatchHistoryPage page, AssetCatalog assets, ItemIcons? icons, DateTimeOffset now)
    {
        var s = page.Summary;
        Rank = Format.Rank(page.Rank.Current);
        var parts = new List<string>();
        if (page.Rank.Peak is { } peak) parts.Add($"peak {Format.Rank(peak)}");
        if (page.Rank.NetChange is { } change) parts.Add($"{(change >= 0 ? "+" : "−")}{Math.Abs(change)} in 30 days");
        RankLine = parts.Count > 0 ? string.Join(" · ", parts) : "no ranked games in 30 days";
        RankPoints = Sparkline(page.Rank);
        WinRate = s.Wins + s.Losses > 0 ? Format.Pct(s.WinRate) : "–";
        Record = $"{s.Wins}W {s.Losses}L · {s.Games} games";
        Kda = s.Games > 0 ? s.Kda.ToString("0.00") : "–";
        KdaLine = $"{s.AvgKills:0.0} / {s.AvgDeaths:0.0} / {s.AvgAssists:0.0} per game";
        GamesTitle = $"Recent games · {s.Games}";

        MainHeroes.Clear();
        foreach (var h in s.TopHeroes)
            MainHeroes.Add(new HeroUsageViewModel(h.HeroId, assets.HeroName(h.HeroId), $"{h.Games} {(h.Games == 1 ? "game" : "games")} · {Format.Pct(h.WinRate)}"));
        LastResults.Clear();
        foreach (var r in s.LastResults) LastResults.Add(r is true ? WinBrush : r is false ? LossBrush : NeutralBrush);

        Games.Clear();
        foreach (var g in page.Games) Games.Add(new GameCardViewModel(g, assets, now));
        if (Games.Count > 0) Games[0].IsExpanded = true;
        RefreshIcons(icons);

        HasData = s.Games > 0;
        Status = HasData ? "" : "No games yet. Matches show here once the API has your match history.";
    }

    /// <summary>Picks up icons that have loaded since the last call. False once nothing is waiting any more.</summary>
    public bool RefreshIcons(ItemIcons? icons)
    {
        if (icons is null) return false;
        bool waiting = false;
        foreach (var h in MainHeroes) waiting |= h.Refresh(icons);
        foreach (var g in Games) waiting |= g.RefreshIcons(icons);
        return waiting;
    }

    private static PointCollection Sparkline(RankTrend trend)
    {
        const double w = 160, h = 36;
        var pts = new PointCollection();
        if (trend.Points.Count > 1)
        {
            var idx = trend.Points.Select(p => (double)HistoryMath.BadgeIndex(p.Badge)).ToList();
            double lo = idx.Min() - 1, hi = idx.Max() + 1;
            for (int i = 0; i < idx.Count; i++)
                pts.Add(new System.Windows.Point(i * w / (idx.Count - 1), h - (idx[i] - lo) / (hi - lo) * h));
        }
        pts.Freeze();
        return pts;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>An icon that may still be loading: the image once it has, the name's initial until then.</summary>
public abstract class IconViewModel : ObservableObject
{
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; protected set => Set(ref _icon, value); }
    public abstract string Initial { get; }
}

public sealed class HeroUsageViewModel : IconViewModel
{
    public HeroUsageViewModel(int heroId, string name, string line) { HeroId = heroId; Name = name; Line = line; }
    public int HeroId { get; }
    public string Name { get; }
    /// <summary>"8 games · 62%".</summary>
    public string Line { get; }
    public override string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool Refresh(ItemIcons icons) { Icon ??= icons.GetHero(HeroId); return Icon is null && icons.Enabled; }
}

/// <summary>An item as a small square: its icon, or its initial on its slot's colour until the icon loads.</summary>
public sealed class ItemChipViewModel : IconViewModel
{
    public ItemChipViewModel(long itemId, AssetCatalog assets)
    {
        ItemId = itemId;
        var item = assets.Item(itemId);
        Name = assets.ItemName(itemId);
        Color = Converters.SpikeColor(item?.Slot);
        Fill = Converters.SpikeFill(item?.Slot);
        Cost = item?.Cost ?? 0;
    }
    public long ItemId { get; }
    public string Name { get; }
    public int Cost { get; }
    public Brush Color { get; }
    public Brush Fill { get; }
    public override string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool Refresh(ItemIcons icons) { Icon ??= icons.Get(ItemId); return Icon is null && icons.Enabled; }
}

/// <summary>One line of the purchase timeline: "12:31  [icon] Leech", and when it was sold, if it was.</summary>
public sealed class PurchaseViewModel
{
    public PurchaseViewModel(MatchItem p, AssetCatalog assets)
    {
        Chip = new ItemChipViewModel(p.ItemId, assets);
        At = p.BoughtAt;
        TimeText = Format.Clock(p.BoughtAt);
        SoldText = p.SoldAt is { } s ? $"sold {Format.Clock(s)}" : "";
    }
    public ItemChipViewModel Chip { get; }
    public TimeSpan At { get; }
    public string TimeText { get; }
    public string SoldText { get; }
    public bool Sold => SoldText.Length > 0;
}

/// <summary>A player in a card's team columns: hero and "5/2/7", you in bold; their souls on hover.</summary>
public sealed record PlayerLineViewModel(string Hero, string Kda, string Souls, bool IsMe);

public sealed class GameCardViewModel : ObservableObject
{
    private static readonly Brush WinEdge = Frozen(Color.FromRgb(0x7F, 0xE0, 0x8A));
    private static readonly Brush LossEdge = Frozen(Color.FromRgb(0xF0, 0x7A, 0x6E));
    private static readonly Brush OtherEdge = Frozen(Color.FromRgb(0x5A, 0x63, 0x70));

    private readonly HeroIcon _hero;
    private bool _expanded;
    private IReadOnlyList<TimelineMarker> _markers = Array.Empty<TimelineMarker>();

    public GameCardViewModel(HistoryGame g, AssetCatalog assets, DateTimeOffset now)
    {
        var e = g.Entry;
        MatchId = e.MatchId;
        ResultText = e.IsWin ? "VICTORY" : e.IsLoss ? (e.Abandoned ? "LEFT" : "DEFEAT") : "NOT SCORED";
        Edge = e.IsWin ? WinEdge : e.IsLoss ? LossEdge : OtherEdge;
        TimeAgo = Ago(now - (e.StartTime + e.Duration));
        DurationText = Format.Clock(e.Duration);
        _hero = new HeroIcon(e.HeroId, assets.HeroName(e.HeroId));
        KdaLine = $"{e.Kills} / {e.Deaths} / {e.Assists}";
        KdaRatio = $"{g.Kda:0.00} KDA";
        SoulsText = $"{Format.Count(e.NetWorth)} souls";
        SpmText = $"{g.SoulsPerMinute:0}/min";
        Duration = e.Duration;

        if (g.Detail is { } d)
        {
            HasDetail = true;
            LevelText = d.Level is { } lvl ? lvl.ToString() : "";
            LastHitsText = d.LastHits is { } lh ? $"{lh} last hits" + (d.Denies is { } dn ? $" · {dn} denies" : "") : "";
            TeamRankText = d.TeamSize > 0 ? $"{Format.Ordinal(d.TeamSoulsRank)} of {d.TeamSize} in souls" : "";
            BadgeText = d.AverageBadge > 0 ? $"lobby {Format.Rank(d.AverageBadge)}" : "";
            foreach (var p in d.Team) Team.Add(Line(p, assets));
            foreach (var p in d.Enemies) Enemies.Add(Line(p, assets));
            Souls = d.Souls ?? Array.Empty<NetWorthPoint>();
            // Abilities share the item list in the metadata: only shop items make the timeline.
            foreach (var p in (d.Purchases ?? Array.Empty<MatchItem>()).Where(p => assets.Item(p.ItemId) is { Slot: not "ability" } || assets.Item(p.ItemId) is null))
                Purchases.Add(new PurchaseViewModel(p, assets));
            foreach (var id in d.FinalItems.Where(id => assets.Item(id) is not { Slot: "ability" })) Items.Add(new ItemChipViewModel(id, assets));
        }
        else
        {
            LevelText = LastHitsText = TeamRankText = BadgeText = "";
            Souls = Array.Empty<NetWorthPoint>();
        }
        BuildMarkers();
    }

    public long MatchId { get; }
    public string ResultText { get; }
    public Brush Edge { get; }
    /// <summary>"2 h ago".</summary>
    public string TimeAgo { get; }
    public string DurationText { get; }
    public HeroIcon Hero => _hero;
    public string LevelText { get; }
    /// <summary>"9 / 7 / 9".</summary>
    public string KdaLine { get; }
    public string KdaRatio { get; }
    public string SoulsText { get; }
    public string SpmText { get; }
    public string LastHitsText { get; }
    /// <summary>"2nd of 6 in souls".</summary>
    public string TeamRankText { get; }
    public string BadgeText { get; }
    public bool HasDetail { get; }
    /// <summary>The detail is not there yet: the API processes a match some minutes after it ends.</summary>
    public bool Pending => !HasDetail;
    public ObservableCollection<ItemChipViewModel> Items { get; } = new();
    public ObservableCollection<PlayerLineViewModel> Team { get; } = new();
    public ObservableCollection<PlayerLineViewModel> Enemies { get; } = new();
    public ObservableCollection<PurchaseViewModel> Purchases { get; } = new();
    public IReadOnlyList<NetWorthPoint> Souls { get; }
    public TimeSpan Duration { get; }
    public bool HasTimeline => Souls.Count > 1 || Purchases.Count > 0;
    public IReadOnlyList<TimelineMarker> Markers { get => _markers; private set => Set(ref _markers, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    public bool RefreshIcons(ItemIcons icons)
    {
        bool waiting = _hero.Refresh(icons);
        bool changed = false;
        foreach (var i in Items) waiting |= i.Refresh(icons);
        foreach (var p in Purchases)
        {
            bool had = p.Chip.Icon is not null;
            waiting |= p.Chip.Refresh(icons);
            changed |= !had && p.Chip.Icon is not null;
        }
        if (changed) BuildMarkers();
        return waiting;
    }

    private void BuildMarkers()
        => Markers = Purchases.Select(p => new TimelineMarker(p.At, HistoryMath.SoulsAt(Souls, p.At) ?? 0, p.Chip.Initial, p.Chip.Color, p.Chip.Fill,
            p.Chip.Icon, $"{p.TimeText}  {p.Chip.Name}" + (p.Chip.Cost > 0 ? $" ({p.Chip.Cost:N0})" : "") + (p.Sold ? $" · {p.SoldText}" : ""))).ToList();

    private static PlayerLineViewModel Line(HistoryPlayer p, AssetCatalog assets)
        => new(assets.HeroName(p.HeroId), $"{p.Kills}/{p.Deaths}/{p.Assists}", $"{p.NetWorth:N0} souls", p.IsMe);

    public static string Ago(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)} min ago"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours} h ago"
        : (int)t.TotalDays == 1 ? "1 day ago" : $"{(int)t.TotalDays} days ago";

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public sealed class HeroIcon : IconViewModel
{
    public HeroIcon(int heroId, string name) { HeroId = heroId; Name = name; }
    public int HeroId { get; }
    public string Name { get; }
    public override string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool Refresh(ItemIcons icons) { Icon ??= icons.GetHero(HeroId); return Icon is null && icons.Enabled; }
}
