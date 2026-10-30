using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.App.History;

/// <summary>
/// Your recent games: a summary row (rank over 30 days, win rate, KDA, main heroes, last 15 results) and a card per
/// game. Clicking a card opens its soul timeline, with each item bought marked on it, and the purchases in order. The
/// timeline can carry a rank gate: the average souls of a chosen rank on the game's hero, the tier above yours to start.
/// </summary>
public partial class MatchHistoryWindow : Window
{
    public const int GameCount = 20;

    private readonly MatchHistoryService? _service;
    private readonly AssetCatalog _assets;
    private readonly ItemIcons? _icons;
    private readonly uint _me;
    private readonly MatchHistoryViewModel _vm = new();
    private readonly DispatcherTimer _iconTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _iconTicks;
    private readonly Func<int, int, CancellationToken, Task<PaceCurve?>>? _goalCurves;
    private readonly GoalSettings _goal;
    private readonly Action<bool>? _saveLowRanks;
    private readonly Dictionary<(int Hero, int Tier), PaceCurve?> _goalCache = new();
    private readonly Func<int, int, CancellationToken, Task<IReadOnlyDictionary<long, ItemTiming>>>? _itemTimings;
    private readonly Dictionary<(int Hero, int Tier), IReadOnlyDictionary<long, ItemTiming>?> _timingCache = new();
    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<CounterItem>>>? _counterItems;
    private readonly Dictionary<(int Mine, int Enemy), Task<IReadOnlyList<CounterItem>?>> _counterCache = new();
    private int? _myBadge;
    private int _goalVersion;

    /// <param name="goalCurves">A rank tier's average curve on a hero (hero id, tier); null leaves the timelines without a gate.</param>
    /// <param name="saveLowRanks">Writes the Initiate and Seeker choice to settings, so the overlay's goal follows it.</param>
    /// <param name="itemTimings">A rank tier's average buy time of each item on a hero (hero id, tier), for the item timings.</param>
    /// <param name="counterItems">The counter items for your hero against an enemy hero (your hero id, theirs), for the counter notes.</param>
    public MatchHistoryWindow(MatchHistoryService? service, AssetCatalog assets, ItemIcons? icons, uint me,
                              Func<int, int, CancellationToken, Task<PaceCurve?>>? goalCurves = null, GoalSettings? goal = null, Action<bool>? saveLowRanks = null,
                              Func<int, int, CancellationToken, Task<IReadOnlyDictionary<long, ItemTiming>>>? itemTimings = null,
                              Func<int, int, CancellationToken, Task<IReadOnlyList<CounterItem>>>? counterItems = null)
    {
        _counterItems = counterItems;
        InitializeComponent();
        _service = service;
        _assets = assets;
        _icons = icons;
        _me = me;
        _goalCurves = goalCurves;
        _goal = goal ?? new GoalSettings();
        _saveLowRanks = saveLowRanks;
        _itemTimings = itemTimings;
        _vm.IncludeLowRanks = _goal.IncludeLowRanks;
        _vm.PropertyChanged += OnVmChanged;
        DataContext = _vm;
        _iconTimer.Tick += (_, _) =>
        {
            // Icons load in the background; pick them up for a minute, then stop asking.
            if (!_vm.RefreshIcons(_icons) || ++_iconTicks > 60) _iconTimer.Stop();
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _iconTimer.Stop();
    }

    /// <summary>The header and the scrolling body, for the screenshot renderer.</summary>
    public FrameworkElement HeaderPart => Header;
    public FrameworkElement BodyPart => Body;

    private async Task LoadAsync()
    {
        if (_service is null) return;
        if (_me == 0) { _vm.Status = "No Steam account found; set accountId in settings.json."; return; }
        RefreshButton.IsEnabled = false;
        _vm.Status = "Loading your match history…";
        try
        {
            var page = await _service.GetAsync(_me, GameCount, CancellationToken.None);
            ShowPage(page);
        }
        catch (Exception ex)
        {
            _vm.Status = $"Match history unavailable: {ex.Message}";
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    /// <summary>Shows a page: the live one, or the demo for screenshots.</summary>
    public void ShowPage(MatchHistoryPage page)
    {
        _vm.Load(page, _assets, _icons, DateTimeOffset.Now);
        _myBadge = page.Rank.Current;
        // Keep a rank picked by hand across a refresh; the first page starts on the tier above yours.
        _vm.SetGoalChoices(_vm.Goal?.Value ?? RankGoal.Resolve(_goal with { IncludeLowRanks = _vm.IncludeLowRanks }, _myBadge));
        _ = ApplyGoalAsync();
        if (_counterItems is not null)
        {
            // Counter items for an opened game load when it is first opened (one cached call per enemy hero); the page's
            // nemeses load now, three calls at most.
            foreach (var card in _vm.Games)
            {
                card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GameCardViewModel.IsExpanded) && card.IsExpanded) _ = LoadCountersAsync(card); };
                if (card.IsExpanded) _ = LoadCountersAsync(card);
            }
            _vm.SetNemeses(CounterNotes.Nemeses(page.Games), _assets);
            _ = LoadNemesisCountersAsync();
        }
        _iconTicks = 0;
        if (_icons is { Enabled: true }) _iconTimer.Start();
    }

