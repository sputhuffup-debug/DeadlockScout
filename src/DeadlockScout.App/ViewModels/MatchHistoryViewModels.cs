using System.Collections.ObjectModel;
using System.Windows.Media;
using DeadlockScout.App.Controls;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeadlockScout.App.ViewModels;

/// <summary>
/// The match history window: a summary row over your recent games and one card per game, each with a soul timeline
/// and its item purchases. Built from a <see cref="MatchHistoryPage"/>; everything shown is derived here, nothing stored.
/// </summary>
public sealed class MatchHistoryViewModel : ObservableObject
{
    private static Brush WinBrush => Themes.Palette.Good;
    private static Brush LossBrush => Themes.Palette.Bad;
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
    /// <summary>Lanes won and lost per hero over the page (0.7).</summary>
    public ObservableCollection<HeroUsageViewModel> LaneRows { get; } = new();
    private bool _hasLaneRows;
    private string _laneTitle = "";
    public bool HasLaneRows { get => _hasLaneRows; private set => Set(ref _hasLaneRows, value); }
    public string LaneTitle { get => _laneTitle; private set => Set(ref _laneTitle, value); }
    /// <summary>The last 15 results, newest first, as coloured bars.</summary>
    public ObservableCollection<Brush> LastResults { get; } = new();
    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    /// <summary>Matches you played that the API has not stored yet, above the games; none of the numbers count them.</summary>
    public ObservableCollection<ProcessingCardViewModel> Processing { get; } = new();
    public TrendsViewModel Trends { get; } = new();
    /// <summary>The marks made with the mark hotkey for a match id (0.6); none by default.</summary>
    public Func<long, IReadOnlyList<TimeSpan>> Marks { get; set; } = _ => Array.Empty<TimeSpan>();

    // The filter bar: one hero, ranked only, or the games with one friend. Value 0 = all.
    public ObservableCollection<TrendOption> HeroFilters { get; } = new();
    public ObservableCollection<TrendOption> FriendFilters { get; } = new();
    public TrendOption? HeroFilter { get => _heroFilter; set => Set(ref _heroFilter, value); }
    public TrendOption? FriendFilter { get => _friendFilter; set => Set(ref _friendFilter, value); }
    public bool RankedOnly { get => _rankedOnly; set => Set(ref _rankedOnly, value); }
    public bool HasFriendFilters { get => _hasFriendFilters; private set => Set(ref _hasFriendFilters, value); }
    private TrendOption? _heroFilter, _friendFilter;
    private bool _rankedOnly, _hasFriendFilters;

    /// <summary>
    /// Fills the filter boxes from the whole page (every hero in it, most played first) and the friends of the teammate
    /// record, keeping what was picked when it is still there.
    /// </summary>
    public void SetFilterChoices(MatchHistoryPage page, IReadOnlyList<Core.Session.TeammateRow> friends, AssetCatalog assets)
    {
        int hero = HeroFilter?.Value ?? 0, friend = FriendFilter?.Value ?? 0;
        HeroFilters.Clear();
        HeroFilters.Add(new TrendOption("All heroes", 0));
        foreach (var g in page.Games.GroupBy(g => g.Entry.HeroId).OrderByDescending(g => g.Count()).ThenBy(g => assets.HeroName(g.Key)))
            HeroFilters.Add(new TrendOption($"{assets.HeroName(g.Key)} ({g.Count()})", g.Key));
        FriendFilters.Clear();
        FriendFilters.Add(new TrendOption("Anyone", 0));
        foreach (var f in friends.Where(f => f.MatchIds is { Count: > 0 }))
            FriendFilters.Add(new TrendOption($"with {(string.IsNullOrWhiteSpace(f.Name) ? "a friend" : f.Name)}", (int)f.AccountId));
        HasFriendFilters = FriendFilters.Count > 1;
        _heroFilter = HeroFilters.FirstOrDefault(o => o.Value == hero) ?? HeroFilters[0];
        _friendFilter = FriendFilters.FirstOrDefault(o => o.Value == friend) ?? FriendFilters[0];
        Raise(nameof(HeroFilter));
        Raise(nameof(FriendFilter));
    }

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
    private string _sourceTitle = "", _sourceVerdict = "";
    private bool _hasSources;
    /// <summary>"SOUL SOURCES AGAINST ORACLE · 12 GAMES".</summary>
    public string SourceTitle { get => _sourceTitle; private set => Set(ref _sourceTitle, value); }
    /// <summary>"Troopers cost you the most: 1.2k souls a game behind Oracle."</summary>
    public string SourceVerdict { get => _sourceVerdict; private set => Set(ref _sourceVerdict, value); }
    public bool HasSources { get => _hasSources; private set => Set(ref _hasSources, value); }
    public ObservableCollection<PhaseCardViewModel> SourceCards { get; } = new();
    private string _deathTitle = "", _deathVerdict = "";
    private bool _hasDeaths;
    public string DeathTitle { get => _deathTitle; private set => Set(ref _deathTitle, value); }
    public string DeathVerdict { get => _deathVerdict; private set => Set(ref _deathVerdict, value); }
    public bool HasDeaths { get => _hasDeaths; private set => Set(ref _hasDeaths, value); }
    public ObservableCollection<PhaseCardViewModel> DeathCards { get; } = new();

