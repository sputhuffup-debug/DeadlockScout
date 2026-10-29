using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.App.History;

/// <summary>
/// Your recent games: a summary row (rank over 30 days, win rate, KDA, main heroes, last 15 results) and a card per
/// game. Clicking a card opens its soul timeline, with each item bought marked on it, and the purchases in order.
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

    public MatchHistoryWindow(MatchHistoryService? service, AssetCatalog assets, ItemIcons? icons, uint me)
    {
        InitializeComponent();
        _service = service;
        _assets = assets;
        _icons = icons;
        _me = me;
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
        _iconTicks = 0;
        if (_icons is { Enabled: true }) _iconTimer.Start();
    }

    private async void Refresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private void ToggleCard(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameCardViewModel card }) card.IsExpanded = !card.IsExpanded;
    }

    /// <summary>
    /// The page the screenshots show: made-up games with real hero and item ids, so names and colours resolve from the
    /// assets (screenshots never show the icons themselves). The first game is opened with its timelines.
    /// </summary>
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
            games.Add(new HistoryGame(entry, new MatchDetail(entry.MatchId, 150 + r.K * 4, 9, 29, held, 1 + (int)(id % 4), 6, r.Badge, team, enemies)));
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