    private async void Refresh(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>The counter items for each enemy hero of an opened game, marked bought or not in it.</summary>
    private async Task LoadCountersAsync(GameCardViewModel card)
    {
        if (card.CountersRequested || card.Detail is not { } detail) return;
        card.CountersRequested = true;
        var myHero = _assets.HeroName(card.HeroId);
        foreach (var row in card.Counters.ToList())
            row.SetItems(await CountersAsync(card.HeroId, row.EnemyHeroId), _assets, myHero, id => CounterNotes.Bought(detail, id));
    }

    private async Task LoadNemesisCountersAsync()
    {
        foreach (var row in _vm.Nemeses.ToList())
            row.SetItems(await CountersAsync(row.MyHeroId, row.EnemyHeroId), _assets, _assets.HeroName(row.MyHeroId), _ => null);
    }

    /// <summary>One request per hero pair for the window's life (the service caches across windows); null when it failed.</summary>
    private Task<IReadOnlyList<CounterItem>?> CountersAsync(int myHero, int enemyHero)
    {
        if (!_counterCache.TryGetValue((myHero, enemyHero), out var task))
        {
            task = Fetch();
            _counterCache[(myHero, enemyHero)] = task;
        }
        return task;

        async Task<IReadOnlyList<CounterItem>?> Fetch()
        {
            try { return await _counterItems!(myHero, enemyHero, CancellationToken.None); }
            catch (Exception) { _counterCache.Remove((myHero, enemyHero)); return null; }
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchHistoryViewModel.Goal)) _ = ApplyGoalAsync();
        else if (e.PropertyName == nameof(MatchHistoryViewModel.IncludeLowRanks))
        {
            try { _saveLowRanks?.Invoke(_vm.IncludeLowRanks); }
            catch (Exception ex) { _vm.Status = $"Could not save settings.json: {ex.Message}"; }
            // The same rank stays picked when it is still on the list; a hidden low rank moves up to Alchemist.
            int tier = _vm.Goal?.Value ?? 0;
            _vm.SetGoalChoices(tier == 0 || tier >= RankGoal.Lowest(_vm.IncludeLowRanks) ? tier : RankGoal.Lowest(false));
        }
    }

    /// <summary>
    /// Puts the picked rank's curve on every game's timeline, one curve per hero. The curves come from the analytics
    /// benchmark, which answers every hero of a rank from one cached set of calls, so changing rank costs little.
    /// </summary>
    private async Task ApplyGoalAsync()
    {
        int version = ++_goalVersion;
        int tier = _vm.Goal?.Value ?? 0;
        string rank = tier > 0 && _goalCurves is not null ? RankGoal.Name(tier) : "";
        foreach (var card in _vm.Games.ToList())
        {
            if (rank.Length == 0) { card.SetGoal(null, ""); card.SetItemTimings(null, ""); continue; }
            (PaceCurve? Curve, string? Error) goal = (null, null);
            if (card.Souls.Count >= 2)
            {
                if (!_goalCache.ContainsKey((card.HeroId, tier))) card.SetGoalLoading(rank);
                goal = await GoalCurveAsync(card.HeroId, tier);
            }
            var timings = card.Purchases.Count == 0 ? null : await ItemTimingsAsync(card.HeroId, tier);
            if (version != _goalVersion) return; // another rank was picked meanwhile
            if (goal.Error is { } error) card.SetGoalError(rank, error);
            else card.SetGoal(card.Souls.Count < 2 ? null : goal.Curve, card.Souls.Count < 2 ? "" : rank);
            card.SetItemTimings(timings, rank);
        }
        _vm.RefreshPhases(rank);
        _vm.RefreshItemTimings(rank);
        if (_icons is { Enabled: true }) { _iconTicks = 0; _iconTimer.Start(); } // the timing rows' item icons
    }