    // ---- Matchup notes: your own note per enemy hero, edited here (saved to settings), shown on the overlay's lane card ----
    private TrendOption? _noteHero;
    private string _noteText = "";
    private bool _hasNotes;
    /// <summary>Every hero, by name, to write a note on.</summary>
    public ObservableCollection<TrendOption> NoteHeroes { get; } = new();
    public TrendOption? NoteHero { get => _noteHero; set => Set(ref _noteHero, value); }
    /// <summary>The picked hero's note; saved when the box loses focus.</summary>
    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }
    public ObservableCollection<NoteRowViewModel> NoteRows { get; } = new();
    public bool HasNotes { get => _hasNotes; private set => Set(ref _hasNotes, value); }

    /// <summary>
    /// Fills the hero list (every playable hero, the page's and the noted ones too, so it is never empty) and the rows of
    /// written notes; keeps the picked hero, else picks the first noted one.
    /// </summary>
    public void SetNotes(IReadOnlyDictionary<int, string> notes, IEnumerable<int> pageHeroes, AssetCatalog assets)
    {
        int picked = NoteHero?.Value ?? 0;
        var ids = assets.Heroes.Where(h => h.Playable).Select(h => h.Id).Concat(pageHeroes).Concat(notes.Keys).Where(id => id > 0).Distinct()
            .Select(id => new TrendOption(assets.HeroName(id), id)).OrderBy(o => o.Text, StringComparer.OrdinalIgnoreCase).ToList();
        NoteHeroes.Clear();
        foreach (var o in ids) NoteHeroes.Add(o);
        NoteRows.Clear();
        foreach (var (hero, note) in notes.OrderBy(n => assets.HeroName(n.Key), StringComparer.OrdinalIgnoreCase))
            NoteRows.Add(new NoteRowViewModel(hero, assets.HeroName(hero), note));
        HasNotes = NoteRows.Count > 0;
        if (picked == 0) picked = NoteRows.FirstOrDefault()?.HeroId ?? 0;
        _noteHero = NoteHeroes.FirstOrDefault(o => o.Value == picked) ?? NoteHeroes.FirstOrDefault();
        _noteText = _noteHero is { } h ? MatchupNotes.For(notes, h.Value) ?? "" : "";
        Raise(nameof(NoteHero));
        Raise(nameof(NoteText));
    }

    // ---- Goals: your targets, edited here, and how often each was hit over the page's games ----
    private TargetSettings _targets = new();
    private string _targetSpmText = "", _targetDeathsText = "", _targetsVerdict = "";
    private TrendOption? _targetPhase;
    private bool _hasTargetRates;
    /// <summary>The targets as last applied; <see cref="ReadTargets"/> turns the editors into new ones.</summary>
    public TargetSettings Targets => _targets;
    /// <summary>"500": souls a minute by 10:00; empty is off.</summary>
    public string TargetSpmText { get => _targetSpmText; set => Set(ref _targetSpmText, value); }
    /// <summary>"1": the most deaths before 10:00; empty is off.</summary>
    public string TargetDeathsText { get => _targetDeathsText; set => Set(ref _targetDeathsText, value); }
    public ObservableCollection<TrendOption> TargetPhases { get; } = new()
    {
        new TrendOption("Off", -1), new TrendOption(Phases.Name(GamePhase.Laning), (int)GamePhase.Laning),
        new TrendOption(Phases.Name(GamePhase.Mid), (int)GamePhase.Mid), new TrendOption(Phases.Name(GamePhase.Late), (int)GamePhase.Late),
    };
    public TrendOption? TargetPhase { get => _targetPhase; set => Set(ref _targetPhase, value); }
    public ObservableCollection<TargetRowViewModel> TargetRows { get; } = new();
    public bool HasTargetRates { get => _hasTargetRates; private set => Set(ref _hasTargetRates, value); }
    public string TargetsVerdict { get => _targetsVerdict; private set => Set(ref _targetsVerdict, value); }

    /// <summary>Fills the editors from <paramref name="t"/> and checks every game against it.</summary>
    public void SetTargets(TargetSettings t)
    {
        _targets = t;
        TargetSpmText = t.LaningSpm > 0 ? t.LaningSpm.ToString() : "";
        TargetDeathsText = t.MaxLaningDeaths is { } d ? d.ToString() : "";
        TargetPhase = TargetPhases.First(o => o.Value == (t.FixPhase is { } p ? (int)p : -1));
        RefreshTargets();
    }

    /// <summary>The targets the editors say: a blank or unreadable number is off.</summary>
    public TargetSettings ReadTargets() => new()
    {
        LaningSpm = int.TryParse(TargetSpmText.Trim(), out var spm) && spm > 0 ? spm : 0,
        MaxLaningDeaths = int.TryParse(TargetDeathsText.Trim(), out var d) && d >= 0 ? d : null,
        FixPhase = TargetPhase is { Value: >= 0 } o ? (GamePhase)o.Value : null,
    };

    /// <summary>Checks every game against the targets and pools the hit rates (newest first).</summary>
    public void RefreshTargets()
    {
        foreach (var g in Games) g.SetTargets(_targets);
        TargetRows.Clear();
        var rates = Targets.Any ? Core.History.Targets.Rates(Games.Select(g => g.TargetChecks).ToList()) : Array.Empty<TargetRate>();
        foreach (var r in rates) TargetRows.Add(new TargetRowViewModel(r, _targets));
        HasTargetRates = TargetRows.Count > 0;
        var worst = rates.Where(r => r.Games > 0).OrderBy(r => r.Rate).FirstOrDefault();
        TargetsVerdict = !_targets.Any ? "Set a target to have every game marked hit or missed."
            : worst is null ? "None of these games can be checked yet (a phase target needs a rank gate)."
            : worst.Rate >= 0.8 ? "Hitting every target in most games: time to raise one."
            : $"Hardest so far: {Core.History.Targets.Describe(worst.Kind, _targets).ToLowerInvariant()}, hit in {worst.Hits} of {worst.Games} games.";
    }

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
    /// <summary>Per hero over the page: the core item skipped or bought late most often against the rank gate (0.7).</summary>
    public void RefreshBuild(string rank)
    {
        BuildRows.Clear();
        if (rank.Length > 0 && _assets is { } assets)
        {
            foreach (var hero in Games.Where(g => g.HasDetail && g.Purchases.Count > 0).GroupBy(g => g.HeroId).OrderByDescending(h => h.Count()))
            {
                if (BuildReview.Recurring(hero.Key, hero.Select(g => g.BuildResults).ToList()) is not { } r) continue;
                var chip = new ItemChipViewModel(r.ItemId, assets);
                BuildRows.Add(new BuildSuggestionViewModel(chip,
                    $"{assets.HeroName(r.HeroId)}: buy {chip.Name} by {Format.Clock(r.RankAt)} · skipped or late in {r.Times} of {r.Games} games, {rank} buys it then"));
            }
        }
        HasBuildRows = BuildRows.Count > 0;
        BuildPageTitle = $"BUILD CHANGES AGAINST {rank.ToUpperInvariant()} · LAST {Games.Count} GAMES";
    }

    /// <summary>
    /// Your build plans (0.7): checks every card on a planned hero and lists each plan with the page's record with and
    /// without it; fills the hero list for typing a plan by hand.
    /// </summary>
    public void SetPlans(IReadOnlyDictionary<int, List<long>> plans, AssetCatalog assets)
    {
        foreach (var g in Games) g.SetPlan(plans.TryGetValue(g.HeroId, out var p) ? p : null);
        var records = BuildPlans.Records(Games.Select(g => (g.HeroId, g.Entry.IsWin ? true : g.Entry.IsLoss ? false : (bool?)null, g.PlanCheck)))
            .ToDictionary(r => r.HeroId);
        PlanRows.Clear();
        foreach (var (hero, plan) in plans.OrderBy(p => assets.HeroName(p.Key), StringComparer.OrdinalIgnoreCase))
        {
            string record = !records.TryGetValue(hero, out var r) ? "No game on this page to check it against yet"
                : r.FollowedGames == 0 ? $"Not kept to in any of the {r.OtherGames} games here (won {r.OtherWins} of them)"
                : r.OtherGames == 0 ? $"Kept to it in all {r.FollowedGames} games here: won {r.FollowedWins}"
                : $"Kept to it in {r.FollowedGames} of {r.FollowedGames + r.OtherGames} games here: won {r.FollowedWins} of {r.FollowedGames}; " +
                  $"without it won {r.OtherWins} of {r.OtherGames}";
            PlanRows.Add(new PlanRowViewModel(hero, assets.HeroName(hero), plan.Select(id => new ItemChipViewModel(id, assets)).ToList(), record));
        }
        HasPlanRows = PlanRows.Count > 0;
        int picked = PlanHero?.Value ?? 0;
        PlanHeroes.Clear();
        foreach (var o in assets.Heroes.Where(h => h.Playable).Select(h => h.Id).Concat(Games.Select(g => g.HeroId)).Concat(plans.Keys)
                     .Where(id => id > 0).Distinct().Select(id => new TrendOption(assets.HeroName(id), id)).OrderBy(o => o.Text, StringComparer.OrdinalIgnoreCase))
            PlanHeroes.Add(o);
        _planHero = PlanHeroes.FirstOrDefault(o => o.Value == picked) ?? PlanHeroes.FirstOrDefault(o => o.Value == (Games.FirstOrDefault()?.HeroId ?? 0)) ?? PlanHeroes.FirstOrDefault();
        Raise(nameof(PlanHero));
    }

    public ObservableCollection<PlanRowViewModel> PlanRows { get; } = new();
    public ObservableCollection<TrendOption> PlanHeroes { get; } = new();
    private TrendOption? _planHero;
    private bool _hasPlanRows;
    private string _planInput = "";
    public TrendOption? PlanHero { get => _planHero; set => Set(ref _planHero, value); }
    public bool HasPlanRows { get => _hasPlanRows; private set => Set(ref _hasPlanRows, value); }
    /// <summary>A plan typed by hand: item names in order, separated by commas.</summary>
    public string PlanInput { get => _planInput; set => Set(ref _planInput, value); }

    public ObservableCollection<BuildSuggestionViewModel> BuildRows { get; } = new();
    private bool _hasBuildRows;
    private string _buildPageTitle = "";
    public bool HasBuildRows { get => _hasBuildRows; private set => Set(ref _hasBuildRows, value); }
    public string BuildPageTitle { get => _buildPageTitle; private set => Set(ref _buildPageTitle, value); }

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

    /// <summary>Pools every game's deaths (against <paramref name="rank"/> when one is picked); clears the card without death times.</summary>
    public void RefreshDeaths(string rank)
    {
        var games = Games.Where(g => g.Detail?.DeathTimes is not null).Select(g => g.DeathResults).ToList();
        DeathCards.Clear();
        if (games.Count == 0) { HasDeaths = false; DeathVerdict = ""; return; }
        var t = DeathReview.Total(games);
        string n(int count) => $"{count} {(count == 1 ? "game" : "games")}";
        DeathCards.Add(PhaseCardViewModel.Stat("DEATHS", $"{t.DeathsPerGame:0.0} a game", $"dead {Format.Clock(t.DeadPerGame)} a game",
            $"{t.Deaths} deaths", n(t.Games), false));
        DeathCards.Add(PhaseCardViewModel.Stat("WHILE DEAD",
            t.GateGainedPerGame is { } g ? Format.Count((int)Math.Round(g)) : "–",
            t.GateGainedPerGame is null ? "pick a rank gate" : $"souls a game the {rank} gate",
            t.GateGainedPerGame is null ? "" : "gained while you waited", n(t.WithGate > 0 ? t.WithGate : t.Games), false));
        bool stash = t.WithUnspent > 0 && t.BigStashShare >= DeathReview.BigStashCallout;
        DeathCards.Add(PhaseCardViewModel.Stat("UNSPENT AT DEATH", t.AverageUnspent is { } u ? $"~{Format.Count((int)Math.Round(u))}" : "–",
            "souls on average", $"{t.BigStash} of {t.WithUnspent} with {Format.Count(DeathReview.BigStash)}+", n(t.Games), stash));
        DeathTitle = $"DEATHS · {n(t.Games).ToUpperInvariant()}";
        DeathVerdict = t.Deaths == 0 ? "No deaths on this page."
            : stash ? $"Spend before you fight: {t.BigStash} of {t.WithUnspent} deaths came with {Format.Count(DeathReview.BigStash)} or more souls unspent."
            : t.GateGainedPerGame is { } lost ? $"Each game you spent {Format.Clock(t.DeadPerGame)} dead, while the {rank} gate gained {Format.Count((int)Math.Round(lost))} souls."
            : $"Each game you spent {Format.Clock(t.DeadPerGame)} dead. Pick a rank gate to see the souls it gained meanwhile.";
        HasDeaths = true;
    }

    /// <summary>Pools every game's souls by source against <paramref name="rank"/>; clears the card when there are none.</summary>
    public void RefreshSources(string rank)
    {
        var games = Games.Select(g => g.SourceResults).Where(r => r.Count > 0).ToList();
        SourceCards.Clear();
        if (rank.Length == 0 || games.Count == 0) { HasSources = false; SourceVerdict = ""; return; }
        var totals = SoulSourceStats.Total(games);
        var costliest = SoulSourceStats.Costliest(totals);
        foreach (var t in totals) SourceCards.Add(PhaseCardViewModel.Source(t, rank, ReferenceEquals(t, costliest)));
        SourceTitle = $"SOUL SOURCES AGAINST {rank.ToUpperInvariant()} · {games.Count} {(games.Count == 1 ? "GAME" : "GAMES")}";
        SourceVerdict = costliest is { } c
            ? $"{SoulSourceStats.Name(c.Source)} cost you the most: {Format.Count((int)Math.Round(c.ShortfallPerGame))} souls a game behind {rank}" +
              $" ({c.Spm:0}/min against {c.ExpectedSpm:0}/min)."
            : totals.All(t => t.Delta is >= 0)
                ? $"Level with or ahead of {rank} from every source."
                : $"Not enough games to name the source that costs you the most yet.";
        HasSources = true;
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

        LaneRows.Clear();
        var lanes = HistoryMath.LaneRecords(page.Games.Where(g => g.Detail is not null)
            .Select(g => (g.Entry.HeroId, HistoryMath.Lane(g.Detail!, g.Entry.Duration))));
        foreach (var r in lanes)
            LaneRows.Add(new HeroUsageViewModel(r.HeroId, assets.HeroName(r.HeroId),
                $"lane won {r.Won} of {r.Games} · {(r.AverageDiff >= 0 ? "+" : "−")}{Format.Count((int)Math.Round(Math.Abs(r.AverageDiff)))} souls at 10:00 on average"));
        HasLaneRows = LaneRows.Count > 0;
        LaneTitle = $"LANING · LAST {page.Games.Count} GAMES";

        MainHeroes.Clear();
        foreach (var h in s.TopHeroes)
            MainHeroes.Add(new HeroUsageViewModel(h.HeroId, assets.HeroName(h.HeroId), $"{h.Games} {(h.Games == 1 ? "game" : "games")} · {Format.Pct(h.WinRate)}"));
        LastResults.Clear();
        foreach (var r in s.LastResults) LastResults.Add(r is true ? WinBrush : r is false ? LossBrush : NeutralBrush);

        Trends.Load(page.History ?? page.Games.Select(g => g.Entry).ToList(), assets, icons, now);
        Games.Clear();
        foreach (var g in page.Games) Games.Add(new GameCardViewModel(g, assets, now, Marks(g.Entry.MatchId)));
        if (Games.Count > 0) Games[0].IsExpanded = true;
        Processing.Clear();
        foreach (var p in page.Processing ?? Array.Empty<PlayedMatch>()) Processing.Add(new ProcessingCardViewModel(p, assets, now));
        RefreshIcons(icons);

        HasData = s.Games > 0 || Processing.Count > 0;
        Status = HasData ? "" : "No games yet. Matches show here once the API has your match history.";
    }

    /// <summary>Picks up icons that have loaded since the last call. False once nothing is waiting any more.</summary>
    public bool RefreshIcons(ItemIcons? icons)
    {
        if (icons is null) return false;
        bool waiting = false;
        foreach (var h in MainHeroes) waiting |= h.Refresh(icons);
        foreach (var h in LaneRows) waiting |= h.Refresh(icons);
        foreach (var g in Games) waiting |= g.RefreshIcons(icons);
        foreach (var p in Processing) waiting |= p.Hero.Refresh(icons);
        foreach (var r in BuildRows) waiting |= r.Chip.Refresh(icons);
        foreach (var r in PlanRows) foreach (var c in r.Items) waiting |= c.Refresh(icons);
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
        Fill = Converters.SpikeFill(item?.Slot, 0x59); // strong enough to read under a white icon
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
    private AssetCatalog? _momentAssets;
    private TimeSpan? _pickedTime;
    private TimelineMoment? _moment;

    /// <summary>The moment picked on the soul timeline (0.6); setting it fills the bubble the chart draws at the line.</summary>
    public TimeSpan? PickedTime
    {
        get => _pickedTime;
        set
        {
            if (!Set(ref _pickedTime, value)) return;
            if (value is not { } t || Detail is not { } d || _momentAssets is not { } assets) { Moment = null; return; }
            var m = HistoryMath.Moment(d, t);
            TimelineMomentRow Row(MomentWorth p) => new(assets.HeroName(p.HeroId), Format.Count(p.NetWorth), p.IsMe);
            int gap = m.TeamTotal - m.EnemyTotal;
            Moment = new TimelineMoment(
                $"{(int)t.TotalMinutes}:{t.Seconds:00} · your team {Format.Count(m.TeamTotal)} vs {Format.Count(m.EnemyTotal)}",
                $"({(gap >= 0 ? "+" : "−")}{Format.Count(Math.Abs(gap))})", gap >= 0,
                m.Team.Select(Row).ToList(), m.Enemies.Select(Row).ToList(),
                m.Missing > 0 ? $"{m.Missing} without a timeline: Refresh to load them" : null);
        }
    }
    /// <summary>Every player's net worth at <see cref="PickedTime"/>, for the chart's bubble; null when nothing is picked.</summary>
    public TimelineMoment? Moment { get => _moment; private set => Set(ref _moment, value); }
    private static Brush WinEdge => Themes.Palette.Good;
    private static Brush LossEdge => Themes.Palette.Bad;
    private static readonly Brush OtherEdge = Frozen(Color.FromRgb(0x5A, 0x63, 0x70));

    private readonly HeroIcon _hero;
    private readonly int _netWorth;
    private bool _expanded;
    private IReadOnlyList<NetWorthPoint> _expected = Array.Empty<NetWorthPoint>();
    private string _expectedLabel = "", _goalText = "";
    private double? _goalDelta;
    private IReadOnlyList<TimelineMarker> _markers = Array.Empty<TimelineMarker>();

    public GameCardViewModel(HistoryGame g, AssetCatalog assets, DateTimeOffset now, IReadOnlyList<TimeSpan>? marks = null)
    {
        _marks = marks ?? Array.Empty<TimeSpan>();
        _cost = assets.ItemCost;
        _buildAssets = assets;
        var e = g.Entry;
        Entry = e;
        HeroName = assets.HeroName(e.HeroId);
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
            _momentAssets = assets;
            if (HistoryMath.Lane(d, e.Duration) is { } lane)
            {
                LaneMine = lane.Mine;
                LaneTheirs = lane.Opponents;
                var names = string.Join(" and ", lane.OpponentHeroes.Select(assets.HeroName));
                LaneLabel = lane.OpponentHeroes.Count > 1 ? "their average" : assets.HeroName(lane.OpponentHeroes[0]);
                string Diff(int minute) => lane.DiffAt(TimeSpan.FromMinutes(minute)) is { } v
                    ? $"{(v >= 0 ? "+" : "−")}{Format.Count(Math.Abs(v))} at {minute}:00" : "";
                var diffs = string.Join(", ", new[] { Diff(5), Diff(10) }.Where(x => x.Length > 0));
                LaneText = $"Lane against {names}" + (diffs.Length > 0 ? $": {diffs}" : "") +
                           lane.Won switch { true => " · lane won", false => " · lane lost", _ => "" };
            }
            TeamWorth = HistoryMath.TeamWorth(d, e.Duration);
            if (TeamWorth.Count > 1)
            {
                Swing = HistoryMath.BiggestSwing(TeamWorth, SwingWindow);
                TeamWorthText = TeamLine(TeamWorth, Swing);
            }
            if (d.Sources is { } src && e.Duration >= SoulSourceStats.MinDuration)
            {
                var split = SoulSourceStats.Split(src, e.NetWorth);
                SourcesText = "Souls from: " + string.Join(" · ", SoulSourceStats.All.Select(x => $"{SoulSourceStats.Name(x).ToLowerInvariant()} {Format.Count(split[x])}")) +
                              (src.LostOnDeath > 0 ? $" · {Format.Count(src.LostOnDeath)} lost on death" : "");
            }
            foreach (var enemy in d.Enemies.Where(p => p.HeroId > 0).Select(p => p.HeroId).Distinct())
                Counters.Add(new CounterRowViewModel(enemy, assets.HeroName(enemy)));
        }
        else
        {
            LevelText = LastHitsText = TeamRankText = BadgeText = "";
            Souls = Array.Empty<NetWorthPoint>();
        }
        SetDeaths(null, "");
        BuildMarkers();
    }

    /// <summary>Your souls in laning (0:00–10:00) and your lane opponents' average (0.7); empty without lane data.</summary>
    public IReadOnlyList<NetWorthPoint> LaneMine { get; } = Array.Empty<NetWorthPoint>();
    public IReadOnlyList<NetWorthPoint> LaneTheirs { get; } = Array.Empty<NetWorthPoint>();
    public bool HasLane => LaneMine.Count > 1;
    /// <summary>The opponents' line's label: their hero, or "their average" for two.</summary>
    public string LaneLabel { get; } = "";
    /// <summary>"Lane against Lady Geist and Paradox: +500 at 5:00, +2k at 10:00 · lane won".</summary>
    public string LaneText { get; } = "";

    /// <summary>How far apart the two ends of the team graph's biggest swing may be.</summary>
    private static readonly TimeSpan SwingWindow = TimeSpan.FromMinutes(3);

    /// <summary>Both teams' total souls through the match (0.7); empty when a player's series is missing.</summary>
    public IReadOnlyList<TeamWorthPoint> TeamWorth { get; } = Array.Empty<TeamWorthPoint>();
    public bool HasTeamWorth => TeamWorth.Count > 1;
    /// <summary>The steepest change in the gap, shown as a band on the team graph.</summary>
    public TeamSwing? Swing { get; }
    /// <summary>"Ahead for 62% of the game · the game turned 12:00–15:00: −12k in 3 minutes".</summary>
    public string TeamWorthText { get; } = "";

    private static string TeamLine(IReadOnlyList<TeamWorthPoint> points, TeamSwing? swing)
    {
        static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        // After the first two minutes, when both teams sit at almost nothing.
        var played = points.Where(p => p.At >= TimeSpan.FromMinutes(2)).ToList();
        int ahead = played.Count == 0 ? 0 : (int)Math.Round(100.0 * played.Count(p => p.Gap > 0) / played.Count);
        var line = $"Your team ahead for {ahead}% of the game";
        if (swing is null) return line + " · no big swing";
        int minutes = (int)Math.Round((swing.To - swing.From).TotalMinutes);
        return line + $" · biggest swing {Clock(swing.From)}–{Clock(swing.To)}: " +
               $"{(swing.Change >= 0 ? "+" : "−")}{Format.Count(Math.Abs(swing.Change))} in {minutes} minute{(minutes == 1 ? "" : "s")} " +
               (swing.Change >= 0 ? "for your team" : "for the enemy");
    }

    public long MatchId { get; }
    public string ResultText { get; }
    /// <summary>The history row this card shows, for the CSV export.</summary>
    public MatchHistoryEntry Entry { get; }
    public string HeroName { get; }
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

    /// <summary>
    /// What to change in this game's build against the rank gate (0.7): items its players buy that you skipped or bought
    /// late, and the ones they rarely buy. From the item timings and stats the page already loads; post-match only.
    /// </summary>
    public void SetBuildReview(IReadOnlyDictionary<long, ItemTiming>? table, IReadOnlyList<ItemBucketStat>? stats, string rank)
    {
        BuildSuggestions.Clear();
        BuildResults = table is null || rank.Length == 0 || _buildAssets is null
            ? Array.Empty<BuildSuggestion>()
            : BuildReview.Review(Purchases.Select(p => p.Item), Duration, table, stats, _cost);
        foreach (var s in BuildResults) BuildSuggestions.Add(new BuildSuggestionViewModel(s, _buildAssets!, rank, HeroName));
        BuildTitle = $"BUILD AGAINST {rank.ToUpperInvariant()}";
        Raise(nameof(HasBuild));
    }

    /// <summary>How this game kept to your plan for its hero (0.7); empty without a plan.</summary>
    public void SetPlan(IReadOnlyList<long>? plan)
    {
        PlanCheck = BuildPlans.Check(plan, Purchases.Select(p => p.Item), _cost);
        PlanText = PlanCheck is not { } c ? ""
            : $"Your {HeroName} plan: kept {c.Kept} of {c.Considered} items" +
              (c.OffAt is { } off && _buildAssets is { } a ? $" · off plan at {a.ItemName(off)}" : " · kept to it");
        PlanFollowed = PlanCheck?.Followed;
    }

    private string _planText = "";
    private bool? _planFollowed;
    public PlanCheck? PlanCheck { get; private set; }
    public string PlanText { get => _planText; private set => Set(ref _planText, value); }
    /// <summary>For the plan line's colour: followed, not followed, or no plan.</summary>
    public bool? PlanFollowed { get => _planFollowed; private set => Set(ref _planFollowed, value); }
    /// <summary>"Use this build as my Haze plan".</summary>
    public string UsePlanText => $"Use this build as my {HeroName} plan";
    public bool CanUsePlan => HasDetail && Purchases.Count > 0;

    private AssetCatalog? _buildAssets;
    private string _buildTitle = "";
    /// <summary>The suggestions as computed, for the page's recurring change.</summary>
    public IReadOnlyList<BuildSuggestion> BuildResults { get; private set; } = Array.Empty<BuildSuggestion>();
    public ObservableCollection<BuildSuggestionViewModel> BuildSuggestions { get; } = new();
    public bool HasBuild => BuildSuggestions.Count > 0;
    public string BuildTitle { get => _buildTitle; private set => Set(ref _buildTitle, value); }

    private static Brush DeathColor => Themes.Palette.Bad;
    private static Brush DeathFill => Frozen(Color.FromArgb(0x30, Themes.Palette.BadColor.R, Themes.Palette.BadColor.G, Themes.Palette.BadColor.B));
    private readonly Func<long, int> _cost;
    private string _deathsText = "";
    /// <summary>"4 deaths · dead 1:35 · the Oracle gate gained 2.3k meanwhile"; empty without death times.</summary>
    public string DeathsText { get => _deathsText; private set => Set(ref _deathsText, value); }
    /// <summary>Each death against your purchases (souls unspent) and the gate (souls it gained meanwhile).</summary>
    public IReadOnlyList<DeathResult> DeathResults { get; private set; } = Array.Empty<DeathResult>();
    public ObservableCollection<PhaseChipViewModel> DeathChips { get; } = new();

    private TargetSettings _targets = new();
    /// <summary>This game against your targets; recomputed when the targets or the rank gate change.</summary>
    public IReadOnlyList<TargetCheck> TargetChecks { get; private set; } = Array.Empty<TargetCheck>();
    public ObservableCollection<PhaseChipViewModel> TargetChips { get; } = new();

    public void SetTargets(TargetSettings t)
    {
        _targets = t;
        TargetChips.Clear();
        TargetChecks = t.Any ? Core.History.Targets.Check(t, Souls, Duration, Detail?.DeathTimes, PhaseResults) : Array.Empty<TargetCheck>();
        foreach (var c in TargetChecks) TargetChips.Add(PhaseChipViewModel.Target(c, t));
    }

    /// <summary>Reads the deaths against <paramref name="gate"/> (null: no rank picked, unspent souls only).</summary>
    private void SetDeaths(PaceCurve? gate, string rank)
    {
        DeathChips.Clear();
        DeathResults = Detail is { DeathTimes: { } times }
            ? DeathReview.Of(times, Souls, Detail.Purchases ?? Array.Empty<MatchItem>(), _cost, gate)
            : Array.Empty<DeathResult>();
        if (Detail?.DeathTimes is null) { DeathsText = ""; return; }
        var dead = TimeSpan.FromTicks(DeathResults.Sum(d => d.Duration.Ticks));
        DeathsText = DeathResults.Count == 0 ? "No deaths."
            : $"{DeathResults.Count} {(DeathResults.Count == 1 ? "death" : "deaths")} · dead {Format.Clock(dead)}" +
              (gate is not null && rank.Length > 0 ? $" · the {rank} gate gained {Format.Count((int)Math.Round(DeathResults.Sum(d => d.GateGained ?? 0)))} meanwhile" : "");
        foreach (var d in DeathResults) DeathChips.Add(PhaseChipViewModel.Death(d, rank));
        BuildMarkers();
    }

    /// <summary>"Souls from: troopers 4.7k · jungle 1.9k · kills 2.4k · objectives 2.3k · other 8.2k · 206 lost on death".</summary>
    public string SourcesText { get; } = "";
    /// <summary>This game's souls by source against the gate's rank; empty without one.</summary>
    public IReadOnlyList<SourceResult> SourceResults { get; private set; } = Array.Empty<SourceResult>();
    public ObservableCollection<PhaseChipViewModel> SourceChips { get; } = new();

    /// <summary>Sets this game's sources against <paramref name="rates"/> (the rank's on this hero); null clears them.</summary>
    public void SetSources(SourceRates? rates, string rank)
    {
        SourceChips.Clear();
        SourceResults = rank.Length == 0 ? Array.Empty<SourceResult>() : SoulSourceStats.Of(Detail?.Sources, _netWorth, Duration, rates);
        foreach (var r in SourceResults) SourceChips.Add(PhaseChipViewModel.Source(r, rank));
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
        SetDeaths(curve, rank);
        PhaseChips.Clear();
        PhaseResults = curve is null || rank.Length == 0 ? Array.Empty<PhaseResult>() : Phases.Of(Souls, Duration, curve);
        foreach (var p in PhaseResults) PhaseChips.Add(new PhaseChipViewModel(p, rank));
        SetTargets(_targets);
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

    private readonly IReadOnlyList<TimeSpan> _marks;
    private static readonly Brush MarkColor = Frozen(Color.FromRgb(0xE8, 0xB0, 0x4B)), MarkFill = Frozen(Color.FromArgb(0x40, 0xE8, 0xB0, 0x4B));

    /// <summary>"Your mark at 14:20: 9.2k souls, 610 a minute over the 2 minutes before": a mark's tooltip, from the soul timeline.</summary>
    private string MarkTip(TimeSpan at)
    {
        int now = HistoryMath.SoulsAt(Souls, at) ?? 0;
        var from = at - TimeSpan.FromMinutes(2) < TimeSpan.Zero ? TimeSpan.Zero : at - TimeSpan.FromMinutes(2);
        double minutes = (at - from).TotalMinutes;
        string rate = minutes > 0 ? $", {(now - (HistoryMath.SoulsAt(Souls, from) ?? 0)) / minutes:0} a minute over the {minutes:0.#} min before" : "";
        return $"{Format.Clock(at)}  your mark: {Format.Count(now)} souls{rate}";
    }

    private void BuildMarkers()
        => Markers = _marks.Where(m => m <= Duration).Select(m => new TimelineMarker(m, HistoryMath.SoulsAt(Souls, m) ?? 0, "★", MarkColor, MarkFill, null, MarkTip(m)))
            .Concat(Purchases.Select(p => new TimelineMarker(p.At, HistoryMath.SoulsAt(Souls, p.At) ?? 0, p.Chip.Initial, p.Chip.Color, p.Chip.Fill,
                p.Chip.Icon, $"{p.TimeText}  {p.Chip.Name}" + (p.Chip.Cost > 0 ? $" ({p.Chip.Cost:N0})" : "") + (p.Sold ? $" · {p.SoldText}" : ""))))
            // Deaths as red crosses on the line, among the purchases by time.
            .Concat(DeathResults.Select(d => new TimelineMarker(d.At, HistoryMath.SoulsAt(Souls, d.At) ?? 0, "✕", DeathColor, DeathFill, null,
                $"{Format.Clock(d.At)}  died, dead {d.Duration.TotalSeconds:0} s" + (d.Unspent is { } u ? $" · ~{Format.Count(u)} unspent" : ""))))
            .OrderBy(m => m.At).ToList();

    private static PlayerLineViewModel Line(HistoryPlayer p, AssetCatalog assets)
        => new(assets.HeroName(p.HeroId), $"{p.Kills}/{p.Deaths}/{p.Assists}", $"{p.NetWorth:N0} souls", p.IsMe);

    public static string Ago(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)} min ago"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours} h ago"
        : (int)t.TotalDays == 1 ? "1 day ago" : $"{(int)t.TotalDays} days ago";

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>
/// A match you just played that the API has not stored yet: hero, when, roughly how long (from the console log, so
/// counting the loading screen), and why there is nothing more yet. It becomes a full card once the API has it.
/// </summary>
public sealed class ProcessingCardViewModel
{
    public ProcessingCardViewModel(PlayedMatch p, AssetCatalog assets, DateTimeOffset now)
    {
        MatchId = p.MatchId;
        Hero = new HeroIcon(p.HeroId ?? 0, p.HeroId is { } h ? assets.HeroName(h) : "Unknown hero");
        TimeAgo = p.Added ? "added " + GameCardViewModel.Ago(now - p.EndedAt) : GameCardViewModel.Ago(now - p.EndedAt);
        DurationText = p.Added ? "by id" : $"~{Format.Clock(p.EndedAt - p.StartedAt)}";
        Note = p.Added
            ? "Added by id: deadlock-api was asked to fetch it from Valve (and will be again in an hour). It becomes a game here once deadlock-api has it, if you were in it."
            : now - p.EndedAt < TimeSpan.FromHours(4)
            ? "deadlock-api has not stored this match yet. To hurry it: open the match in Deadlock's in-game match history, then Send my matches to deadlock-api."
            : "deadlock-api has not stored this match yet. Open it in Deadlock's in-game match history, then Send my matches to deadlock-api; without that it may never come.";
    }
    public long MatchId { get; }
    public HeroIcon Hero { get; }
    public string TimeAgo { get; }
    public string DurationText { get; }
    public string Note { get; }
    public string MatchText => $"match {MatchId}";
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
    private PhaseChipViewModel(string label, double? delta, string tooltip)
    {
        Label = label;
        Delta = delta;
        Value = delta is { } d ? Format.Signed(d) : "–";
        Tooltip = tooltip;
    }

