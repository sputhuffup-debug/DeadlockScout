using System.Collections.ObjectModel;
using System.Windows.Media;
using DeadlockScout.App.Controls;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
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
    public TrendsViewModel Trends { get; } = new();

    /// <summary>The ranks the soul timelines can be compared with, highest first, then "Off".</summary>
    public ObservableCollection<TrendOption> GoalChoices { get; } = new();
    private TrendOption? _goal;
    private bool _lowRanks;
    /// <summary>The rank whose average souls each timeline shows as its gate; Value 0 is off.</summary>
    public TrendOption? Goal { get => _goal; set => Set(ref _goal, value); }
    /// <summary>Initiate and Seeker on the list (and for the overlay's goal); off keeps Alchemist the lowest.</summary>
    public bool IncludeLowRanks { get => _lowRanks; set => Set(ref _lowRanks, value); }

    private string _phaseTitle = "", _phaseVerdict = "";
    private bool _hasPhases;
    /// <summary>"PHASES AGAINST ORACLE · 9 GAMES".</summary>
    public string PhaseTitle { get => _phaseTitle; private set => Set(ref _phaseTitle, value); }
    /// <summary>"Mid game costs you the most: 1.2k souls a game behind Oracle."</summary>
    public string PhaseVerdict { get => _phaseVerdict; private set => Set(ref _phaseVerdict, value); }
    public bool HasPhases { get => _hasPhases; private set => Set(ref _hasPhases, value); }
    public ObservableCollection<PhaseCardViewModel> PhaseCards { get; } = new();

    private string _timingTitle = "", _timingVerdict = "";
    private bool _hasTimings;
    private AssetCatalog? _assets;
    /// <summary>"ITEM TIMINGS AGAINST ORACLE · 9 GAMES".</summary>
    public string TimingTitle { get => _timingTitle; private set => Set(ref _timingTitle, value); }
    /// <summary>"Furthest behind: Headhunter, 1:42 later than Oracle."</summary>
    public string TimingVerdict { get => _timingVerdict; private set => Set(ref _timingVerdict, value); }
    public bool HasTimings { get => _hasTimings; private set => Set(ref _hasTimings, value); }
    public ObservableCollection<ItemTimingRowViewModel> TimingRows { get; } = new();

    // Heroes that beat you: the enemy heroes with your worst records over the page, each with their counter items.
    private string _nemesesTitle = "";
    private bool _hasNemeses;
    public string NemesesTitle { get => _nemesesTitle; private set => Set(ref _nemesesTitle, value); }
    public bool HasNemeses { get => _hasNemeses; private set => Set(ref _hasNemeses, value); }
    public ObservableCollection<CounterRowViewModel> Nemeses { get; } = new();

    /// <summary>The page's nemeses, without their counter items yet (those load per row).</summary>
    public void SetNemeses(IReadOnlyList<Nemesis> nemeses, AssetCatalog assets)
    {
        Nemeses.Clear();
        foreach (var n in nemeses)
            Nemeses.Add(new CounterRowViewModel(n.EnemyHeroId, assets.HeroName(n.EnemyHeroId),
                $"you {n.Wins}–{n.Losses} against them · counter items for {assets.HeroName(n.MyHeroId)}", n.MyHeroId));
        NemesesTitle = $"HEROES THAT BEAT YOU · LAST {Games.Count} GAMES";
        HasNemeses = Nemeses.Count > 0;
    }
    /// <summary>Rows the item timings card shows: the items you buy most.</summary>
    public const int TimingRowCount = 8;

    /// <summary>Pools every game's item timings against <paramref name="rank"/>; clears the card when there are none.</summary>
    public void RefreshItemTimings(string rank)
    {
        var games = Games.Select(g => g.TimingResults).Where(r => r.Count > 0).ToList();
        TimingRows.Clear();
        if (rank.Length == 0 || games.Count == 0 || _assets is not { } assets) { HasTimings = false; TimingVerdict = ""; return; }
        var totals = ItemTimings.Total(games);
        foreach (var t in totals.Take(TimingRowCount)) TimingRows.Add(new ItemTimingRowViewModel(t, assets));
        TimingTitle = $"ITEM TIMINGS AGAINST {rank.ToUpperInvariant()} · {games.Count} {(games.Count == 1 ? "GAME" : "GAMES")}";
        TimingVerdict = ItemTimings.Latest(totals) is { } late
            ? $"Furthest behind: {assets.ItemName(late.ItemId)}, {Format.Clock(late.Difference)} later than {rank} over {late.Games} games."
            : $"No item you buy often is more than {ItemTimings.OnTime.TotalSeconds:0} s behind {rank}.";
        HasTimings = true;
    }

    /// <summary>Pools every game's phases against <paramref name="rank"/>; clears the card when there are none.</summary>
    public void RefreshPhases(string rank)
    {
        var games = Games.Select(g => g.PhaseResults).Where(p => p.Count > 0).ToList();
        PhaseCards.Clear();
        if (rank.Length == 0 || games.Count == 0) { HasPhases = false; PhaseVerdict = ""; return; }
        var totals = Phases.Total(games);
        var weakest = Phases.Weakest(totals);
        foreach (var t in totals) PhaseCards.Add(new PhaseCardViewModel(t, rank, ReferenceEquals(t, weakest)));
        PhaseTitle = $"PHASES AGAINST {rank.ToUpperInvariant()} · {games.Count} {(games.Count == 1 ? "GAME" : "GAMES")}";
        PhaseVerdict = weakest is { } w
            ? $"{Phases.Name(w.Phase)} costs you the most: {Format.Count((int)Math.Round(w.ShortfallPerGame))} souls a game behind {rank}."
            : totals.All(t => t.Delta is >= 0)
                ? $"Ahead of {rank} in every phase: try a higher rank gate."
                : $"Not enough games behind {rank} in one phase to call it yet.";
        HasPhases = true;
    }

    /// <summary>Fills the rank list for <see cref="IncludeLowRanks"/> and selects <paramref name="tier"/> (0 for off).</summary>
    public void SetGoalChoices(int tier)
    {
        GoalChoices.Clear();
        foreach (var t in RankGoal.Choices(_lowRanks)) GoalChoices.Add(new TrendOption(RankGoal.Name(t), t));
        GoalChoices.Add(new TrendOption("Off", 0));
        Goal = GoalChoices.FirstOrDefault(c => c.Value == tier) ?? GoalChoices[^1];
    }

    public void Load(MatchHistoryPage page, AssetCatalog assets, ItemIcons? icons, DateTimeOffset now)
    {
        _assets = assets;
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

        Trends.Load(page.History ?? page.Games.Select(g => g.Entry).ToList(), assets, icons, now);
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
        waiting |= Trends.RefreshIcons(icons);
        foreach (var r in TimingRows) waiting |= r.Chip.Refresh(icons);
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
public sealed class PurchaseViewModel : ObservableObject
{
    private string _rankText = "";
    private double? _gap;

    public PurchaseViewModel(MatchItem p, AssetCatalog assets)
    {
        Item = p;
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
    public MatchItem Item { get; }
    /// <summary>"Oracle 12:30 · +1:42": the rank's average buy time and your gap to it; empty when not compared.</summary>
    public string RankText { get => _rankText; private set => Set(ref _rankText, value); }
    /// <summary>The gap in seconds, positive when you were later, for its colour.</summary>
    public double? Gap { get => _gap; private set => Set(ref _gap, value); }

    public void SetTiming(ItemTimingResult? r, string rank)
    {
        RankText = r is null ? "" : $"{rank} {Format.Clock(r.Rank)} · {Format.SignedClock(r.Difference)}";
        Gap = r?.Difference.TotalSeconds;
    }
}

/// <summary>One item in the item timings card: your average buy time over the page's games against the rank's.</summary>
public sealed class ItemTimingRowViewModel
{
    public ItemTimingRowViewModel(ItemTimingTotal t, AssetCatalog assets)
    {
        Chip = new ItemChipViewModel(t.ItemId, assets);
        Games = t.Games.ToString();
        You = Format.Clock(t.You);
        Rank = Format.Clock(t.Rank);
        GapText = Format.SignedClock(t.Difference);
        Gap = t.Difference.TotalSeconds;
    }
    public ItemChipViewModel Chip { get; }
    public string Games { get; }
    public string You { get; }
    public string Rank { get; }
    public string GapText { get; }
    public double Gap { get; }
}

/// <summary>A player in a card's team columns: hero and "5/2/7", you in bold; their souls on hover.</summary>
public sealed record PlayerLineViewModel(string Hero, string Kda, string Souls, bool IsMe);

/// <summary>One counter item on a row: its chip, how many points it adds and, in a game, whether you bought it.</summary>
public sealed class CounterItemLineViewModel
{
    public CounterItemLineViewModel(CounterItem c, AssetCatalog assets, bool? bought, string myHero, string enemyHero)
    {
        Chip = new ItemChipViewModel(c.ItemId, assets);
        LiftText = $"+{c.Lift * 100:0.0}";
        BoughtText = bought switch { true => "bought", false => "not bought", null => "" };
        Bought = bought;
        Tooltip = $"{Chip.Name}: {myHero} players win {c.Lift * 100:0.0} points more often with it when {enemyHero} is on the other team " +
                  $"than they do with it otherwise ({c.Matches:N0} games, every rank, last 30 days; the matchup's own edge taken out)";
    }

    public ItemChipViewModel Chip { get; }
    public string LiftText { get; }
    public string BoughtText { get; }
    /// <summary>True bought, false not bought, null on the page card (no single game).</summary>
    public bool? Bought { get; }
    public string Tooltip { get; }
}

/// <summary>An enemy hero and its counter items: on an opened game (bought or not) or on the page's "heroes that beat you".</summary>
public sealed class CounterRowViewModel : ObservableObject
{
    private string _status = "loading…";

    public CounterRowViewModel(int enemyHeroId, string heroName, string line = "", int myHeroId = 0)
    {
        EnemyHeroId = enemyHeroId;
        HeroName = heroName;
        Line = line;
        MyHeroId = myHeroId;
    }

    public int EnemyHeroId { get; }
    public int MyHeroId { get; }
    public string HeroName { get; }
    /// <summary>The page card's "you 1–4 against them · counter items for Haze"; empty on a game.</summary>
    public string Line { get; }
    public ObservableCollection<CounterItemLineViewModel> Items { get; } = new();
    /// <summary>"loading…", "no clear counter item" or empty once items show.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public void SetItems(IReadOnlyList<CounterItem>? items, AssetCatalog assets, string myHero, Func<long, bool?> bought, int max = 3)
    {
        Items.Clear();
        if (items is null) { Status = "counter items unavailable"; return; }
        foreach (var c in items.Take(max)) Items.Add(new CounterItemLineViewModel(c, assets, bought(c.ItemId), myHero, HeroName));
        Status = Items.Count == 0 ? "no clear counter item" : "";
    }
}

public sealed class GameCardViewModel : ObservableObject
{
    private static readonly Brush WinEdge = Frozen(Color.FromRgb(0x7F, 0xE0, 0x8A));
    private static readonly Brush LossEdge = Frozen(Color.FromRgb(0xF0, 0x7A, 0x6E));
    private static readonly Brush OtherEdge = Frozen(Color.FromRgb(0x5A, 0x63, 0x70));

    private readonly HeroIcon _hero;
    private readonly int _netWorth;
    private bool _expanded;
    private IReadOnlyList<NetWorthPoint> _expected = Array.Empty<NetWorthPoint>();
    private string _expectedLabel = "", _goalText = "";
    private double? _goalDelta;
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
        SoulsShort = Format.Count(e.NetWorth);
        _netWorth = e.NetWorth;
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
            Souls = HistoryMath.FromZero(d.Souls ?? Array.Empty<NetWorthPoint>()); // details cached before the parser added 0:00 lack it
            // Abilities share the item list in the metadata: only shop items make the timeline.
            foreach (var p in (d.Purchases ?? Array.Empty<MatchItem>()).Where(p => assets.Item(p.ItemId) is { Slot: not "ability" } || assets.Item(p.ItemId) is null))
                Purchases.Add(new PurchaseViewModel(p, assets));
            foreach (var id in d.FinalItems.Where(id => assets.Item(id) is not { Slot: "ability" })) Items.Add(new ItemChipViewModel(id, assets));
            Detail = d;
            foreach (var enemy in d.Enemies.Where(p => p.HeroId > 0).Select(p => p.HeroId).Distinct())
                Counters.Add(new CounterRowViewModel(enemy, assets.HeroName(enemy)));
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
    /// <summary>"31.2k": the souls beside the K/D/A.</summary>
    public string SoulsShort { get; }
    public int HeroId => _hero.HeroId;
    /// <summary>The chosen rank's average souls on this hero through the match, drawn dashed on the timeline.</summary>
    public IReadOnlyList<NetWorthPoint> Expected { get => _expected; private set => Set(ref _expected, value); }
    /// <summary>"Oracle avg" at the end of that line.</summary>
    public string ExpectedLabel { get => _expectedLabel; private set => Set(ref _expectedLabel, value); }
    /// <summary>"Oracle gate on Haze: 28.6k by 34:12 (836/min) · you 31.2k, +9%".</summary>
    public string GoalText { get => _goalText; private set => Set(ref _goalText, value); }
    /// <summary>You against the gate at the end of the match: +0.09 is 9% ahead.</summary>
    public double? GoalDelta { get => _goalDelta; private set => Set(ref _goalDelta, value); }

    private string _itemTimingText = "";
    private double? _itemTimingGap;
    /// <summary>Your first purchases against the rank's average buy times; empty without them.</summary>
    public IReadOnlyList<ItemTimingResult> TimingResults { get; private set; } = Array.Empty<ItemTimingResult>();
    /// <summary>"Items against Oracle: 0:35 later on average over 9 items".</summary>
    public string ItemTimingText { get => _itemTimingText; private set => Set(ref _itemTimingText, value); }
    public double? ItemTimingGap { get => _itemTimingGap; private set => Set(ref _itemTimingGap, value); }

    /// <summary>Compares this game's purchases with <paramref name="table"/> (the rank's average buy times on this hero); null clears it.</summary>
    public void SetItemTimings(IReadOnlyDictionary<long, ItemTiming>? table, string rank)
    {
        TimingResults = table is null || rank.Length == 0 ? Array.Empty<ItemTimingResult>() : ItemTimings.Compare(Purchases.Select(p => p.Item), table);
        foreach (var p in Purchases)
            p.SetTiming(TimingResults.FirstOrDefault(r => r.ItemId == p.Item.ItemId && r.You == p.Item.BoughtAt), rank);
        var gap = ItemTimings.AverageGap(TimingResults);
        ItemTimingGap = gap?.TotalSeconds;
        ItemTimingText = gap is not { } g ? "" : Math.Abs(g.TotalSeconds) <= ItemTimings.OnTime.TotalSeconds
            ? $"Items against {rank}: on time on average over {TimingResults.Count} items"
            : $"Items against {rank}: {Format.Clock(g.Duration())} {(g > TimeSpan.Zero ? "later" : "earlier")} on average over {TimingResults.Count} items";
    }

    /// <summary>Laning, mid and late game against the gate; empty without one.</summary>
    public IReadOnlyList<PhaseResult> PhaseResults { get; private set; } = Array.Empty<PhaseResult>();
    public ObservableCollection<PhaseChipViewModel> PhaseChips { get; } = new();

    /// <summary>While the rank's curve loads: no line yet, and a line saying so.</summary>
    public void SetGoalLoading(string rank)
    {
        Expected = Array.Empty<NetWorthPoint>();
        ExpectedLabel = "";
        GoalDelta = null;
        GoalText = $"Loading the {rank} average for {_hero.Name}…";
    }

    /// <summary>The rank's curve could not be loaded: why, so the cause shows instead of a bare "no average".</summary>
    public void SetGoalError(string rank, string reason)
    {
        SetGoal(null, "");
        GoalText = $"Could not load the {rank} average for {_hero.Name}: {reason}. Pick the rank again to retry.";
    }

    /// <summary>Shows <paramref name="curve"/> (the average of <paramref name="rank"/> on this hero) as the gate; null clears it.</summary>
    public void SetGoal(PaceCurve? curve, string rank)
    {
        PhaseChips.Clear();
        PhaseResults = curve is null || rank.Length == 0 ? Array.Empty<PhaseResult>() : Phases.Of(Souls, Duration, curve);
        foreach (var p in PhaseResults) PhaseChips.Add(new PhaseChipViewModel(p, rank));
        if (curve is null || rank.Length == 0)
        {
            Expected = Array.Empty<NetWorthPoint>();
            ExpectedLabel = "";
            GoalText = rank.Length > 0 ? $"No {rank} average for {_hero.Name} yet." : "";
            GoalDelta = null;
            return;
        }
        Expected = RankGoal.Line(curve, Duration);
        ExpectedLabel = $"{rank} avg";
        int end = Expected[^1].NetWorth;
        double spm = Duration.TotalMinutes > 0 ? end / Duration.TotalMinutes : 0;
        GoalDelta = end > 0 ? (double)_netWorth / end - 1 : null;
        GoalText = $"{rank} gate on {_hero.Name}: {Format.Count(end)} by {DurationText} ({spm:0}/min) · you {SoulsShort}" +
                   (GoalDelta is { } d ? $", {Format.Signed(d)}" : "");
    }
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
    /// <summary>The processed lobby, for whether a counter item was bought; null while pending.</summary>
    public MatchDetail? Detail { get; }
    /// <summary>One row per enemy hero; their counter items load when the game is first opened.</summary>
    public ObservableCollection<CounterRowViewModel> Counters { get; } = new();
    public bool HasCounters => Counters.Count > 0;
    public bool CountersRequested { get; set; }

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

/// <summary>One phase of one game on its card: "MID GAME −9%", coloured by how it went.</summary>
public sealed class PhaseChipViewModel
{
    public PhaseChipViewModel(PhaseResult p, string rank)
    {
        Label = Phases.Name(p.Phase).ToUpperInvariant();
        Delta = p.Delta;
        Value = p.Delta is { } d ? Format.Signed(d) : "–";
        Tooltip = $"{Phases.Name(p.Phase)} ({Format.Clock(p.From)}–{Format.Clock(p.To)}): you {p.Spm:0}/min, {rank} {p.ExpectedSpm:0}/min · " +
                  $"{Format.SignedCount((int)Math.Round(p.Gained - p.Expected))} souls";
    }
    public string Label { get; }
    public string Value { get; }
    public double? Delta { get; }
    public string Tooltip { get; }
}

/// <summary>One phase over the page's games, for the phases card; the weakest one is outlined.</summary>
public sealed class PhaseCardViewModel
{
    public PhaseCardViewModel(PhaseTotal t, string rank, bool weakest)
    {
        Name = Phases.Name(t.Phase).ToUpperInvariant();
        Span = Phases.Span(t.Phase);
        Delta = t.Delta;
        DeltaText = t.Delta is { } d ? Format.Signed(d) : "–";
        Rates = $"you {t.Spm:0}/min · {rank} {t.ExpectedSpm:0}/min";
        int perGame = (int)Math.Round(t.ShortfallPerGame);
        Souls = perGame > 0 ? $"{Format.Count(perGame)} souls a game behind" : $"{Format.Count(-perGame)} souls a game ahead";
        Games = $"{t.Games} {(t.Games == 1 ? "game" : "games")}";
        IsWeakest = weakest;
    }
    public string Name { get; }
    public string Span { get; }
    public double? Delta { get; }
    public string DeltaText { get; }
    public string Rates { get; }
    public string Souls { get; }
    public string Games { get; }
    public bool IsWeakest { get; }
}