    private async Task<IReadOnlyDictionary<long, ItemTiming>?> ItemTimingsAsync(int heroId, int tier)
    {
        if (_itemTimings is null) return null;
        if (_timingCache.TryGetValue((heroId, tier), out var cached)) return cached;
        IReadOnlyDictionary<long, ItemTiming>? table = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            table = await _itemTimings(heroId, tier, cts.Token);
        }
        catch (Exception) { } // no timings for this hero: its purchases just show their own times
        _timingCache[(heroId, tier)] = table;
        return table;
    }

    /// <summary>
    /// The rank's curve on the hero, kept for the window's life once it has loaded (or the API has none). A failure (a
    /// timeout behind the rate limiter, an API error) is not kept: the card shows why, and picking the rank again retries.
    /// </summary>
    private async Task<(PaceCurve? Curve, string? Error)> GoalCurveAsync(int heroId, int tier)
    {
        if (_goalCache.TryGetValue((heroId, tier), out var cached)) return (cached, null);
        try
        {
            using var cts = new CancellationTokenSource(GoalTimeout);
            var curve = await _goalCurves!(heroId, tier, cts.Token);
            _goalCache[(heroId, tier)] = curve;
            return (curve, null);
        }
        catch (OperationCanceledException) { return (null, $"no answer in {GoalTimeout.TotalSeconds:0} s (the API may be busy)"); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>A rank's curve is 8 analytics calls the first time (then cached for the patch), behind the per-minute limit.</summary>
    private static readonly TimeSpan GoalTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Clicks on the rank picker stay there instead of opening or closing the card around it.</summary>
    private void KeepCard(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ToggleCard(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameCardViewModel card }) card.IsExpanded = !card.IsExpanded;
    }

    /// <summary>
    /// The page the screenshots show: made-up games with real hero and item ids, so names and colours resolve from the
    /// assets (screenshots never show the icons themselves). The first game is opened with its timelines.
    /// </summary>
    /// <summary>
    /// A made-up rank curve for the screenshots: higher tiers earn faster, and the demo Haze game (Archon) sits a little
    /// above the Oracle line. No network.
    /// </summary>
    public static PaceCurve DemoGoalCurve(int heroId, int tier)
    {
        var points = Enumerable.Range(0, 11).Select(i =>
        {
            int m = i * 5;
            return new PacePoint(m, null, null, null, (int)((380 + 42 * tier) * m + 3.2 * m * m));
        }).ToList();
        return new PaceCurve(heroId, RankGoal.Bracket(tier), "demo", points, 900, "analytics");
    }

    /// <summary>When the demo player usually buys an item (the first card's build times, or a steady pace for the others).</summary>
    private static double DemoBuyMinute(long item, int order) => item switch
    {
        1009965641 => 0.4, 668299740 => 2.8, 4053935515 => 14.2, 3585132399 => 17.0, 865846625 => 23.4,
        _ => 6 + order * 5.5,
    };

    /// <summary>
    /// Made-up rank buy times for the screenshots: the demo build's times nudged earlier for higher tiers, some items more
    /// than others, so the comparison has early and late items. No network.
    /// </summary>
    public static IReadOnlyDictionary<long, ItemTiming> DemoItemTimings(int heroId, int tier)
    {
        long[] items =
        {
            1009965641, 2829638276, 668299740, 3077079169, 1548066885, 2064029594, 876563814, 4053935515, 3585132399, 7409189,
            865846625, 3884003354, 968099481, 754480263, 3577481646, 1235347618,
        };
        double[] minutes = { 0.4, 0.4, 2.6, 5.4, 6.6, 9.0, 12.4, 12.5, 17.8, 18.6, 21.9, 27.0, 6.4, 11.0, 15.8, 22.0 };
        double scale = 1 - 0.03 * (tier - 7);
        return items.Select((id, i) => new ItemTiming(id, 400 + 37 * i, minutes[i] * 60 * scale)).ToDictionary(t => t.ItemId);
    }

    /// <summary>
    /// A made-up soul series ending on <paramref name="total"/>: a fair lane, a slow mid game, a strong finish, so the
    /// demo's phases card has a weakest phase to point at. <paramref name="seed"/> varies the shape a little per game.
    /// </summary>
    private static IReadOnlyList<NetWorthPoint> DemoSouls(int total, int minutes, int seed)
    {
        double Rate(double m) => (m < 10 ? 1.0 + 0.04 * (seed % 3) : m < 20 ? 0.78 + 0.03 * (seed % 4) : 1.05) * (1 + m / 60);
        var raw = new List<(double T, double Nw)> { (0, 0) };
        for (int i = 1; i <= minutes * 2; i++) raw.Add((i * 30, raw[^1].Nw + Rate(i / 2.0)));
        double scale = total / raw[^1].Nw;
        return raw.Select(p => new NetWorthPoint(TimeSpan.FromSeconds(p.T), (int)(p.Nw * scale))).ToList();
    }

    /// <summary>
    /// Made-up counter items for the screenshots: real item ids so names and colours resolve, made-up lifts and game counts.
    /// Leech against Paradox is one the demo build bought; several heroes have none, as most do.
    /// </summary>
    public static IReadOnlyList<CounterItem> DemoCounters(int myHero, int enemyHero)
    {
        const long inhibitor = 2037039379, silencer = 3133167885, frenzy = 339443430, healbane = 2603935618, leech = 865846625, spiritResilience = 2163598980;
        (long Item, double Lift, int Games)[] items = enemyHero switch
        {
            2 => new[] { (inhibitor, 0.013, 44180), (silencer, 0.006, 133722) },
            10 => new[] { (leech, 0.017, 21400) },
            12 => new[] { (healbane, 0.021, 9120) },
            20 => new[] { (frenzy, 0.033, 5737), (spiritResilience, 0.009, 18210) },
            18 => new[] { (healbane, 0.015, 7400) },
            _ => Array.Empty<(long, double, int)>(),
        };
        return items.Select(i => new CounterItem(i.Item, enemyHero, i.Lift, 4.0, i.Games)).ToList();
    }

    public static MatchHistoryPage DemoPage(DateTimeOffset now)
    {
        const uint me = 1000;
        // Haze's build in buy order: (item, minute bought, minute sold or 0).
        (long Id, double At, double Sold)[] build =
        {
            (1009965641, 0.4, 0), (2829638276, 0.4, 19.5), (668299740, 2.8, 0), (3077079169, 5.1, 0), (1548066885, 7.0, 0),
            (2064029594, 9.3, 0), (876563814, 11.6, 0), (4053935515, 14.2, 0), (3585132399, 17.0, 0), (7409189, 19.5, 0),
            (865846625, 23.4, 0), (3884003354, 28.8, 0),
        };
        var duration = new TimeSpan(0, 34, 12);
        var souls = Enumerable.Range(0, (int)duration.TotalSeconds / 30 + 1).Select(i =>
        {
            double t = i * 30;
            // A strong lane, a slower stretch around 15 minutes after a lost fight, then a late surge.
            double nw = 410 * t / 60 + 0.11 * Math.Pow(t / 60, 2) * 60 - 900 * Math.Exp(-Math.Pow((t / 60 - 16) / 3, 2)) + 40 * Math.Sin(t / 70);
            return new NetWorthPoint(TimeSpan.FromSeconds(t), (int)Math.Max(0, nw));
        }).ToList();
        double scale = 31240.0 / souls[^1].NetWorth; // end on the card's 31.2k
        souls = souls.Select(p => p with { NetWorth = (int)(p.NetWorth * scale) }).ToList();

        HistoryPlayer P(uint id, int hero, int k, int d, int a, int nw) => new(id, hero, k, d, a, nw, id == me);
        var team = new[] { P(1001, 1, 9, 5, 12, 33100), P(me, 13, 8, 3, 11, 31240), P(1002, 6, 3, 6, 15, 26400), P(1003, 7, 6, 4, 9, 25800),
                           P(1004, 11, 4, 7, 14, 23100), P(1005, 19, 2, 5, 18, 20900) };
        var enemies = new[] { P(1006, 2, 7, 6, 6, 30400), P(1007, 3, 5, 7, 8, 27900), P(1008, 4, 6, 5, 7, 26100), P(1009, 10, 4, 8, 6, 24300),
                              P(1010, 12, 3, 6, 10, 22000), P(1011, 14, 1, 6, 9, 19800) };
        var first = new MatchDetail(1, 182, 14, 31, build.Where(b => b.Sold == 0).Select(b => b.Id).ToList(), 2, 6, 74, team, enemies, souls,
            build.Select(b => new MatchItem(b.Id, TimeSpan.FromMinutes(b.At), b.Sold > 0 ? TimeSpan.FromMinutes(b.Sold) : null)).ToList());

        // The rest: (hero, won, k, d, a, souls, minutes, hours ago, badge after).
        (int Hero, bool Won, int K, int D, int A, int Nw, int Min, double Ago, int Badge)[] rest =
        {
            (13, false, 5, 7, 8, 24100, 31, 1.4, 73), (1, true, 11, 2, 9, 29800, 28, 2.1, 74), (13, true, 9, 1, 6, 27300, 27, 26, 73),
            (13, false, 4, 6, 10, 21800, 33, 27, 72), (7, true, 7, 4, 13, 26500, 30, 29, 73), (13, true, 10, 3, 7, 30100, 32, 50, 72),
            (1, false, 6, 8, 5, 22400, 36, 52, 71), (13, true, 8, 4, 12, 28700, 29, 75, 72), (13, true, 12, 5, 4, 32600, 35, 98, 71),
        };
        var games = new List<HistoryGame>
        {
            new(new MatchHistoryEntry(100, 13, now.AddMinutes(-34 - 25), duration, Teams.Amber, 8, 3, 11, 31240, 1, 74, null, Teams.Amber), first),
        };
        long id = 99;
        foreach (var r in rest)
        {
            var start = now.AddHours(-r.Ago).AddMinutes(-r.Min);
            var entry = new MatchHistoryEntry(id--, r.Hero, start, TimeSpan.FromMinutes(r.Min), Teams.Sapphire, r.K, r.D, r.A, r.Nw, r.Won ? 1 : 2, r.Badge, null,
                r.Won ? Teams.Sapphire : Teams.Amber);
            var held = r.Hero == 13 ? new long[] { 1009965641, 668299740, 4053935515, 3585132399, 865846625 } : new long[] { 968099481, 754480263, 3577481646, 1235347618 };
            // The held items in buy order, a little later or earlier each game, so the timings card has a spread.
            double drift = 1 + 0.04 * ((int)(id % 5) - 1);
            var bought = held.Select((item, i) => new MatchItem(item, TimeSpan.FromMinutes(Math.Min(r.Min - 1, DemoBuyMinute(item, i) * drift)), null)).ToList();
            // Ivy was in every game lost (and Mo & Krill in some), so the page has heroes that beat you.
            var lineup = r.Won ? enemies : enemies.Select(p => p.HeroId == 14 ? p with { HeroId = 20 } : p.HeroId == 12 && id % 2 == 0 ? p with { HeroId = 18 } : p).ToArray();
            games.Add(new HistoryGame(entry, new MatchDetail(entry.MatchId, 150 + r.K * 4, 9, 29, held, 1 + (int)(id % 4), 6, r.Badge, team, lineup,
                DemoSouls(r.Nw, r.Min, (int)id), bought)));
        }
        var entries = games.Select(g => g.Entry).ToList();

        // Older games for the trends, only as history rows (no cards): a slow climb in souls per minute and a win rate
        // that starts near even. Fixed numbers, no randomness, so the screenshot is the same every time.
        var history = entries.ToList();
        int[] heroes = { 13, 13, 1, 13, 7, 13, 1, 13, 6, 13 };
        for (int i = 0; i < 40; i++)
        {
            int hero = heroes[i % heroes.Length];
            int min = 27 + (i * 7) % 11;
            double spm = 820 - i * 6 + (i * 37 % 13 - 6) * 14; // newer games (low i) earn more
            bool won = (i * 5 + i / 3) % 9 < (i < 20 ? 5 : 4);
            int k = 3 + (i * 3) % 9, d = 2 + (i * 5) % 7, a = 4 + (i * 7) % 11;
            var start = now.AddHours(-120 - i * 9.5).AddMinutes(-min);
            history.Add(new MatchHistoryEntry(id--, hero, start, TimeSpan.FromMinutes(min), Teams.Sapphire, k, d, a, (int)(spm * min), won ? 1 : 2, 71 - i / 10, null,
                won ? Teams.Sapphire : Teams.Amber));
        }
        return new MatchHistoryPage(HistoryMath.Summarize(entries), HistoryMath.Rank(entries, now - MatchHistoryService.RankWindow), games, history);
    }
}