    private PhaseChipViewModel(string label, string value, double? delta, string tooltip)
    {
        Label = label;
        Value = value;
        Delta = delta;
        Tooltip = tooltip;
    }

    /// <summary>A target in one game: green when hit, red when missed, plain when it could not be checked.</summary>
    public static PhaseChipViewModel Target(TargetCheck c, TargetSettings t) => new(
        Core.History.Targets.Short(c.Kind, t), (c.Hit switch { true => "✓ ", false => "✗ ", _ => "" }) + c.Value,
        c.Hit switch { true => 1, false => -1, _ => null },
        Core.History.Targets.Describe(c.Kind, t) + c.Hit switch
        {
            true => ": hit", false => ": missed",
            _ => c.Kind == TargetKind.Phase ? ": pick a rank gate to check it" : ": this game does not reach 10:00 or has no timeline",
        });

    /// <summary>A death: when, and the souls carried unspent; red when they would have bought a big item.</summary>
    public static PhaseChipViewModel Death(DeathResult d, string rank) => new(
        Format.Clock(d.At), d.Unspent is { } u ? $"~{Format.Count(u)} unspent" : $"{d.Duration.TotalSeconds:0} s",
        d.Unspent >= DeathReview.BigStash ? -1 : null,
        $"Died at {Format.Clock(d.At)}, dead {d.Duration.TotalSeconds:0} s" +
        (d.Unspent is { } x ? $" · about {x:N0} souls unspent (net worth then, less the items held)" : "") +
        (d.GateGained is { } g && rank.Length > 0 ? $" · the {rank} gate gained {g:N0} meanwhile" : ""));

    /// <summary>A source in one game against the rank, in the same chip.</summary>
    public static PhaseChipViewModel Source(SourceResult r, string rank) => new(
        SoulSourceStats.Name(r.Source).ToUpperInvariant(), r.Delta,
        $"{SoulSourceStats.Name(r.Source)} ({SoulSourceStats.Holds(r.Source)}): you {r.Spm:0}/min, {rank} {r.ExpectedSpm:0}/min · " +
        $"{Format.SignedCount((int)Math.Round(r.Gained - r.Expected))} souls");

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
    private PhaseCardViewModel(string name, string span, double? delta, string rates, double shortfallPerGame, int games, bool weakest, string? tooltip)
    {
        Name = name;
        Span = span;
        Delta = delta;
        DeltaText = delta is { } d ? Format.Signed(d) : "–";
        Rates = rates;
        int perGame = (int)Math.Round(shortfallPerGame);
        Souls = perGame > 0 ? $"{Format.Count(perGame)} souls a game behind" : $"{Format.Count(-perGame)} souls a game ahead";
        Games = $"{games} {(games == 1 ? "game" : "games")}";
        IsWeakest = weakest;
        Tooltip = tooltip;
    }

    private PhaseCardViewModel(string name, string big, string line1, string line2, string line3, bool outlined)
    {
        Name = name;
        Span = "";
        DeltaText = big;
        Rates = line1;
        Souls = line2;
        Games = line3;
        IsWeakest = outlined;
    }

    /// <summary>A plain figure in the same card (no delta colour): the deaths card's tiles.</summary>
    public static PhaseCardViewModel Stat(string name, string big, string line1, string line2, string line3, bool outlined)
        => new(name, big, line1, line2, line3, outlined);

    /// <summary>A source over the page's games, in the same card; the costliest is outlined.</summary>
    public static PhaseCardViewModel Source(SourceTotal t, string rank, bool costliest) => new(
        // Two lines: five cards in a row are too narrow for both rates on one, which broke after the slash.
        SoulSourceStats.Name(t.Source).ToUpperInvariant(), "", t.Delta, $"you {t.Spm:0}/min\n{rank} {t.ExpectedSpm:0}/min",
        t.ShortfallPerGame, t.Games, costliest, $"{SoulSourceStats.Name(t.Source)}: {SoulSourceStats.Holds(t.Source)}");

    public string Name { get; }
    public string Span { get; }
    public double? Delta { get; }
    public string DeltaText { get; }
    public string Rates { get; }
    public string Souls { get; }
    public string Games { get; }
    public bool IsWeakest { get; }
    public string? Tooltip { get; }
}

/// <summary>One target over the page: what it is, how often it was hit, and a dot per game (newest first).</summary>
public sealed class TargetRowViewModel
{
    private static Brush Hit => Themes.Palette.Good;
    private static Brush Miss => Themes.Palette.Bad;
    private static readonly Brush Unknown = Frozen(Color.FromRgb(0x3A, 0x43, 0x4E));

    public TargetRowViewModel(TargetRate r, TargetSettings t)
    {
        Name = Targets.Describe(r.Kind, t);
        Rate = r.Rate;
        RateText = r.Games == 0 ? "not checked yet" : $"{r.Hits} of {r.Games} · {Format.Pct(r.Rate!.Value)}";
        Dots = r.Results.Select(x => x switch { true => Hit, false => Miss, _ => Unknown }).ToList();
    }

    public string Name { get; }
    public double? Rate { get; }
    public string RateText { get; }
    public IReadOnlyList<Brush> Dots { get; }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>One written matchup note: "Lady Geist  Poke her early; her lifesteal wins long trades".</summary>
public sealed record NoteRowViewModel(int HeroId, string Hero, string Note);

/// <summary>One build suggestion: the item's square and what to change ("Spirit Shielding: skipped · Oracle buys it at 15:00, +4.1 points").</summary>
public sealed class BuildSuggestionViewModel
{
    public BuildSuggestionViewModel(ItemChipViewModel chip, string text) { Chip = chip; Text = text; }

    public BuildSuggestionViewModel(BuildSuggestion s, AssetCatalog assets, string rank, string hero)
    {
        Chip = new ItemChipViewModel(s.ItemId, assets);
        string lift = s.Lift is { } l ? $", {(l >= 0 ? "+" : "−")}{Math.Abs(l * 100):0.0} points of win rate" : "";
        Text = s.Kind switch
        {
            BuildAdvice.Skipped => $"{Chip.Name}: skipped · {rank} buys it at {Format.Clock(s.RankAt)} in {(s.Share >= 0.7 ? "most" : "many")} games{lift}",
            BuildAdvice.Late => $"{Chip.Name}: bought at {Format.Clock(s.YouAt!.Value)}, {rank} at {Format.Clock(s.RankAt)} (+{Format.Clock(s.YouAt.Value - s.RankAt)}){lift}",
            _ => $"{Chip.Name}: {rank} rarely buys it on {hero}{lift}",
        };
        Good = s.Kind == BuildAdvice.Rare && s.Lift is > 0;
    }

    public ItemChipViewModel Chip { get; }
    public string Text { get; }
    /// <summary>A rare item that wins more than usual: a find, not a mistake.</summary>
    public bool Good { get; }
}

/// <summary>One build plan on the page: the hero, its items in order and the page's record with and without it.</summary>
public sealed record PlanRowViewModel(int HeroId, string Hero, IReadOnlyList<ItemChipViewModel> Items, string Record);

